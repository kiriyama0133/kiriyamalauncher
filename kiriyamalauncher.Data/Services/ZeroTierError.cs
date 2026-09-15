using System;

namespace kiriyamalauncher.Data;

/// <summary>
/// ZeroTier 错误的分类维度。每个错误码归属一个分类，便于日志检索、文档归类与界面分级展示。
/// </summary>
public enum ZeroTierErrorCategory
{
    /// <summary>配置类：planet / moon / 网络 ID / 连接模式等本地配置错误。</summary>
    Config,

    /// <summary>服务类：ZeroTier 系统服务未安装 / 未注册 / 启动失败 / 僵死。</summary>
    Service,

    /// <summary>网络类：加入网络失败、等待虚拟 IP 超时、网络状态异常。</summary>
    Network,

    /// <summary>授权类：节点未被控制器授权（Auth）、被拒绝访问。</summary>
    Auth,

    /// <summary>离线类：节点连不上根服务器 / moon，处于 OFFLINE。</summary>
    Offline,

    /// <summary>系统/环境类：非 Windows、无管理员权限、进程异常等。</summary>
    System,
}

/// <summary>
/// 结构化 ZeroTier 错误。
///
/// 与之前「只有一句话」的区别：
///   - <see cref="Code"/>：稳定的错误码（形如 ZT-1001），可搜索、可写进文档、可被用户复制上报；
///   - <see cref="Category"/>：错误分类，用于日志检索与界面分级（错误 / 警告 / 提示）；
///   - <see cref="Summary"/>：一句话给用户看的结论；
///   - <see cref="Detail"/>：展开后的技术细节（含证据），写进日志；
///   - <see cref="Hint"/>：可选的「下一步怎么做」建议；
///   - <see cref="Evidence"/>：原始证据（命令、退出码、原始输出），排查时最关键的一手材料。
/// </summary>
public sealed record ZeroTierError(
    string Code,
    ZeroTierErrorCategory Category,
    string Summary,
    string? Detail = null,
    string? Hint = null,
    string? Evidence = null)
{
    /// <summary>把错误渲染成给界面显示的一句话（含错误码，方便用户复制上报）。</summary>
    public string ToDisplayText() => $"[{Code}] {Summary}";

    /// <summary>把错误渲染成写入日志的完整一行（含分类、细节、证据）。</summary>
    public string ToLogText()
    {
        string text = $"[{Code}/{Category}] {Summary}";

        if (!string.IsNullOrWhiteSpace(Detail))
        {
            text += $" 细节：{Detail}";
        }

        if (!string.IsNullOrWhiteSpace(Hint))
        {
            text += $" 建议：{Hint}";
        }

        if (!string.IsNullOrWhiteSpace(Evidence))
        {
            text += $" 证据：{Evidence}";
        }

        return text;
    }

    public override string ToString() => ToLogText();
}

/// <summary>
/// ZeroTier 错误码定义与工厂方法。
///
/// 错误码分段约定（前缀 ZT，三位分类 + 三位序号）：
///   - 1xxx 配置类（Config）
///   - 2xxx 服务类（Service）
///   - 3xxx 网络类（Network）
///   - 4xxx 授权类（Auth）
///   - 5xxx 离线类（Offline）
///   - 6xxx 系统/环境类（System）
///
/// 新增错误时：在此类里加一个 const 码值 + 一个工厂方法，保证错误码不重复、语义集中管理。
/// </summary>
public static class ZeroTierErrors
{
    // —— 配置类（1xxx）——
    /// <summary>planet 文件是自建/污染文件，与当前连接模式（官方控制器）冲突。</summary>
    public const string PlanetConflict = "ZT-1001";

    /// <summary>moon 文件无效（endpoint 为回环地址等）。</summary>
    public const string InvalidMoon = "ZT-1002";

    /// <summary>网络 ID 无效。</summary>
    public const string InvalidNetworkId = "ZT-1003";

    // —— 服务类（2xxx）——
    /// <summary>ZeroTier One 未安装。</summary>
    public const string NotInstalled = "ZT-2001";

    /// <summary>系统服务未注册。</summary>
    public const string ServiceNotRegistered = "ZT-2002";

    /// <summary>系统服务启动失败/超时。</summary>
    public const string ServiceStartFailed = "ZT-2003";

    /// <summary>命令执行超时（控制面可能僵死）。</summary>
    public const string CommandTimeout = "ZT-2004";

    /// <summary>存在抢占端口的野实例。</summary>
    public const string ForeignInstance = "ZT-2005";

    /// <summary>控制面失联，自动恢复失败。</summary>
    public const string ControlPlaneDead = "ZT-2006";

    // —— 网络类（3xxx）——
    /// <summary>加入网络命令失败。</summary>
    public const string JoinFailed = "ZT-3001";

    /// <summary>等待虚拟 IP 超时。</summary>
    public const string JoinTimeout = "ZT-3002";

    /// <summary>网络不存在（NOT_FOUND）。</summary>
    public const string NetworkNotFound = "ZT-3003";

    // —— 授权类（4xxx）——
    /// <summary>节点未被控制器授权（ACCESS_DENIED）。</summary>
    public const string AccessDenied = "ZT-4001";

    /// <summary>停留在 REQUESTING_CONFIGURATION（控制器没下发配置）。</summary>
    public const string RequestingConfiguration = "ZT-4002";

    // —— 离线类（5xxx）——
    /// <summary>节点 OFFLINE（连不上根服务器/moon）。</summary>
    public const string NodeOffline = "ZT-5001";

    /// <summary>peers 里找不到控制器节点。</summary>
    public const string ControllerNotInPeers = "ZT-5002";

    /// <summary>控制器可达但无活动路径。</summary>
    public const string ControllerNoActivePath = "ZT-5003";

    // —— 系统/环境类（6xxx）——
    /// <summary>当前平台不是 Windows。</summary>
    public const string UnsupportedPlatform = "ZT-6001";

    /// <summary>缺少管理员权限。</summary>
    public const string RequiresAdmin = "ZT-6002";

    // —— 工厂方法 ——

    public static ZeroTierError PlanetConflictError(string? evidence = null) => new(
        PlanetConflict, ZeroTierErrorCategory.Config,
        "planet 文件与当前连接模式冲突（官方控制器必须使用官方 planet 根服务器）",
        "本地 planet 是自建/污染文件，节点被导向了错误的根服务器宇宙，join 请求根本到不了官方控制器。",
        "删除 C:\\ProgramData\\ZeroTier\\One\\planet（服务重启会自动重下官方 planet）后重试。",
        evidence);

    public static ZeroTierError InvalidMoonError(string? evidence = null) => new(
        InvalidMoon, ZeroTierErrorCategory.Config,
        "moon 文件无效（endpoint 指向回环地址）",
        "该 moon 是用 initmoon 默认模板生成的，endpoint 从未改成真实公网 IP，orbit 它等于 orbit 自己。",
        "在服务器上重新生成 moon 并填写真实公网 IP，或直接删除该 moon 文件。",
        evidence);

    public static ZeroTierError InvalidNetworkIdError(string? evidence = null) => new(
        InvalidNetworkId, ZeroTierErrorCategory.Config,
        "网络 ID 无效",
        "无法把配置的网络 ID 解析为合法的 16 位十六进制 ZeroTier 网络地址。",
        "检查设置里的网络 ID 是否为 16 位十六进制字符。",
        evidence);

    public static ZeroTierError NotInstalledError(string? evidence = null) => new(
        NotInstalled, ZeroTierErrorCategory.Service,
        "ZeroTier One 客户端未安装",
        "找不到任何 zerotier-one 可执行文件，且随应用发行的安装包也无法完成安装。",
        "以管理员身份运行本程序，或手动安装 ZeroTier One 后重试。",
        evidence);

    public static ZeroTierError ServiceNotRegisteredError(string? evidence = null) => new(
        ServiceNotRegistered, ZeroTierErrorCategory.Service,
        "ZeroTier 系统服务未注册",
        "Windows 服务 ZeroTierOneService 在 SCM 中不可见，且自动注册（exe -I / MSI）均失败。",
        "以管理员身份运行，或重启 Windows 后重试（服务可能处于待删除状态）。",
        evidence);

    public static ZeroTierError ServiceStartFailedError(string? evidence = null) => new(
        ServiceStartFailed, ZeroTierErrorCategory.Service,
        "ZeroTier 系统服务启动失败",
        "服务已注册但无法进入 RUNNING 状态。",
        "查看 Windows 服务里 ZeroTierOneService 的启动类型，确认未被禁用或拦截。",
        evidence);

    public static ZeroTierError CommandTimeoutError(string command, string? evidence = null) => new(
        CommandTimeout, ZeroTierErrorCategory.Service,
        $"ZeroTier 命令「{command}」执行超时",
        "CLI 在规定时间内未返回，通常意味着服务控制接口僵死或端口被抢。",
        "程序会自动尝试重启服务；若反复出现，检查是否有代理软件虚拟网卡劫持了 127.0.0.1。",
        evidence);

    public static ZeroTierError ForeignInstanceError(string? evidence = null) => new(
        ForeignInstance, ZeroTierErrorCategory.Service,
        "检测到多个 ZeroTier 实例抢占端口",
        "存在非服务会话的前台 zerotier-one 实例，与服务双绑同一端口，CLI 随机撞到僵尸进程。",
        "程序会自动终止多余实例；若反复出现，请勿手动双击 zerotier-one 启动。",
        evidence);

    public static ZeroTierError ControlPlaneDeadError(string reason, string? evidence = null) => new(
        ControlPlaneDead, ZeroTierErrorCategory.Service,
        "ZeroTier 服务控制接口失联且自动恢复失败",
        $"服务进程存在但不再响应命令（{reason}），多次重启服务后仍未恢复。",
        "检查代理软件是否劫持回环连接，把 127.0.0.1 加入直连名单。",
        evidence);

    public static ZeroTierError JoinFailedError(string? evidence = null) => new(
        JoinFailed, ZeroTierErrorCategory.Network,
        "加入网络失败",
        "zerotier-one -q join 命令执行失败。",
        "检查网络 ID 是否正确、服务是否正常运行。",
        evidence);

    public static ZeroTierError JoinTimeoutError(int seconds, string? evidence = null) => new(
        JoinTimeout, ZeroTierErrorCategory.Network,
        $"等待虚拟 IP 超时（{seconds} 秒）",
        "加入网络已受理，但在超时窗口内一直没拿到虚拟网卡地址。",
        "结合日志里的节点在线状态与 peers 概况定位根因。",
        evidence);

    public static ZeroTierError NetworkNotFoundError(string? evidence = null) => new(
        NetworkNotFound, ZeroTierErrorCategory.Network,
        "网络不存在（NOT_FOUND）",
        "ZeroTier 服务返回 NOT_FOUND，网络上没有这个网络。",
        "检查网络 ID 是否填写正确。",
        evidence);

    public static ZeroTierError AccessDeniedError(string? evidence = null) => new(
        AccessDenied, ZeroTierErrorCategory.Auth,
        "节点未被授权（ACCESS_DENIED）",
        "控制器拒绝了这个节点，因为还没有给它勾选 Auth。",
        "到控制器后台找到本节点并勾选 Auth 后重试。",
        evidence);

    public static ZeroTierError RequestingConfigurationError(string? evidence = null) => new(
        RequestingConfiguration, ZeroTierErrorCategory.Auth,
        "等待控制器下发网络配置",
        "节点已请求配置但控制器一直没响应 —— 授权缺失或控制器不可达。",
        "确认节点已授权；若已授权仍卡住，检查控制器是否可达（见 peers 探测）。",
        evidence);

    public static ZeroTierError NodeOfflineError(string? evidence = null) => new(
        NodeOffline, ZeroTierErrorCategory.Offline,
        "ZeroTier 节点 OFFLINE（连不上根服务器/moon）",
        "节点无法连上任何根服务器，此时控制器授权了配置也推不下来。",
        "检查服务器 9993/UDP 可达性，以及 planet/moon 配置是否正确。",
        evidence);

    public static ZeroTierError ControllerNotInPeersError(string controllerHex, string? evidence = null) => new(
        ControllerNotInPeers, ZeroTierErrorCategory.Offline,
        $"peers 里没有控制器节点 {controllerHex}",
        "控制器没有注册到本节点所用的根服务器，节点无法获知控制器位置，配置推不下来。",
        "在控制器所在机器上也放置相同的 planet 文件并重启其 zerotier-one 服务。",
        evidence);

    public static ZeroTierError ControllerNoActivePathError(string controllerHex, string? evidence = null) => new(
        ControllerNoActivePath, ZeroTierErrorCategory.Offline,
        $"控制器 {controllerHex} 无活动路径",
        "控制器在 peers 里但没有任何活动连接，双方可能都躲在 NAT 后且根无法中继。",
        "检查控制器 planet/moon 配置，或让控制器使用同一自建根。",
        evidence);

    public static ZeroTierError UnsupportedPlatformError() => new(
        UnsupportedPlatform, ZeroTierErrorCategory.System,
        "当前平台不支持嵌入的 ZeroTier 客户端",
        "嵌入客户端后端目前只支持 Windows。",
        "请在设置里切换为 ZeroTier.Sockets（libzt）引擎。",
        null);

    public static ZeroTierError RequiresAdminError(string? evidence = null) => new(
        RequiresAdmin, ZeroTierErrorCategory.System,
        "缺少管理员权限",
        "安装/注册 ZeroTier 服务需要管理员权限，当前进程权限不足。",
        "以管理员身份重新运行本程序。",
        evidence);
}
