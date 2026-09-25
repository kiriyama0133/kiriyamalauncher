using kiriyamalauncher.Data.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 基于「嵌入的 ZeroTier 客户端」的实现（方案 A：官方 MSI 静默安装 + 命令行控制）。
///
/// 与 <see cref="ZeroTierService"/>（libzt 内嵌节点，不需要虚拟网卡）不同，
/// 这个后端驱动系统里真正安装的 ZeroTier One 服务：
///   - 首次需要静默安装官方 ZeroTierOne.msi（本程序以管理员身份运行，见 app.require-admin.manifest）；
///   - 服务会创建一块真正的「ZeroTier One 虚拟网卡」，游戏进程直接通过系统网卡收发流量，
///     因此不再需要 civ6hook 注入组件；
///   - 命令通过 <c>zerotier-one_x64.exe -q &lt;command&gt;</c> 执行（Windows 上 -q 即 zerotier-cli 的等价物）。
///
/// Windows 上 ZeroTier One 的关键路径与命令（官方文档）：
///   - 服务程序：C:\ProgramData\ZeroTier\One\zerotier-one_x64.exe
///   - 服务名：ZeroTierOneService
///   - 加入网络：zerotier-one_x64.exe -q join &lt;16位网络ID&gt;
///   - 离开网络：zerotier-one_x64.exe -q leave &lt;16位网络ID&gt;
///   - 列出网络：zerotier-one_x64.exe -q listnetworks [-j]
///   - 节点信息：zerotier-one_x64.exe -q info
///   - 绕月：zerotier-one_x64.exe -q orbit &lt;moonID&gt; &lt;moonID&gt;
/// </summary>
public class ZeroTierClientBackend : IZeroTierBackend
{
    /// <summary>ZeroTier One 服务的默认安装目录。</summary>
    private static readonly string ServiceDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One");

    private const string ServiceExecutableName = "zerotier-one_x64.exe";

    private static readonly string ServiceExecutable = Path.Combine(ServiceDirectory, ServiceExecutableName);

    /// <summary>ZeroTier One 的 Windows 服务名（MSI 注册的服务）。</summary>
    private const string ServiceName = "ZeroTierOneService";

    /// <summary>
    /// 按优先级返回第一个实际存在的 zerotier-one 可执行文件；都不存在时返回 null。
    /// 数据目录（ProgramData）的副本来自服务自更新残留，MSI 默认装到 Program Files (x86)。
    /// </summary>
    private static string? FindServiceExecutable()
    {
        if (File.Exists(ServiceExecutable))
        {
            return ServiceExecutable;
        }

        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            string candidate = Path.Combine(programFilesX86, "ZeroTier", "One", "zerotier-one_x64.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string candidate = Path.Combine(programFiles, "ZeroTier", "One", "zerotier-one_x64.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>随应用一起发行的官方安装包（发布目录下的 tools/zerotier/ZeroTierOne.msi）。</summary>
    private static readonly string BundledInstaller = Path.Combine(
        AppContext.BaseDirectory, "tools", "zerotier", "ZeroTierOne.msi");

    /// <summary>加入网络后等待虚拟 IP 的最长时间（含控制面失联后的自动恢复时间，正常成功只需几秒）。</summary>
    private static readonly TimeSpan JOIN_TIMEOUT = TimeSpan.FromSeconds(60);

    /// <summary>状态快照的后台刷新间隔。</summary>
    private static readonly TimeSpan REFRESH_INTERVAL = TimeSpan.FromSeconds(2);

    /// <summary>单条 zerotier-one 命令的最长执行时间（CLI 偶发挂起时绝不能拖死调用方）。</summary>
    private static readonly TimeSpan COMMAND_TIMEOUT = TimeSpan.FromSeconds(10);

    /// <summary>等待系统服务启动的最长时间。</summary>
    private static readonly TimeSpan SERVICE_START_TIMEOUT = TimeSpan.FromSeconds(30);

    private readonly ILogger<ZeroTierClientBackend> _logger;
    private readonly IRouteMetricService _routeMetric;
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    /// <summary>
    /// CLI 命令全局串行闸门：ZeroTier 服务的控制接口一次只处理一个连接，
    /// 并发的 -q 进程会互相排队饿死（实测 join 期间 join 轮询与后台刷新两组 CLI 并发，
    /// 约一半命令 10 秒超时，另一半成功把失败计数清零 —— 表现为「连续 1 次」反复出现）。
    /// 注意它与 <see cref="_commandLock"/> 是两把锁：_commandLock 保护 Start/Join/Leave
    /// 的流程级互斥（会长时间持有），本闸门只覆盖每一次子进程调用，持有期间绝不嵌套等待。
    /// </summary>
    private readonly SemaphoreSlim _cliGate = new(1, 1);

    private readonly List<ulong> _joinedNetworkIds = [];
    private ulong _currentNetworkId;

    // —— 状态快照：由后台刷新循环维护，同步成员只读缓存（绝不跑子进程、绝不阻塞）——
    private NetworkSnapshot _snapshot = NetworkSnapshot.Empty;
    private int _refreshStarted;
    private CancellationTokenSource? _refreshCts;

    // —— 控制面失联自愈 ——
    // 实测 join 可能触发服务停止响应（僵死甚至意外停止），之后所有 -q 命令超时；
    // 唯一可靠的解锁方式是重启服务（服务会按 networks.d 自动重连已加入的网络）。
    /// <summary>两次控制面自愈之间的最小间隔。</summary>
    private static readonly TimeSpan HEAL_COOLDOWN = TimeSpan.FromSeconds(30);

    /// <summary>后台刷新连续失败次数（成功清零；≥2 视为控制面失联）。</summary>
    private int _consecutiveRefreshFailures;

    /// <summary>自愈互斥（join 流程与后台刷新共用同一恢复入口）。</summary>
    private readonly SemaphoreSlim _healGate = new(1, 1);

    /// <summary>上次自愈时间（UTC，用于冷却）。</summary>
    private DateTime _lastHealAtUtc = DateTime.MinValue;

    public ZeroTierClientBackend(ILogger<ZeroTierClientBackend> logger, IRouteMetricService routeMetric)
    {
        _logger = logger;
        _routeMetric = routeMetric;
    }

    /// <inheritdoc />
    public string Kind => ZeroTierSettings.ClientBackend;

    /// <inheritdoc />
    public string ConnectionMode { get; set; } = ZeroTierSettings.OfficialController;

    public event EventHandler<string>? EventRaised;

    /// <inheritdoc />
    public bool IsStarted => FindServiceExecutable() is not null;

    /// <summary>
    /// 构造一个「启动/连接失败」的状态：记录结构化日志、播报事件，并返回携带错误码的失败状态。
    /// 统一失败出口，保证所有失败点都带错误码与分类，而非只有一句话。
    /// </summary>
    private ZeroTierStatus Fail(ZeroTierError error)
    {
        _logger.LogWarning(error.ToLogText());
        EventRaised?.Invoke(this, error.ToDisplayText());
        return new ZeroTierStatus(false, string.Empty, 0, string.Empty, false, false, error.ToDisplayText(), error);
    }

    /// <inheritdoc />
    public IReadOnlyList<ulong> JoinedNetworkIds => _joinedNetworkIds.ToArray();

    /// <inheritdoc />
    public string LocalVirtualIp => _currentNetworkId == 0 ? string.Empty : FindVirtualIp(_currentNetworkId);

    /// <inheritdoc />
    public string LocalNetworkPrefix
    {
        get
        {
            string virtualIp = LocalVirtualIp;

            if (string.IsNullOrWhiteSpace(virtualIp))
            {
                return string.Empty;
            }

            string[] parts = virtualIp.Split('.');
            return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}." : string.Empty;
        }
    }

    /// <inheritdoc />
    public async Task<ZeroTierStatus> StartAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return Fail(ZeroTierErrors.UnsupportedPlatformError());
            }

            if (FindServiceExecutable() is null)
            {
                // 尝试用随应用发行的官方 MSI 静默安装（需要管理员权限，本程序 Release 下已带提权清单）。
                if (!await TryInstallAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Fail(ZeroTierErrors.NotInstalledError());
                }
            }

            // 0) 先清掉「野生」实例：Windows 允许多个 zerotier-one_x64.exe 同时跑（Console 会话的
            //    前台节点会与系统服务双绑同一监听端口），CLI 按端口文件连接时随机撞到初始化不全的
            //    实例，表现为 -q 命令 10 秒超时。必须保证服务独占数据目录与端口。
            await EnsureNoForeignInstanceAsync(cancellationToken).ConfigureAwait(false);

            // 1) 服务必须已安装且在运行：服务不存在时 -q 命令会挂住（之前 join 超时的根源），
            //    所以连接前先把系统服务拉起来（未注册会自动重装 MSI 补回）。
            string? serviceError = await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

            if (serviceError is not null)
            {
                return Fail(ZeroTierErrors.ServiceNotRegisteredError(serviceError));
            }

            // 2) 服务在跑 ≠ 本地 API 活着：用 info 探测控制接口；连不到就自愈一次
            //    （终止全部实例重新拉起服务），覆盖服务僵死、端口被抢等「RUNNING 但命令挂死」场景。
            CommandResult info = default;
            bool healed = false;

            while (true)
            {
                info = await RunCommandAsync("info", cancellationToken).ConfigureAwait(false);

                if (info.IsSuccess && !string.IsNullOrWhiteSpace(info.NodeId))
                {
                    break;
                }

                if (!healed)
                {
                    healed = true;
                    EventRaised?.Invoke(this, "ZeroTier 命令无响应，正在重启系统服务以恢复本地接口……");
                    _logger.LogWarning("zerotier-one -q info 无响应（{Error}），尝试重启服务恢复。", info.ErrorText);

                    await KillAllServiceProcessesAsync(cancellationToken).ConfigureAwait(false);

                    serviceError = await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

                    if (serviceError is not null)
                    {
                        return Fail(ZeroTierErrors.ServiceNotRegisteredError(serviceError));
                    }

                    continue;
                }

                return Fail(ZeroTierErrors.ControlPlaneDeadError(
                    "info 探测无响应，重启服务后仍未恢复",
                    info.ErrorText));
            }

            // 2.5) 官方控制器模式下，校准 planet 为官方根服务器文件。
            //     首次安装官方 MSI 后，服务启动时若从官方根下载 planet 失败（网络受限、
            //     或机器上曾装过带自建 planet 的第三方 ZeroTier），数据目录会残留一份
            //     「自建/污染」的 planet，节点跑在错误的根服务器宇宙里，join 官方网络时
            //     central.zerotier.com 永远看不到入网请求。这里检测到非官方 planet 就
            //     停服务 → 删 planet → 重启服务，让服务自动重下官方 planet。
            if (string.Equals(ConnectionMode, ZeroTierSettings.OfficialController, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureOfficialPlanetAsync(cancellationToken).ConfigureAwait(false);

                // 校准可能重启了服务，重新探测一次 info 拿到校准后的最新在线状态。
                CommandResult refreshed = await RunCommandAsync("info", cancellationToken).ConfigureAwait(false);

                if (refreshed.IsSuccess && !string.IsNullOrWhiteSpace(refreshed.NodeId))
                {
                    info = refreshed;
                }
            }

            // 2.6) 显式解析并播报节点自身的在线状态 —— info 输出形如
            //      `200 info 08bd1dea74 1.16.2 ONLINE`。之前日志只显示网络状态
            //      （REQUESTING_CONFIGURATION），节点连不上根服务器（自建 planet/moon
            //      不可达）时用户无法区分「控制器没授权」和「节点根本离线」。
            string? nodeOnlineState = ExtractOnlineState(info.RawOutput);

            if (string.Equals(nodeOnlineState, "OFFLINE", StringComparison.OrdinalIgnoreCase))
            {
                ZeroTierError offlineError = ZeroTierErrors.NodeOfflineError(info.RawOutput?.Trim());
                EventRaised?.Invoke(this, $"警告：{offlineError.ToDisplayText()}（此时控制器授权了也收不到配置）");
                _logger.LogWarning(offlineError.ToLogText());
            }
            else
            {
                _logger.LogInformation("zerotier-one info：{Raw}", info.RawOutput?.Trim());
            }

            // 3) 清理上次会话遗留的网络成员资格：服务把成员资格持久化在 networks.d，
            //    每次启动都会自动重连，每个成员资格常驻一块虚拟网卡 —— 界面上
            //    「多个 ZeroTier 虚拟网卡」就是遗留成员资格累积的结果。
            //    连接前先全部离开，保证每次连接都从干净状态开始。
            await PruneLeftoverNetworksAsync(cancellationToken).ConfigureAwait(false);

            // 记下节点 ID（基本不变），并启动后台刷新循环维护网络状态快照。
            NetworkSnapshot previous = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, new NetworkSnapshot(info.NodeId, previous.Networks));
            Interlocked.Exchange(ref _consecutiveRefreshFailures, 0);
            EnsureStatusRefreshRunning();

            EventRaised?.Invoke(this, $"ZeroTier 客户端已就绪（节点 ID：{info.NodeId}）");
            _logger.LogInformation("ZeroTier 客户端已就绪，节点 ID：{NodeId}", info.NodeId);
            return new ZeroTierStatus(true, info.NodeId, 0, string.Empty, true, false, "客户端已就绪");
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// 启动后台状态刷新循环（幂等）：每隔 REFRESH_INTERVAL 跑一次 <c>listnetworks -j</c>，
    /// 把结果以不可变快照整体替换进缓存。所有同步成员（GetStatus / LocalVirtualIp 等）
    /// 只读缓存 —— 之前它们每次都同步跑子进程，在界面线程上还套了同步等待，直接把整个启动器冻死了。
    /// </summary>
    private void EnsureStatusRefreshRunning()
    {
        if (Interlocked.CompareExchange(ref _refreshStarted, 1, 0) != 0)
        {
            // Stop() 取消过刷新循环时允许重新启动（否则断开重连后快照永远不再更新）。
            if (_refreshCts is { IsCancellationRequested: false })
            {
                return;
            }

            Volatile.Write(ref _refreshStarted, 0);
        }

        _refreshCts = new CancellationTokenSource();
        CancellationToken token = _refreshCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshSnapshotAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "刷新 ZeroTier 状态快照失败。");
                }

                // 控制面连续无响应（服务僵死/意外停止）→ 后台也尝试自愈，
                // 与 join 流程共用同一恢复入口（内部有互斥与冷却，不会双重重启）。
                if (Volatile.Read(ref _consecutiveRefreshFailures) >= 3)
                {
                    try
                    {
                        await RecoverControlPlaneAsync(
                            _currentNetworkId, "后台状态刷新连续无响应", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 自愈失败不终止刷新循环，下一轮再试。
                    }
                }

                try
                {
                    await Task.Delay(REFRESH_INTERVAL, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>
    /// 跑一次 listnetworks -j 并整体替换状态快照（线程安全：快照不可变、引用原子替换）。
    /// 命令失败时保留上一份快照并累计连续失败次数 —— 这个计数就是控制面失联自愈的触发信号。
    /// </summary>
    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await RunCommandAsync("listnetworks -j", cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            int failures = Interlocked.Increment(ref _consecutiveRefreshFailures);

            // 低频告警：头两次每次都记，之后每 10 次记一次，避免服务失联期间日志刷屏。
            if (failures <= 2 || failures % 10 == 0)
            {
                _logger.LogWarning("刷新 ZeroTier 状态失败（连续 {Failures} 次）：{Error}", failures, result.ErrorText);
            }

            return;
        }

        Interlocked.Exchange(ref _consecutiveRefreshFailures, 0);

        using JsonDocument document = ParseListNetworksJson(result.RawOutput);

        List<NetworkInfo> networks = new();

        foreach (JsonElement network in document.RootElement.EnumerateArray())
        {
            ulong id = 0;

            if (network.TryGetProperty("nwid", out JsonElement nwid))
            {
                if (nwid.ValueKind == JsonValueKind.String)
                {
                    ulong.TryParse(nwid.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
                }
                else if (nwid.ValueKind == JsonValueKind.Number)
                {
                    id = nwid.GetUInt64();
                }
            }

            if (id == 0)
            {
                continue;
            }

            string status = network.TryGetProperty("status", out JsonElement statusElement)
                            && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? string.Empty
                : string.Empty;

            string virtualIp = string.Empty;

            if (network.TryGetProperty("assignedAddresses", out JsonElement addresses))
            {
                foreach (JsonElement address in addresses.EnumerateArray())
                {
                    string? value = address.GetString();

                    if (value is null || !value.Contains('.', StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // assignedAddresses 形如 "10.74.203.111/24"（带 CIDR 前缀长度），
                    // 这里必须剥掉后缀只留纯 IP：隧道校验、发现报文回包、自机比对、
                    // 界面显示都要求纯 IP —— 之前直接透传 "/24" 导致隧道启动被拒
                    // （「不是合法的本机虚拟 IP」）。
                    string bareIp = value.Split('/')[0].Trim();

                    if (bareIp.Length > 0 && System.Net.IPAddress.TryParse(bareIp, out _))
                    {
                        virtualIp = bareIp;
                        break;
                    }
                }
            }

            networks.Add(new NetworkInfo(id, status, virtualIp));
        }

        NetworkSnapshot previous = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, new NetworkSnapshot(previous.NodeId, networks));
    }

    /// <summary>刷新一次快照，异常只记日志（用于 join/leave 后立即刷新，不等下个周期）。</summary>
    private async Task RefreshSnapshotSafeAsync()
    {
        try
        {
            await RefreshSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "刷新 ZeroTier 状态快照失败。");
        }
    }

    /// <summary>
    /// 用随应用发行的官方 ZeroTierOne.msi 静默安装客户端。
    /// 注意：MSI 对「产品仍注册在案但服务被删」的机器会 no-op 退出 0（什么都没装），
    /// 所以这里只认退出码与文件就位，服务是否真正恢复由调用方再核实。
    /// </summary>
    private async Task<bool> TryInstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BundledInstaller))
        {
            _logger.LogWarning("未找到随应用发行的安装包 {Installer}，无法自动安装 ZeroTier One。", BundledInstaller);
            return false;
        }

        EventRaised?.Invoke(this, "正在静默安装 ZeroTier One 客户端（首次可能需要几十秒）…");

        // 先读输出再等退出（RunProcessAsync 内部已处理管道互等与超时杀进程）。
        ProcessRunResult result = await RunProcessAsync(
            "msiexec.exe", string.Empty,
            ["/i", BundledInstaller, "/qn", "/norestart"],
            TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            // 常见退出码：1603 安装失败、1625 需要提升权限、1638 另一版本已安装。
            _logger.LogWarning(
                "ZeroTier One 安装失败（msiexec 退出码 {Code}）：{Error}",
                result.ExitCode,
                string.IsNullOrWhiteSpace(result.ErrorText) ? "（msiexec 无输出，退出码见上）" : result.ErrorText);
            return false;
        }

        if (FindServiceExecutable() is not null)
        {
            _logger.LogInformation("ZeroTier One 安装完成。");
            return true;
        }

        // 退出码 0 但可执行文件没就位：基本就是 MSI 把这次安装当成了 no-op。
        _logger.LogWarning("msiexec 退出码 0，但未找到 zerotier-one 可执行文件（MSI 可能把这次安装当成了已安装的空操作）。");
        return false;
    }

    /// <summary>
    /// 用本机已有的 zerotier-one 可执行文件直接注册 Windows 服务（官方 <c>-I</c> 开关）。
    /// 这是「文件还在、服务被删」残缺状态最快的恢复方式 —— MSI 在这种状态下只会空转。
    /// 需要管理员权限（本程序带 require-admin 清单）。返回是否注册成功（服务在 SCM 中可见）。
    /// </summary>
    private async Task<bool> TryRegisterServiceAsync(CancellationToken cancellationToken)
    {
        string? executable = FindServiceExecutable();

        if (executable is null)
        {
            _logger.LogWarning("未找到任何 zerotier-one 可执行文件，无法直接注册系统服务。");
            return false;
        }

        EventRaised?.Invoke(this, "正在注册 ZeroTier 系统服务……");
        _logger.LogInformation("使用 {Exe} -I 注册 Windows 服务 {Service}。", executable, ServiceName);

        ProcessRunResult result = await RunProcessAsync(
            executable,
            Path.GetDirectoryName(executable) ?? string.Empty,
            ["-I"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "注册 ZeroTier 系统服务失败（{Exe} -I 退出码 {Code}）：{Error}",
                executable, result.ExitCode, result.ErrorText);
            return false;
        }

        // -I 正常返回后服务应立即可见；轮询几秒以防 SCM 注册延迟。
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            string? state = await QueryServiceStateAsync(cancellationToken).ConfigureAwait(false);

            if (state is not null)
            {
                _logger.LogInformation("ZeroTier 系统服务（{Service}）注册成功。", ServiceName);
                return true;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("{Exe} -I 已执行成功，但系统服务仍未注册（可能服务处于待删除状态，需重启 Windows）。", executable);
        return false;
    }

    /// <summary>
    /// 确保没有「野生」的 zerotier-one_x64.exe 实例（用户会话里手动启动的前台节点）。
    /// 野生实例会与系统服务抢占数据目录文件锁与监听端口（Windows 允许 SO_REUSEADDR 双绑同一端口），
    /// CLI 按端口文件连接时随机撞到初始化不全的实例，命令永久挂死（实测：两个进程双绑 49770，
    /// -q 命令约一半概率超时）。发现野生实例时终止全部实例 —— 服务实例会被一并终止，
    /// 随后由 <see cref="EnsureServiceRunningAsync"/> 检测到 STOPPED 并重新拉起。
    /// </summary>
    private async Task EnsureNoForeignInstanceAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult listing = await RunProcessAsync(
            "tasklist", string.Empty,
            ["/FO", "CSV", "/NH", "/FI", $"IMAGENAME eq {ServiceExecutableName}"],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        if (!listing.IsSuccess || string.IsNullOrWhiteSpace(listing.Output))
        {
            return; // 枚举失败不阻塞启动，交给后面的 info 自愈兜底。
        }

        bool hasForeign = false;

        // CSV 行形如 "zerotier-one_x64.exe","39948","Console","1","17,332 K"；
        // 第 3 列是会话名（Services = 系统服务，Console = 用户会话的前台实例）。
        foreach (string rawLine in listing.Output.Split('\n'))
        {
            string line = rawLine.Trim();

            if (!line.StartsWith('"'))
            {
                continue; // 空行或「没有匹配任务」提示。
            }

            string[] fields = line.Split('"');

            if (fields.Length >= 6 && !string.Equals(fields[5], "Services", StringComparison.OrdinalIgnoreCase))
            {
                hasForeign = true;
                break;
            }
        }

        if (!hasForeign)
        {
            return;
        }

        EventRaised?.Invoke(this, "检测到其他 ZeroTier 实例正在运行（会与服务抢占端口），正在停止……");
        _logger.LogWarning("发现非服务会话的 {Exe} 实例，终止全部实例以保证系统服务独占数据目录与端口。", ServiceExecutableName);
        await KillAllServiceProcessesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>强制终止所有 zerotier-one_x64.exe 实例（含系统服务进程；随后需重新启动服务）。</summary>
    private async Task KillAllServiceProcessesAsync(CancellationToken cancellationToken)
    {
        await RunProcessAsync(
            "taskkill", string.Empty,
            ["/F", "/IM", ServiceExecutableName],
            TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        // 给 SCM 一点时间把进程退出同步为 STOPPED，EnsureServiceRunningAsync 会重新拉起。
        await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 控制面失联自愈：终止全部实例 → 重新拉起系统服务 →（需要时）重新下发 join →
    /// 等后台刷新循环确认命令通道恢复。实测 join 可能触发服务停止响应（僵死/意外停止），
    /// 表现为之后所有 -q 命令超时；重启服务是唯一可靠的解锁方式（服务会按 networks.d
    /// 自动重连已加入的网络，这里再显式 join 一次双保险）。
    /// 供 join 流程与后台刷新循环共用：内部用 <see cref="_healGate"/> 互斥、<see cref="HEAL_COOLDOWN"/> 冷却，
    /// 多处同时触发也只会执行一次恢复。返回控制面是否恢复。
    /// </summary>
    private async Task<bool> RecoverControlPlaneAsync(ulong rejoinNetworkId, string reason, CancellationToken cancellationToken)
    {
        await _healGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (DateTime.UtcNow - _lastHealAtUtc >= HEAL_COOLDOWN)
            {
                _lastHealAtUtc = DateTime.UtcNow;

                EventRaised?.Invoke(this, $"ZeroTier 服务无响应（{reason}），正在自动恢复……");
                _logger.LogWarning("ZeroTier 控制面失联（{Reason}）：终止全部实例并重启系统服务。", reason);

                await KillAllServiceProcessesAsync(cancellationToken).ConfigureAwait(false);

                string? error = await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

                if (error is not null)
                {
                    EventRaised?.Invoke(this, $"ZeroTier 服务自动恢复失败：{error}");
                    _logger.LogWarning("ZeroTier 服务自动恢复失败：{Error}", error);
                }
                else if (rejoinNetworkId != 0)
                {
                    string networkHex = rejoinNetworkId.ToString("x16", CultureInfo.InvariantCulture);
                    await RunCommandAsync($"join {networkHex}", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _healGate.Release();
        }

        // 等刷新循环确认命令通道恢复（服务起来后第一轮 listnetworks 成功即把失败计数清零）。
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(12))
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            if (Volatile.Read(ref _consecutiveRefreshFailures) == 0)
            {
                EventRaised?.Invoke(this, "ZeroTier 服务已恢复响应。");
                _logger.LogInformation("ZeroTier 控制面已恢复。");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 确保 ZeroTier 系统服务（<see cref="ServiceName"/>）已安装且处于运行状态。
    /// 服务未注册时按三级恢复：exe 直接注册（-I）→ MSI 重装 → 再补一次 -I
    /// （MSI 对「产品仍注册在案」的机器会静默 no-op，服务补不回来，所以不能只依赖 MSI）。
    /// 已注册但停止时启动并等待就绪。返回 null 表示就绪，否则返回给用户的错误说明。
    /// </summary>
    private async Task<string?> EnsureServiceRunningAsync(CancellationToken cancellationToken)
    {
        string? state = await QueryServiceStateAsync(cancellationToken).ConfigureAwait(false);

        if (state is null)
        {
            // 服务未注册（常见：客户端被卸载或服务被清理工具删掉，但文件/数据目录还在）。
            EventRaised?.Invoke(this, "检测到 ZeroTier 系统服务未注册，正在恢复……");
            _logger.LogWarning("未找到 Windows 服务 {Service}，尝试自动恢复。", ServiceName);

            // 第 1 级：本机已有 exe → 官方 -I 开关直接注册（最快）。
            if (!await TryRegisterServiceAsync(cancellationToken).ConfigureAwait(false))
            {
                // 第 2 级：MSI 重装（顺带补齐可能缺失的文件与驱动）。
                EventRaised?.Invoke(this, "正在通过官方安装包恢复 ZeroTier……");

                if (!await TryInstallAsync(cancellationToken).ConfigureAwait(false))
                {
                    return "ZeroTier 系统服务未注册，且自动恢复失败。请以管理员身份运行本程序后重试。";
                }

                // 第 3 级：MSI 可能被「已安装」no-op 跳过，装完再用 -I 补注册一次。
                if (!await TryRegisterServiceAsync(cancellationToken).ConfigureAwait(false))
                {
                    return "ZeroTier One 已安装，但系统服务（ZeroTierOneService）仍未注册。"
                        + "可能被安全软件拦截，或上次卸载未完成（建议重启 Windows 后重试）。";
                }
            }

            state = await QueryServiceStateAsync(cancellationToken).ConfigureAwait(false);

            if (state is null)
            {
                return "ZeroTier 系统服务仍未注册（建议重启 Windows 后重试）。";
            }
        }

        if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 服务已注册但没在跑 → 启动并轮询等待就绪（服务冷启动通常几秒）。
        EventRaised?.Invoke(this, "正在启动 ZeroTier 系统服务……");
        _logger.LogInformation("ZeroTier 服务当前状态 {State}，正在启动。", string.IsNullOrWhiteSpace(state) ? "未知" : state);

        await RunProcessAsync(
            "sc.exe", string.Empty, ["start", ServiceName], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < SERVICE_START_TIMEOUT)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            state = await QueryServiceStateAsync(cancellationToken).ConfigureAwait(false);

            if (string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("ZeroTier 系统服务已启动。");
                return null;
            }
        }

        return $"ZeroTier 系统服务启动超时（{SERVICE_START_TIMEOUT.TotalSeconds:N0} 秒），当前状态：{state ?? "未知"}。";
    }

    /// <summary>
    /// 用 <c>sc query</c> 查询 ZeroTier 系统服务状态。
    /// 返回状态字符串（如 RUNNING / STOPPED）；服务未注册时返回 null。
    /// </summary>
    private async Task<string?> QueryServiceStateAsync(CancellationToken cancellationToken)
    {
        ProcessRunResult result = await RunProcessAsync(
            "sc.exe", string.Empty, ["query", ServiceName], TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);

        // sc query 对不存在的服务返回退出码 1060。
        if (result.ExitCode == 1060)
        {
            return null;
        }

        if (!result.IsSuccess)
        {
            // 查询失败但服务存在（权限等），当作「未运行」处理。
            return string.Empty;
        }

        // 输出形如 "        STATE              : 4  RUNNING"。
        Match match = Regex.Match(result.Output, @"STATE\s*:\s*\d+\s+(\S+)");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <inheritdoc />
    public async Task OrbitMoonAsync(ulong moonId, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string moonHex = moonId.ToString("x", CultureInfo.InvariantCulture);
            CommandResult result = await RunCommandAsync($"orbit {moonHex} {moonHex}", cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"moon 轨道设置失败：{result.ErrorText}");
            }

            _logger.LogInformation("已围绕 moon {MoonId:x} 建立轨道。", moonId);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ZeroTierStatus> JoinNetworkAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _currentNetworkId = networkId;

            string networkHex = networkId.ToString("x16", CultureInfo.InvariantCulture);
            _logger.LogInformation("正在加入网络 {NetworkId:x16}（客户端引擎）……", networkId);
            CommandResult joinResult = await RunCommandAsync($"join {networkHex}", cancellationToken).ConfigureAwait(false);

            if (!joinResult.IsSuccess)
            {
                ZeroTierError joinError = ZeroTierErrors.JoinFailedError(joinResult.ErrorText);
                _logger.LogWarning(joinError.ToLogText());
                ZeroTierStatus failStatus = GetStatus(networkId);
                return failStatus with { Error = joinError, StatusText = joinError.ToDisplayText() };
            }

            if (!_joinedNetworkIds.Contains(networkId))
            {
                _joinedNetworkIds.Add(networkId);
            }

            // join 已受理，立即刷一次快照（不等下个刷新周期）。
            await RefreshSnapshotSafeAsync().ConfigureAwait(false);

            // 客户端走的是系统服务，加入后等虚拟网卡拿到地址（正常几秒；最长 JOIN_TIMEOUT）。
            // 轮询只读后台维护的快照 —— 绝不在这里同步跑子进程（会冻死界面线程）。
            // 失联自愈：实测 join 可能触发服务停止响应（之后所有 -q 命令超时，服务进程僵死）。
            // 后台刷新连续失败 ≥2 即判定控制面失联，自动重启服务并重新下发 join。
            Stopwatch stopwatch = Stopwatch.StartNew();
            string? lastStatusText = null;
            bool configHintShown = false;
            int healAttempts = 0;
            ZeroTierError? failureError = null;
            DateTime lastOnlineProbe = DateTime.UtcNow;
            bool offlineAnnounced = false;

            while (stopwatch.Elapsed < JOIN_TIMEOUT)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Volatile.Read(ref _consecutiveRefreshFailures) >= 2)
                {
                    healAttempts++;

                    if (healAttempts > 2)
                    {
                        failureError = ZeroTierErrors.ControlPlaneDeadError(
                            "加入网络期间服务多次无响应，自动重启后仍未恢复",
                            "本机代理软件的虚拟网卡可能劫持了回环连接，建议把 127.0.0.1 加入代理直连名单后重试");
                        break;
                    }

                    bool recovered = await RecoverControlPlaneAsync(
                        networkId, $"加入网络 {networkId:x16} 时服务无响应", cancellationToken).ConfigureAwait(false);

                    if (!recovered)
                    {
                        continue; // 未恢复时继续等待，冷却结束后可再次触发恢复
                    }

                    stopwatch.Restart();
                    lastStatusText = null;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(FindVirtualIp(networkId)))
                {
                    break;
                }

                // 把服务报告的真实状态同步到界面（仅在状态变化时播报，避免刷屏）。
                string statusText = DescribeNetworkStatus(FindNetworkStatus(networkId));

                if (!string.Equals(statusText, lastStatusText, StringComparison.Ordinal))
                {
                    lastStatusText = statusText;
                    EventRaised?.Invoke(this, $"网络 {networkId:x16}：{statusText}");
                }

                // 长时间停留在 REQUESTING_CONFIGURATION：本地控制面正常但控制器始终没有下发配置，
                // 最常见原因是控制器上还没给本节点授权（Auth），或控制器不可达。
                if (!configHintShown
                    && stopwatch.Elapsed > TimeSpan.FromSeconds(20)
                    && string.Equals(FindNetworkStatus(networkId), "REQUESTING_CONFIGURATION", StringComparison.Ordinal))
                {
                    configHintShown = true;
                    string nodeId = Volatile.Read(ref _snapshot).NodeId;
                    ZeroTierError requestingError = ZeroTierErrors.RequestingConfigurationError();
                    EventRaised?.Invoke(this,
                        $"{requestingError.ToDisplayText()} —— 请到控制器确认成员 {nodeId} 已授权（Auth）且控制器在线可达。");
                    _logger.LogWarning(requestingError.ToLogText());

                    // 自动定位「已授权仍拿不到配置」的根因：检查控制器节点是否在本节点的 peers 里。
                    // 网络 ID 前 10 位十六进制就是控制器的节点 ID —— 若 peers 里没有它，说明控制器
                    // 没有注册到本节点所用的根服务器（自建 planet 的根），配置永远推不下来。
                    await ProbeControllerPeerAsync(networkId, cancellationToken).ConfigureAwait(false);
                }

                // 每 10 秒探测一次节点自身的在线状态（低频，避免与后台刷新争抢 CLI 闸门）：
                // OFFLINE = 连不上根服务器/moon，此时控制器授权了配置也推不下来 ——
                // 这是「已在控制器授权却一直 REQUESTING_CONFIGURATION」的最常见根因。
                if ((DateTime.UtcNow - lastOnlineProbe).TotalSeconds >= 10)
                {
                    lastOnlineProbe = DateTime.UtcNow;

                    CommandResult probe = await RunCommandAsync("info", cancellationToken).ConfigureAwait(false);
                    string? probeState = ExtractOnlineState(probe.RawOutput);

                    if (string.Equals(probeState, "OFFLINE", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!offlineAnnounced)
                        {
                            offlineAnnounced = true;
                            ZeroTierError offlineError = ZeroTierErrors.NodeOfflineError(probe.RawOutput?.Trim());
                            EventRaised?.Invoke(this, offlineError.ToDisplayText());
                            _logger.LogWarning(offlineError.ToLogText());

                            // 顺带把 peers 概况写进日志：0 个 ONLINE = 根本没连上任何根。
                            CommandResult peersResult = await RunCommandAsync("peers", cancellationToken).ConfigureAwait(false);
                            string peersSummary = string.Join(
                                " | ",
                                (peersResult.RawOutput ?? string.Empty)
                                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Where(line => line.Contains("ONLINE", StringComparison.Ordinal)
                                                   || line.Contains("OFFLINE", StringComparison.Ordinal))
                                    .Take(6));
                            _logger.LogWarning("节点 OFFLINE 时的 peers 概况：{Peers}", peersSummary);
                        }
                    }
                    else if (probe.IsSuccess)
                    {
                        offlineAnnounced = false; // 恢复在线后允许下次离线再播报
                    }
                }

                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                // 快照由后台循环每 2 秒维护，这里不再额外刷命令 —— 之前每轮末尾都手动刷一次，
                // 与后台循环的 CLI 进程争抢单线程的服务控制接口，把命令挤到超时（实测日志里
                // 「连续 1 次」反复出现就是这么来的：一半命令饿死超时，另一半成功把计数清零）。
            }

            ZeroTierStatus status = GetStatus(networkId);

            if (!status.IsTransportReady)
            {
                if (failureError is not null)
                {
                    status = status with { StatusText = failureError.ToDisplayText(), Error = failureError };
                    EventRaised?.Invoke(this, failureError.ToDisplayText());
                    _logger.LogWarning(failureError.ToLogText());
                }
                else
                {
                    // 最常见的原因已由上面的探测分类：节点 OFFLINE（连不上根/moon）或未授权。
                    // 这里兜底把「网络状态 + 等待超时」归类成一个结构化错误。
                    ZeroTierError timeoutError = ZeroTierErrors.JoinTimeoutError(
                        (int)JOIN_TIMEOUT.TotalSeconds, $"网络状态：{status.StatusText}");
                    status = status with { StatusText = timeoutError.ToDisplayText(), Error = timeoutError };
                    _logger.LogWarning(timeoutError.ToLogText());
                    EventRaised?.Invoke(this, $"{timeoutError.ToDisplayText()}（网络状态：{status.StatusText}）");
                }
            }

            if (status.IsTransportReady)
            {
                EventRaised?.Invoke(this, $"已通过虚拟网卡加入网络，虚拟 IP：{status.VirtualIp}");

                // 拿到虚拟 IP 后提高虚拟网卡优先级，让游戏流量优先走 ZeroTier 隧道。
                await RaiseVirtualInterfacePriorityAsync(cancellationToken).ConfigureAwait(false);
            }

            return status;
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<ZeroTierStatus> DisconnectAsync(ulong networkId, CancellationToken cancellationToken = default)
        => LeaveNetworkAsync(networkId, cancellationToken);

    /// <summary>
    /// 提高 ZeroTier One 虚拟网卡的接口优先级（metric），让游戏流量优先走隧道。
    /// 委托给跨平台 <see cref="IRouteMetricService"/>（按平台自动发现网卡并调整 metric）。
    /// 失败只记日志、不抛异常——网卡优先级是优化项，不能反过来阻断联机。
    /// </summary>
    public async Task RaiseVirtualInterfacePriorityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            RouteMetricResult result = await _routeMetric.RaiseZeroTierMetricAsync(_currentNetworkId, 1, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                _logger.LogInformation("ZeroTier 网卡优先级已调整：{Message}", result.Message);
                EventRaised?.Invoke(this, result.Message);
            }
            else
            {
                _logger.LogWarning("调整 ZeroTier 网卡优先级未成功（不影响联机）：{Message}", result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 优化项失败不能阻断联机，只记录。
            _logger.LogWarning(ex, "提高 ZeroTier 虚拟网卡优先级失败（不影响联机，可手动设置 metric）。");
        }
    }

    /// <inheritdoc />
    public ZeroTierStatus GetStatus(ulong networkId)
    {
        // 只读缓存快照：这个方法会被界面 / 发现服务频繁同步调用，
        // 绝不在这里跑子进程或同步等待（否则会冻死调用线程）。
        NetworkSnapshot snapshot = Volatile.Read(ref _snapshot);

        if (networkId == 0)
        {
            return new ZeroTierStatus(true, snapshot.NodeId, 0, string.Empty, true, false, "未加入网络");
        }

        NetworkInfo? network = null;

        foreach (NetworkInfo item in snapshot.Networks)
        {
            if (item.Id == networkId)
            {
                network = item;
                break;
            }
        }

        string virtualIp = network?.VirtualIp ?? string.Empty;
        bool ready = !string.IsNullOrWhiteSpace(virtualIp);

        // 未就绪时把 ZeroTier 服务报告的真实状态带出来（未授权 / 请求配置 / 网络不存在），
        // 并映射成结构化错误（含错误码），否则界面上只会一直显示「正在加入网络」，排查不了问题。
        string statusText = ready ? "已连接" : DescribeNetworkStatus(network?.Status);
        ZeroTierError? error = ready ? null : ClassifyNetworkStatus(network?.Status, networkId);

        return new ZeroTierStatus(
            true,
            snapshot.NodeId,
            networkId,
            virtualIp,
            true,
            ready,
            error is null ? statusText : error.ToDisplayText(),
            error);
    }

    /// <summary>把 listnetworks 里的 status 字段翻译成给用户看的说明（拿不到时给通用提示）。</summary>
    private static string DescribeNetworkStatus(string? status) => status switch
    {
        null or "" => "正在加入网络（等待虚拟网卡地址）",
        "OK" => "网络已就绪，等待分配虚拟网卡地址",
        "ACCESS_DENIED" => "网络拒绝访问（节点未被授权，需要在控制器 / 官网勾选 Auth）",
        "REQUESTING_CONFIGURATION" => "正在向控制器请求网络配置……",
        "NOT_FOUND" => "网络不存在（请检查网络 ID）",
        "CLIENT_TOO_OLD" => "客户端版本过旧，被网络拒绝",
        var other => $"网络状态：{other}"
    };

    /// <summary>
    /// 把 ZeroTier 服务的网络状态字符串映射成结构化错误（含错误码）。
    /// 只在「未拿到虚拟 IP」时调用，用于把可识别的失败状态归类到稳定错误码，
    /// 让用户和日志都能一眼定位（而不是只有一段话）。
    /// </summary>
    private static ZeroTierError? ClassifyNetworkStatus(string? status, ulong networkId)
    {
        return status switch
        {
            "ACCESS_DENIED" => ZeroTierErrors.AccessDeniedError($"网络 {networkId:x16}"),
            "REQUESTING_CONFIGURATION" => ZeroTierErrors.RequestingConfigurationError($"网络 {networkId:x16}"),
            "NOT_FOUND" => ZeroTierErrors.NetworkNotFoundError($"网络 {networkId:x16}"),
            "CLIENT_TOO_OLD" => new ZeroTierError(
                "ZT-3004", ZeroTierErrorCategory.Network,
                "客户端版本过旧，被网络拒绝",
                "该网络要求更高版本的 ZeroTier 客户端。",
                "升级 ZeroTier One 到最新版本后重试。",
                $"网络 {networkId:x16}"),
            _ => null,
        };
    }

    /// <summary>从缓存快照里找网络的虚拟 IP。</summary>
    private string FindVirtualIp(ulong networkId)
    {
        foreach (NetworkInfo network in Volatile.Read(ref _snapshot).Networks)
        {
            if (network.Id == networkId)
            {
                return network.VirtualIp;
            }
        }

        return string.Empty;
    }

    /// <summary>从缓存快照里找网络的服务端状态。</summary>
    private string FindNetworkStatus(ulong networkId)
    {
        foreach (NetworkInfo network in Volatile.Read(ref _snapshot).Networks)
        {
            if (network.Id == networkId)
            {
                return network.Status;
            }
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<ZeroTierPingResult> PingAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // 有真虚拟网卡后，直接用系统 ICMP ping 即可（不再需要 libzt 的 TCP 探测）。
        if (string.IsNullOrWhiteSpace(host))
        {
            return new ZeroTierPingResult(false, 0, "主机地址为空。");
        }

        int timeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 100d, 30000d);

        return await Task.Run(async () =>
        {
            try
            {
                ProcessStartInfo startInfo = new("ping")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    ArgumentList = { "-n", "1", "-w", timeoutMs.ToString(CultureInfo.InvariantCulture), host.Trim() }
                };

                using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ping。");

                // 先读输出再等退出：反过来在输出超管道缓冲时会互等。
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    return new ZeroTierPingResult(false, 0, $"来自 {host} 无响应");
                }

                // 从输出里解析「时间=XXms」。
                return ParsePingRoundTrip(output, host);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Ping {Host} 失败：{Message}", host, ex.Message);
                return new ZeroTierPingResult(false, 0, $"请求无响应（{ex.Message}）");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Stop()
    {
        // 断开时不动服务生命周期（用户可能马上重连）；
        // 应用退出时的完整清理（离开全部网络 + 停止服务）见 CleanupOnExitAsync。
        _currentNetworkId = 0;

        _refreshCts?.Cancel();
    }

    /// <inheritdoc />
    public async Task CleanupOnExitAsync()
    {
        // 退出路径尽力而为：任何失败都吞掉，绝不抛出、绝不长时间阻塞退出。
        // 整个清理限 12 秒预算（服务健康时 1~2 秒完成；服务僵死时超时放弃，不影响退出）。
        try
        {
            using CancellationTokenSource cleanupCts = new(TimeSpan.FromSeconds(12));
            CancellationToken token = cleanupCts.Token;

            string? state = await QueryServiceStateAsync(token).ConfigureAwait(false);

            if (state is null || !string.Equals(state, "RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                return; // 服务没在跑就没有运行态可清理，也不值得为清理把服务拉起来。
            }

            // 1) 离开全部网络：成员资格持久化在 networks.d，不 leave 虚拟网卡就会常驻。
            CommandResult list = await RunCommandAsync("listnetworks -j", token).ConfigureAwait(false);

            if (list.IsSuccess)
            {
                foreach (ulong id in ExtractNetworkIds(list.RawOutput))
                {
                    string networkHex = id.ToString("x16", CultureInfo.InvariantCulture);
                    CommandResult leave = await RunCommandAsync($"leave {networkHex}", token).ConfigureAwait(false);

                    if (leave.IsSuccess)
                    {
                        _joinedNetworkIds.Remove(id);
                        _logger.LogInformation("退出清理：已离开网络 {NetworkId:x16}（虚拟网卡随之一并移除）。", id);
                    }
                }
            }

            // 2) 停止系统服务（不停止的话服务进程会常驻后台）。
            await StopServiceAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "退出清理 ZeroTier 时出现异常（已忽略）。");
        }
    }

    /// <summary>停止 ZeroTier 系统服务并等待其退出（退出清理用）。</summary>
    private async Task StopServiceAsync(CancellationToken cancellationToken)
    {
        EventRaised?.Invoke(this, "正在停止 ZeroTier 系统服务……");
        _logger.LogInformation("退出清理：正在停止 Windows 服务 {Service}。", ServiceName);

        await RunProcessAsync(
            "sc.exe", string.Empty, ["stop", ServiceName], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        // 等待服务真正退出（等不到也没关系：停止信号已发出，SCM 会完成剩余工作）。
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            string? state = await QueryServiceStateAsync(cancellationToken).ConfigureAwait(false);

            if (string.Equals(state, "STOPPED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("ZeroTier 系统服务已停止。");
                EventRaised?.Invoke(this, "ZeroTier 系统服务已停止");
                return;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 官方控制器模式下，把 planet 校准为官方根服务器文件。
    ///
    /// 背景：官方 MSI 安装后，服务首次启动时会从官方根下载 planet；若此时网络受限，
    /// 或机器上曾装过带自建 planet 的第三方 ZeroTier（国内常见的「ZeroTier 加速」工具会
    /// 替换 planet），数据目录里就会残留一份「自建/污染」的单根 planet。节点因此跑在
    /// 错误的根服务器宇宙里，join 官方网络时 central.zerotier.com 永远看不到入网请求。
    ///
    /// 机制：ZeroTier 服务在 planet 文件缺失时会自动从官方根重新下载官方 planet，
    /// 所以这里检测到非官方 planet 就「停服务 → 删 planet → 重启服务」触发重下。
    /// 识别依据：官方 planet 必含 cafe 前缀的根服务器（节点 ID 高 16 位 = 0xcafe，
    /// 字节序列 CA FE）；自建单根 planet（如 5caabe9748 / 7db2aeb412）不含。
    /// </summary>
    private async Task EnsureOfficialPlanetAsync(CancellationToken cancellationToken)
    {
        string planetPath = Path.Combine(ServiceDirectory, "planet");

        if (!File.Exists(planetPath))
        {
            // 无 planet：服务启动时会自动从官方根下载，无需处理。
            return;
        }

        byte[] content;

        try
        {
            content = await File.ReadAllBytesAsync(planetPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 planet 文件失败，跳过校准。");
            return;
        }

        if (ContainsOfficialRoot(content))
        {
            // 已是官方 planet。
            return;
        }

        EventRaised?.Invoke(this, "检测到非官方的 planet 文件，正在重新校准为官方根服务器……");
        _logger.LogWarning(
            "检测到 planet 不是官方根服务器文件（{Bytes} 字节，无 cafe 前缀根特征），将删除并重启服务重下官方 planet。",
            content.Length);

        // 服务运行中会缓存并锁定 planet：必须先停服务，再删文件，再重启触发重下。
        await StopServiceAsync(cancellationToken).ConfigureAwait(false);
        await KillAllServiceProcessesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            File.Delete(planetPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除 planet 文件失败（可能仍被占用），继续尝试重启服务。");
        }

        string? error = await EnsureServiceRunningAsync(cancellationToken).ConfigureAwait(false);

        if (error is not null)
        {
            _logger.LogWarning("重新校准 planet 后重启服务失败：{Error}", error);
            EventRaised?.Invoke(this, $"重新校准 planet 后重启服务失败：{error}");
            return;
        }

        // 给服务一点时间从官方根拉取官方 planet。
        await Task.Delay(3000, cancellationToken).ConfigureAwait(false);

        EventRaised?.Invoke(this, "planet 已重新校准为官方根服务器文件。");
        _logger.LogInformation("planet 已重新校准为官方根服务器文件。");
    }

    /// <summary>官方 planet 必含 cafe 前缀的根服务器（节点 ID 高 16 位 = 0xcafe，字节序列 CA FE）。</summary>
    private static bool ContainsOfficialRoot(byte[] content)
    {
        for (int i = 0; i < content.Length - 1; i++)
        {
            if (content[i] == 0xCA && content[i + 1] == 0xFE)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>离开指定网络（客户端支持真退网）。</summary>
    private async Task<ZeroTierStatus> LeaveNetworkAsync(ulong networkId, CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string networkHex = networkId.ToString("x16", CultureInfo.InvariantCulture);
            CommandResult result = await RunCommandAsync($"leave {networkHex}", cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                _joinedNetworkIds.Remove(networkId);
                _logger.LogInformation("已离开网络 {NetworkId:x}。", networkId);
            }

            if (_currentNetworkId == networkId)
            {
                _currentNetworkId = 0;
            }

            await RefreshSnapshotSafeAsync().ConfigureAwait(false);
            return GetStatus(networkId);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    /// <summary>
    /// 离开服务当前已加入的全部网络（建立连接前调用）。
    /// ZeroTier 服务把成员资格持久化在 networks.d 并在每次启动时自动重连，
    /// 每个成员资格对应一块常驻虚拟网卡；不清理的话网卡会越积越多。
    /// </summary>
    private async Task PruneLeftoverNetworksAsync(CancellationToken cancellationToken)
    {
        CommandResult list = await RunCommandAsync("listnetworks -j", cancellationToken).ConfigureAwait(false);

        if (!list.IsSuccess)
        {
            return; // 列不出来就跳过清理，交给 join 流程继续。
        }

        foreach (ulong id in ExtractNetworkIds(list.RawOutput))
        {
            string networkHex = id.ToString("x16", CultureInfo.InvariantCulture);
            CommandResult leave = await RunCommandAsync($"leave {networkHex}", cancellationToken).ConfigureAwait(false);

            if (leave.IsSuccess)
            {
                _joinedNetworkIds.Remove(id);
                _logger.LogInformation("已清理上次遗留的网络 {NetworkId:x16}（虚拟网卡随 leave 一并移除）。", id);
                EventRaised?.Invoke(this, $"已清理上次遗留的网络 {networkHex} 及其虚拟网卡");
            }
        }
    }

    /// <summary>从 listnetworks -j 输出里提取全部网络 ID。</summary>
    private static List<ulong> ExtractNetworkIds(string? rawOutput)
    {
        List<ulong> ids = [];

        using JsonDocument document = ParseListNetworksJson(rawOutput);

        foreach (JsonElement network in document.RootElement.EnumerateArray())
        {
            if (!network.TryGetProperty("nwid", out JsonElement nwid))
            {
                continue;
            }

            if (nwid.ValueKind == JsonValueKind.String
                && ulong.TryParse(nwid.GetString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
            {
                ids.Add(id);
            }
            else if (nwid.ValueKind == JsonValueKind.Number && nwid.TryGetUInt64(out ulong numericId))
            {
                ids.Add(numericId);
            }
        }

        return ids;
    }

    /// <summary>
    /// 探测网络控制器（网络 ID 前 10 位十六进制对应的节点）是否在本节点的 peers 列表里。
    /// 已授权仍一直 REQUESTING_CONFIGURATION 时，最常见的根因是控制器没有注册到本节点
    /// 使用的根服务器（自建 planet 的根只有根自己）—— 节点无法从根获知控制器的位置，
    /// 控制器的网络配置（netconf）因此永远推不下来；此探测把结论直接写进日志与界面。
    /// </summary>
    private async Task ProbeControllerPeerAsync(ulong networkId, CancellationToken cancellationToken)
    {
        try
        {
            ulong controllerId = networkId >> 24;
            string controllerHex = controllerId.ToString("x10", CultureInfo.InvariantCulture);

            CommandResult result = await RunCommandAsync("listpeers -j", cancellationToken).ConfigureAwait(false);

            string json = result.RawOutput?.Trim() ?? string.Empty;
            int start = json.IndexOf('[');

            if (!result.IsSuccess || start < 0)
            {
                _logger.LogWarning("探测控制器可达性失败（listpeers -j 无有效输出）：{Error}", result.ErrorText);
                return;
            }

            using JsonDocument document = JsonDocument.Parse(json[start..]);

            int totalPeers = 0;
            int activePeers = 0;
            List<string> summaries = new();
            int planetCount = 0;
            int controllerLatency = -1;
            string controllerVersion = string.Empty;
            int controllerActivePaths = 0;
            bool controllerFound = false;

            foreach (JsonElement peer in document.RootElement.EnumerateArray())
            {
                totalPeers++;

                // address 字段是十进制字符串形式的节点 ID。
                ulong address = 0;

                if (peer.TryGetProperty("address", out JsonElement addressElement))
                {
                    string raw = addressElement.ValueKind == JsonValueKind.String
                        ? addressElement.GetString() ?? string.Empty
                        : addressElement.GetRawText();

                    if (!ulong.TryParse(raw, out address))
                    {
                        ulong.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
                    }
                }

                string version = peer.TryGetProperty("version", out JsonElement versionElement)
                    ? versionElement.GetString() ?? "-"
                    : "-";
                int latency = peer.TryGetProperty("latency", out JsonElement latencyElement)
                    && latencyElement.ValueKind == JsonValueKind.Number
                        ? latencyElement.GetInt32()
                        : -1;
                string role = peer.TryGetProperty("role", out JsonElement roleElement)
                    ? roleElement.GetString() ?? "?"
                    : "?";

                int activePaths = 0;

                if (peer.TryGetProperty("paths", out JsonElement pathsElement)
                    && pathsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement path in pathsElement.EnumerateArray())
                    {
                        if (path.ValueKind == JsonValueKind.Object
                            && path.TryGetProperty("active", out JsonElement activeElement)
                            && activeElement.ValueKind == JsonValueKind.True)
                        {
                            activePaths++;
                        }
                    }
                }

                if (activePaths > 0)
                {
                    activePeers++;
                }

                if (string.Equals(role, "PLANET", StringComparison.OrdinalIgnoreCase))
                {
                    planetCount++;
                }

                bool isController = address == controllerId && controllerId != 0;

                if (isController)
                {
                    controllerFound = true;
                    controllerLatency = latency;
                    controllerVersion = version;
                    controllerActivePaths = activePaths;
                }

                summaries.Add(
                    $"{(isController ? "【控制器】" : string.Empty)}{address.ToString("x10")}"
                    + $" {role} v{version} {latency}ms {activePaths} 条活动路径");
            }

            _logger.LogInformation(
                "当前 peers（共 {Total} 个，{Active} 个有活动路径）：{Summary}",
                totalPeers, activePeers, string.Join(" | ", summaries));

            // 官方 planet 固定包含 3 个根服务器（role=PLANET）。peers 里 PLANET 不足 3 个，
            // 说明本机 planet 是自建/污染的单根文件 —— 节点在错误的「根服务器宇宙」里，
            // 官方控制器的配置永远推不下来（我方发布场景最常见根因，直接给出可执行的修复建议）。
            if (planetCount < 3)
            {
                ZeroTierError planetError = ZeroTierErrors.PlanetConflictError(
                    $"peers 里只有 {planetCount} 个 PLANET 根（官方应为 3 个）：{string.Join(" | ", summaries)}");
                EventRaised?.Invoke(this, planetError.ToDisplayText());
                _logger.LogWarning(planetError.ToLogText());
            }

            if (!controllerFound)
            {
                ZeroTierError error = ZeroTierErrors.ControllerNotInPeersError(controllerHex);
                EventRaised?.Invoke(this, error.ToDisplayText());
                _logger.LogWarning(error.ToLogText());
            }
            else if (controllerActivePaths == 0)
            {
                ZeroTierError error = ZeroTierErrors.ControllerNoActivePathError(controllerHex, $"v{controllerVersion}");
                EventRaised?.Invoke(this, error.ToDisplayText());
                _logger.LogWarning(error.ToLogText());
            }
            else
            {
                _logger.LogInformation(
                    "控制器 {Controller} 可达（v{Version}，{Latency}ms，{Paths} 条活动路径）—— "
                    + "若仍 REQUESTING_CONFIGURATION，请核对控制器上该成员的授权与 IP 分配设置。",
                    controllerHex, controllerVersion, controllerLatency, controllerActivePaths);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "探测控制器可达性失败。");
        }
    }

    /// <summary>
    /// 从 <c>zerotier-one -q info</c> 的输出（形如
    /// <c>200 info 08bd1dea74 1.16.2 ONLINE</c>）提取节点在线状态（ONLINE/OFFLINE）。
    /// </summary>
    private static string? ExtractOnlineState(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return null;
        }

        string[] parts = rawOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 ? parts[^1] : null;
    }

    /// <summary>把 listnetworks -j 的原始输出解析成 JSON（可能夹带非 JSON 前缀，从第一个 '[' 截取）。</summary>
    private static JsonDocument ParseListNetworksJson(string? rawOutput)
    {
        string json = rawOutput?.Trim() ?? string.Empty;
        int start = json.IndexOf('[');

        if (start < 0)
        {
            return JsonDocument.Parse("[]");
        }

        return JsonDocument.Parse(json[start..]);
    }

    /// <summary>
    /// 执行一条 zerotier-one -q 命令并解析结果。
    /// 带硬超时（COMMAND_TIMEOUT）：CLI 偶发挂起时杀掉进程返回失败，绝不无限等待。
    /// </summary>
    private async Task<CommandResult> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        string? executable = FindServiceExecutable();

        if (executable is null)
        {
            return CommandResult.Error("ZeroTier One 客户端未安装。");
        }

        // 命令里的参数要按空格拆开（例如 "orbit <a> <b>"、"-j"），
        // 用参数列表保证每个 token 独立传递、不受空格影响。
        List<string> arguments = ["-q"];
        arguments.AddRange(command.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // 全局串行：服务控制接口一次只处理一个连接，并发 CLI 会互相排队饿死。
        await _cliGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ProcessRunResult result = await RunProcessAsync(
                executable,
                Path.GetDirectoryName(executable) ?? string.Empty,
                arguments, COMMAND_TIMEOUT, cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                // 超时单独归类（ZT-2004），其余按命令失败处理。
                if (result.ExitCode == -1 && result.ErrorText.Contains("超时", StringComparison.Ordinal))
                {
                    ZeroTierError timeoutError = ZeroTierErrors.CommandTimeoutError(command, result.ErrorText);
                    _logger.LogWarning(timeoutError.ToLogText());
                }
                else
                {
                    _logger.LogWarning("zerotier-one 命令「{Command}」失败（退出码 {Code}）：{Error}", command, result.ExitCode, result.ErrorText);
                }

                return CommandResult.Error(result.ErrorText);
            }

            return CommandResult.Success(result.Output, ParseNodeId(result.Output));
        }
        finally
        {
            _cliGate.Release();
        }
    }

    /// <summary>
    /// 运行一个外部命令行工具并取回输出。
    /// 带硬超时：超时或取消时杀掉整个进程树；先读输出再等退出（避免管道缓冲互等）。
    /// </summary>
    private async Task<ProcessRunResult> RunProcessAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {executable}。");

            // 先开始读输出、再等退出：反过来在输出填满管道缓冲区时进程与读取会互等。
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时或外部取消都要杀掉子进程，避免残留。
                try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已自行退出 */ }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{Exe} 调用超时（{Seconds:N0} 秒），已终止。", executable, timeout.TotalSeconds);
                    return new ProcessRunResult(false, -1, string.Empty, $"命令超时（{timeout.TotalSeconds:N0} 秒）");
                }

                throw;
            }

            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return new ProcessRunResult(true, 0, output, string.Empty);
            }

            string message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            return new ProcessRunResult(
                false,
                process.ExitCode,
                output,
                string.IsNullOrWhiteSpace(message) ? $"命令失败（退出码 {process.ExitCode}）" : message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "运行 {Exe} 失败。", executable);
            return new ProcessRunResult(false, -1, string.Empty, ex.Message);
        }
    }

    /// <summary>从 info 输出里解析节点 ID（10 位十六进制的 ZeroTier 地址）。</summary>
    private static string ParseNodeId(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        // info 输出形如 "200 info <10位十六进制> 1.16.2 ONLINE"。
        // 不假设固定位置（不同版本 token 顺序可能不同），直接找长度为 10 的十六进制 token。
        foreach (string token in output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 10 && ulong.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return token;
            }
        }

        return string.Empty;
    }

    /// <summary>从 ping 输出里解析往返时间（毫秒）。</summary>
    private static ZeroTierPingResult ParsePingRoundTrip(string output, string host)
    {
        // Windows 中文系统形如「时间=2ms TTL=64」，英文系统形如「time=2ms TTL=64」。
        // 先找中文「时间」，找不到再找英文「time」，然后取等号后的数字。
        int index = output.IndexOf("时间", StringComparison.Ordinal);

        if (index >= 0)
        {
            int equals = output.IndexOf('=', index + 2);
            if (equals >= 0)
            {
                string number = new(output[(equals + 1)..].TakeWhile(char.IsDigit).ToArray());
                if (long.TryParse(number, out long ms))
                {
                    return new ZeroTierPingResult(true, ms, $"来自 {host} 的响应：时间 {ms} ms");
                }
            }

            return new ZeroTierPingResult(true, 0, $"来自 {host} 的响应");
        }

        index = output.IndexOf("time", StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            int equals = output.IndexOf('=', index + 4);
            int lt = output.IndexOf('<', index + 4);

            if (equals >= 0)
            {
                string number = new(output[(equals + 1)..].TakeWhile(char.IsDigit).ToArray());
                if (long.TryParse(number, out long ms))
                {
                    return new ZeroTierPingResult(true, ms, $"来自 {host} 的响应：时间 {ms} ms");
                }
            }
            else if (lt >= 0)
            {
                return new ZeroTierPingResult(true, 0, $"来自 {host} 的响应：时间 <1 ms");
            }
        }

        return new ZeroTierPingResult(true, 0, $"来自 {host} 的响应");
    }

    /// <summary>命令执行结果。</summary>
    private readonly record struct CommandResult(bool IsSuccess, string RawOutput, string NodeId, string ErrorText)
    {
        public static CommandResult Success(string output, string nodeId) => new(true, output, nodeId, string.Empty);

        public static CommandResult Error(string message) => new(false, string.Empty, string.Empty, message);
    }

    /// <summary>外部进程运行结果。</summary>
    private readonly record struct ProcessRunResult(bool IsSuccess, int ExitCode, string Output, string ErrorText);

    /// <summary>一次 listnetworks 解析出的状态快照（不可变，整体替换保证线程安全）。</summary>
    private sealed record NetworkSnapshot(string NodeId, IReadOnlyList<NetworkInfo> Networks)
    {
        public static readonly NetworkSnapshot Empty = new(string.Empty, Array.Empty<NetworkInfo>());
    }

    /// <summary>快照里单个网络的信息。</summary>
    private sealed record NetworkInfo(ulong Id, string Status, string VirtualIp);
}
