using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;
using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using Material.Icons;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using SukiUI.Dialogs;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 游戏联机页。
///
/// 两级结构：
/// 1. 游戏类别列表（默认）——数据来自 <see cref="IGameService"/>；
/// 2. 某个游戏的联机面板——只有一个「连接到虚拟局域网」，点一下就把本机接入 ZeroTier 网络。
///
/// 页面本身不持有数据。
/// </summary>
public partial class GameSessionViewModel : PageViewModel
{
    private const string IDLE_LIST_TEXT = "选一个游戏，进入它的联机面板。";
    private const string IDLE_BOARD_TEXT = "点右上角「连接到虚拟局域网」，接入之后同一个网络里的玩家就能互相看到。";

    private readonly IGameService _gameService;
    private readonly IAppNotifier _appNotifier;
    private readonly IZeroTierService _zeroTier;
    private readonly IGameLaunchService _launchService;
    private readonly IGameIntegrationViewModelFactory _integrationFactory;
    private readonly ISukiDialogManager _dialogManager;
    private readonly IAppSnapshot _snapshot;
    private readonly ILogger<GameSessionViewModel> _logger;

    /// <summary>当前已经加入的网络 ID（断开时用它退网）。</summary>
    private ulong _connectedNetworkId;

    /// <summary>断开之后忽略 ZeroTier 的进度事件（节点还在后台跑，别打扰用户）。</summary>
    private bool _suppressZeroTierEvents;

    public override string DisplayName => "游戏联机";

    public override MaterialIconKind Icon => MaterialIconKind.GamepadVariantOutline;

    /// <summary>进入某个游戏板块后，标题栏左侧会出现返回按钮。</summary>
    public override bool CanGoBack => IsInGameBoard;

    /// <summary>游戏类别列表。</summary>
    public ObservableCollection<GameViewModel> Games { get; } = [];

    /// <summary>当前进入的游戏；为 null 表示在游戏类别列表。</summary>
    [ObservableProperty]
    private GameViewModel? _selectedGame;

    /// <summary>是否已经进入某个游戏的板块。</summary>
    [ObservableProperty]
    private bool _isInGameBoard;

    /// <summary>
    /// 当前显示在 <see cref="SukiUI.Controls.SukiTransitioningContentControl"/> 里的页面对象：
    /// 列表态是 <see cref="GameListPageViewModel"/>，板块态是 <see cref="GameBoardPageViewModel"/>。
    /// 切换它就会触发 SukiUI 的 cross-fade 过渡。
    /// </summary>
    [ObservableProperty]
    private object? _activePageContent;

    /// <summary>游戏列表页顶部的状态提示（与板块态分开，避免互相覆盖）。</summary>
    [ObservableProperty]
    private string _listStatusText = "正在获取游戏列表……";

    /// <summary>游戏板块页顶部的状态提示。</summary>
    [ObservableProperty]
    private string _statusText = "点右上角「连接到虚拟局域网」，接入之后同一个网络里的玩家就能互相看到。";

    /// <summary>本机是否已经接入虚拟局域网。</summary>
    [ObservableProperty]
    private bool _isConnected;

    /// <summary>接入后的虚拟 IP（没接入时为空）。</summary>
    [ObservableProperty]
    private string _virtualIp = string.Empty;

    /// <summary>当前游戏的联机集成面板（这个游戏没有联机组件时为 null）。</summary>
    [ObservableProperty]
    private GameIntegrationViewModel? _integration;

    /// <summary>当前游戏是否配置了联机集成。</summary>
    public bool HasIntegration => Integration is not null;

    /// <summary>
    /// 是否显示注入组件面板：仅当「配置了联机集成」且「非客户端引擎」时。
    /// 客户端引擎下走真虚拟网卡，游戏直接互通，注入面板（选路径/启动/注入）不显示。
    /// </summary>
    public bool ShowInjection => HasIntegration && !IsClientBackend;

    /// <summary>
    /// 是否使用「嵌入的 ZeroTier 客户端」引擎（走系统虚拟网卡）。
    ///
    /// 客户端引擎下游戏进程直接通过真虚拟网卡互通，不再需要 civ6hook 注入组件，
    /// 因此联机面板里要隐藏注入相关 UI，连接后也不开启自动注入。
    /// </summary>
    public bool IsClientBackend => string.Equals(
        _snapshot.Preferences.ZeroTierTransportBackend,
        ZeroTierSettings.ClientBackend,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>是否使用「中继服务器」连接模式（服务端联机，走房间大厅）。</summary>
    public bool IsRelayServer => string.Equals(
        _snapshot.Preferences.ZeroTierConnectionMode,
        ZeroTierSettings.RelayServer,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>局域网设备卡片（轮询虚拟局域网，列出发现的设备）。</summary>
    public LanDevicesViewModel LanDevices { get; }

    /// <summary>房间大厅（中继服务器 / 服务端联机模式下显示）。</summary>
    public RoomsViewModel Rooms { get; }

    public GameSessionViewModel(
        IGameService gameService,
        IAppNotifier appNotifier,
        IZeroTierService zeroTier,
        IGameLaunchService launchService,
        IGameIntegrationViewModelFactory integrationFactory,
        ISukiDialogManager dialogManager,
        ILogger<GameSessionViewModel> logger,
        IAppSnapshot snapshot)
    {
        LanDevices = Ioc.Default.GetRequiredService<LanDevicesViewModel>();
        Rooms = Ioc.Default.GetRequiredService<RoomsViewModel>();
        _gameService = gameService;
        _appNotifier = appNotifier;
        _zeroTier = zeroTier;
        _launchService = launchService;
        _integrationFactory = integrationFactory;
        _dialogManager = dialogManager;
        _logger = logger;
        _snapshot = snapshot;
        _zeroTier.EventRaised += OnZeroTierEventRaised;
        _launchService.StatusChanged += OnIntegrationStatusChanged;
        _snapshot.Changed += OnSnapshotChanged;

        // 游戏隧道是 libzt 注入方案专用：客户端引擎（真虚拟网卡）下不允许自动开启，
        // 否则每次发现/扫描都会白启动一次并报「不是合法的本机虚拟 IP」之类的错。
        LanDevices.IsTunnelEnabled = !IsClientBackend;

        _activePageContent = new GameListPageViewModel(this);

        _ = LoadGamesAsync();
    }

    partial void OnIsInGameBoardChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));

    partial void OnIntegrationChanged(GameIntegrationViewModel? value)
    {
        OnPropertyChanged(nameof(HasIntegration));
        OnPropertyChanged(nameof(ShowInjection));
    }

    partial void OnIsConnectedChanged(bool value) => LanDevices.RefreshLocalInfo();

    [RelayCommand]
    private async Task LoadGamesAsync()
    {
        IsBusy = true;

        try
        {
            IReadOnlyList<GameDto> games = await _gameService.GetGamesAsync();

            Games.Clear();
            foreach (GameDto game in games)
            {
                Games.Add(new GameViewModel(game, OnOpenGameRequested));
            }

            ListStatusText = Games.Count > 0 ? IDLE_LIST_TEXT : "暂时没有支持联机的游戏。";
        }
        catch (Exception ex)
        {
            ListStatusText = $"获取游戏列表失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 返回游戏类别列表。
    /// 已经接入了虚拟局域网时先弹确认框（确认后断开连接再返回），没连接就直接返回。
    /// </summary>
    protected override async Task OnGoBackAsync()
    {
        try
        {
            if (IsConnected)
            {
                bool confirmed = await ConfirmDisconnectAsync();
                if (!confirmed)
                {
                    return;
                }

                IsBusy = true;
                try
                {
                    using (IDisposable loading = _appNotifier.ShowLoading("正在断开局域网", "正在离开虚拟局域网并清理虚拟网卡……"))
                    {
                        await DisconnectAsync();
                    }
                }
                finally
                {
                    IsBusy = false;
                }

                _appNotifier.Info("已断开虚拟局域网", "已断开连接并返回游戏列表。");
            }

            IsInGameBoard = false;
            SelectedGame = null;
            StatusText = IDLE_BOARD_TEXT;
            ActivePageContent = new GameListPageViewModel(this);

            // 离开板块就不再轮询这个游戏的进程（已经注入的组件不受影响）。
            _launchService.StopMonitoring();
            Integration = null;
            LanDevices.StopDiscovery();
        }
        catch (Exception ex)
        {
            // 命令里的异常会让进程直接挂掉，这里兜住并记进日志。
            _logger.LogError(ex, "返回游戏列表时出错。");
        }
    }

    /// <summary>断开虚拟局域网（停在当前页面）。</summary>
    [RelayCommand]
    private async Task DisconnectFromVirtualNetworkAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            using (IDisposable loading = _appNotifier.ShowLoading("正在断开局域网", "正在离开虚拟局域网并清理虚拟网卡……"))
            {
                await DisconnectAsync();
            }

            StatusText = IDLE_BOARD_TEXT;
            _appNotifier.Info("已断开虚拟局域网", "已断开连接（后台节点保持在线，可以直接重新连接）。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "断开虚拟局域网时出错。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>确认是否断开：SukiUI 的「确认 / 取消」对话框。</summary>
    private Task<bool> ConfirmDisconnectAsync()
        => _dialogManager.CreateDialog()
            .WithTitle("断开虚拟局域网？")
            .WithContent("返回上一页会断开当前的虚拟局域网连接，其他玩家将无法再通过虚拟 IP 访问你。")
            .WithYesNoResult("确认", "取消")
            .Dismiss().ByClickingBackground()
            .TryShowAsync();

    /// <summary>
    /// 断开虚拟局域网。
    ///
    /// 只做「应用层断开」：本程序不再参与这个网络、清掉虚拟 IP 与按钮状态。
    /// 底层不调用 libzt 的退网 / 停止接口 —— 实测 zts_net_leave 会让进程原生崩溃，
    /// 而 zts_node_free 之后又无法在同一进程里重新初始化；节点保持在线，
    /// 所以「重新连接」只是再 Join 一次，既不会崩也不会卡。
    /// </summary>
    private async Task DisconnectAsync()
    {
        // 中继服务器模式：断开 = 离开房间大厅 + 应用层断开 ZeroTier 网络（节点保持在线）。
        if (IsRelayServer)
        {
            Rooms.Disconnect();
            _logger.LogInformation("已断开中继服务器房间大厅。");

            ulong relayNetworkId = _connectedNetworkId != 0 ? _connectedNetworkId : ParseNetworkIdOrDefault();
            if (relayNetworkId != 0)
            {
                try
                {
                    await _zeroTier.DisconnectAsync(relayNetworkId);
                    _logger.LogInformation("中继模式断开 ZeroTier 网络（应用层），网络 ID = {NetworkId:x}。", relayNetworkId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "中继模式断开 ZeroTier 网络失败，网络 ID = {NetworkId:x}。", relayNetworkId);
                }
            }

            _connectedNetworkId = 0;
            IsConnected = false;
            VirtualIp = string.Empty;
            _suppressZeroTierEvents = true;
            return;
        }

        ulong networkId = _connectedNetworkId != 0 ? _connectedNetworkId : ParseNetworkIdOrDefault();

        _logger.LogInformation("开始断开虚拟局域网，网络 ID = {NetworkId:x}。", networkId);

        try
        {
            await _zeroTier.DisconnectAsync(networkId);
            _logger.LogInformation("断开完成（应用层），网络 ID = {NetworkId:x}。", networkId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "断开虚拟局域网失败，网络 ID = {NetworkId:x}。", networkId);
            _appNotifier.Warning("断开时出错", ex.Message);
        }

        _connectedNetworkId = 0;
        IsConnected = false;
        VirtualIp = string.Empty;
        _launchService.StopAutoInject();
        // libzt 的节点不退出网络，之后的网络事件不再往界面上抛。
        _suppressZeroTierEvents = true;
    }

    /// <summary>
    /// 中继服务器模式（服务端联机）：连接房间大厅。
    ///
    /// 中继服务器模式本质上就是「自建控制器」：服务端自建 ZeroTier Controller 管理房间网络，
    /// 用 Flow Rules + Tag 做房间隔离。所以连接流程要分两步：
    ///   1. 先像自建控制器模式一样接入服务器管理的 ZeroTier 网络（启动节点 → 绕 moon → 加入
    ///      自建控制器网络 ID），拿到本机节点 ID 与虚拟 IP；
    ///   2. 再连 HTTP 房间大厅拉取房间列表。
    /// 之后加入房间时服务端才能用 nodeId/virtualIp 给本机打房间 Tag。
    /// </summary>
    private async Task ConnectToRelayServerAsync()
    {
        _logger.LogInformation("开始连接中继服务器（服务端联机）。");

        // 第一步：接入自建控制器管理的 ZeroTier 网络（拿节点 ID + 虚拟 IP）。
        StatusText = "正在接入中继服务器的 ZeroTier 网络……";
        bool networkReady = await ConnectZeroTierAsync(allowOfficialFallback: false);

        if (!networkReady)
        {
            IsConnected = false;
            VirtualIp = string.Empty;
            return;
        }

        // 第二步：连 HTTP 房间大厅。
        StatusText = "正在连接房间大厅……";
        await Rooms.ConnectAsync();

        IsConnected = Rooms.IsConnected;
        VirtualIp = IsConnected ? _zeroTier.LocalVirtualIp : string.Empty;
        StatusText = Rooms.StatusText;

        if (Rooms.IsConnected)
        {
            _appNotifier.Success("已连接中继服务器", $"已接入 ZeroTier 网络，{Rooms.StatusText}");
        }
        else
        {
            _appNotifier.Warning("连接中继服务器失败", Rooms.StatusText);
        }
    }

    /// <summary>把偏好里的网络 ID 解析成数字；无效时返回 0。</summary>
    private ulong ParseNetworkIdOrDefault()
        => ulong.TryParse(ResolveNetworkId(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong networkId)
            ? networkId
            : 0;

    /// <summary>
    /// 接入 ZeroTier 网络：启动内嵌节点 → 绕 moon → 加入目标网络，返回是否拿到虚拟 IP（transport ready）。
    /// 中继服务器模式与自建控制器模式共用这一段；官方模式也可复用。
    /// </summary>
    /// <param name="allowOfficialFallback">
    /// 为 true 时（官方/自建控制器模式）网络 ID 无效会回退到官方默认网络；为 false（中继服务器模式）
    /// 时只认「自建控制器网络 ID」，拿不到就直接终止。
    /// </param>
    private async Task<bool> ConnectZeroTierAsync(bool allowOfficialFallback)
    {
        _logger.LogInformation(
            "开始连接虚拟局域网：引擎 = {Backend}。",
            IsClientBackend ? "Client（嵌入的 ZeroTier 客户端，走系统虚拟网卡）" : "Sockets（libzt 内嵌节点）");

        StatusText = "正在启动 ZeroTier 节点……";
        string storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kiriyamalauncher",
            "zerotier");

        // 中继服务器模式本质就是自建控制器；moon 是自己搭的根节点，只有自建控制器/中继模式才绕月。
        bool isSelfHosted = IsRelayServer || string.Equals(
            _snapshot.Preferences.ZeroTierConnectionMode,
            ZeroTierSettings.SelfHostedController,
            StringComparison.OrdinalIgnoreCase);

        // 把连接模式告诉后端：客户端引擎在官方模式下会自动把 planet 校准为官方根
        // 服务器文件（首装后 planet 可能是自建污染文件，导致官方控制台看不到入网请求）。
        _zeroTier.ConnectionMode = isSelfHosted
            ? ZeroTierSettings.SelfHostedController
            : ZeroTierSettings.OfficialController;

        ZeroTierStatus status = await _zeroTier.StartAsync(storagePath);

        // 启动失败（客户端未装 / 服务装不上、起不来 / 节点探测失败）直接终止，不再往下加入网络。
        if (!status.IsStarted)
        {
            string reason = status.Error is not null ? status.Error.ToDisplayText() : status.StatusText;
            _logger.LogWarning("ZeroTier 节点启动失败：{Reason}", status.Error?.ToLogText() ?? status.StatusText);
            StatusText = $"ZeroTier 启动失败：{reason}";
            _appNotifier.Error("ZeroTier 启动失败", reason);
            return false;
        }

        _logger.LogInformation("ZeroTier 节点已启动：{NodeId}", status.NodeId);
        _appNotifier.Info(
            "ZeroTier 节点已启动",
            string.IsNullOrWhiteSpace(status.NodeId) ? "节点 ID 获取中……" : $"节点 ID：{status.NodeId}");

        if (isSelfHosted)
        {
            string moonIdText = string.IsNullOrWhiteSpace(_snapshot.Preferences.MoonServerIp)
                ? ZeroTierSettings.DefaultMoonId
                : _snapshot.Preferences.MoonServerIp.Trim();

            if (ulong.TryParse(moonIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong moonId))
            {
                StatusText = $"正在围绕 moon {moonIdText} 建立轨道……";
                await _zeroTier.OrbitMoonAsync(moonId);
            }
            else
            {
                _appNotifier.Warning("Moon 设置有误", $"「{moonIdText}」不是合法的 moon 节点 ID，已跳过绕月。");
            }
        }

        string networkIdText = ResolveNetworkId();

        // 中继服务器模式只认自建控制器网络 ID；自建控制器模式同理，拿不到就终止并提示。
        if (!allowOfficialFallback && string.IsNullOrWhiteSpace(networkIdText))
        {
            StatusText = "请先到设置里填写「自建控制器网络 ID」。";
            _appNotifier.Warning(
                "缺少自建控制器网络 ID",
                "中继服务器模式依赖服务端自建的 ZeroTier 控制器网络。请先到设置里填写「自建控制器网络 ID」（服务端 controller 生成的 16 位网络 ID）。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(networkIdText))
        {
            StatusText = "请先到设置里填写「自建控制器网络 ID」。";
            _appNotifier.Warning("缺少自建控制器网络 ID", "自建控制器模式下，请先到设置里填写你的控制器生成的 16 位网络 ID。");
            return false;
        }

        StatusText = $"正在加入网络 {networkIdText}……";
        ulong networkId = ulong.Parse(networkIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ZeroTierStatus joined = await _zeroTier.JoinNetworkAsync(networkId);
        _logger.LogInformation(
            "加入网络 {NetworkId:x} 结果：ready = {Ready}，虚拟 IP = {VirtualIp}，状态 = {StatusText}",
            networkId, joined.IsTransportReady, joined.VirtualIp, joined.StatusText);

        _connectedNetworkId = networkId;
        VirtualIp = joined.IsTransportReady ? joined.VirtualIp : string.Empty;

        // 接入虚拟局域网后：
        // - 客户端引擎（真虚拟网卡）：游戏进程直接通过系统网卡互通，无需注入组件；
        // - Sockets 引擎（libzt 内嵌）：需要开启常驻自动注入，让游戏进程出现就注入 Hook。
        if (joined.IsTransportReady)
        {
            if (!IsClientBackend)
            {
                _launchService.StartAutoInject();

                // 隧道是 Hook ↔ 启动器之间的管道：自动开一下，免得用户忘了点
                // （忘了开的话，游戏里的广播只能被丢弃）。
                LanDevices.TryAutoStartTunnel();
            }
        }

        StatusText = joined.IsTransportReady
            ? $"已接入虚拟局域网，虚拟 IP：{joined.VirtualIp}"
            : $"网络状态：{joined.StatusText}";

        if (joined.IsTransportReady)
        {
            _appNotifier.Success("已连接到虚拟局域网", $"虚拟 IP：{joined.VirtualIp}");

            // 软断开的副作用：之前连过的网络还在（libzt 无法退网），换过 Network ID 就会同时挂在多个网络里。
            ulong[] otherNetworks = _zeroTier.JoinedNetworkIds.Where(id => id != networkId).ToArray();
            if (otherNetworks.Length > 0)
            {
                string others = string.Join('、', otherNetworks.Select(id => id.ToString("x16")));
                _appNotifier.Warning("仍停留在旧网络", $"libzt 无法退网，节点还连着：{others}。要彻底离开请重启应用。");
            }
        }
        else
        {
            string hint = isSelfHosted
                ? "自建控制器模式下，请确认控制器已经启动并授权了这个节点。"
                : "请到 my.zerotier.com 打开这个网络，把本机节点（见上一条通知里的节点 ID）勾选 Auth。";

            string reason = joined.Error is not null ? joined.Error.ToDisplayText() : joined.StatusText;
            _appNotifier.Warning("网络尚未就绪", $"{reason}\n{hint}");
        }

        return joined.IsTransportReady;
    }

    /// <summary>
    /// 连接到虚拟局域网：启动内嵌 ZeroTier 节点 → 围绕 moon 建立轨道 → 加入网络 → 报告虚拟 IP。
    /// </summary>
    [RelayCommand]
    private async Task ConnectToVirtualNetworkAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        _suppressZeroTierEvents = false;

        try
        {
            // 中继服务器模式：接入自建控制器网络 + 连 HTTP 房间大厅（服务端联机）。
            if (IsRelayServer)
            {
                await ConnectToRelayServerAsync();
                return;
            }

            bool networkReady = await ConnectZeroTierAsync(allowOfficialFallback: true);
            IsConnected = networkReady;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接虚拟局域网失败。");
            IsConnected = false;
            VirtualIp = string.Empty;
            StatusText = $"连接虚拟局域网失败：{ex.Message}";
            _appNotifier.Error("连接虚拟局域网失败", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 取当前连接模式对应的网络 ID：
    ///   - 自建控制器模式 / 中继服务器模式：都用「自建控制器网络 ID」（没填返回空串，由调用方提示）；
    ///   - 官方模式：用「ZeroTier 网络 ID」，没填或无效回退到官方默认网络。
    /// </summary>
    private string ResolveNetworkId()
    {
        string mode = _snapshot.Preferences.ZeroTierConnectionMode;

        if (string.Equals(mode, ZeroTierSettings.SelfHostedController, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, ZeroTierSettings.RelayServer, StringComparison.OrdinalIgnoreCase))
        {
            string selfHosted = (_snapshot.Preferences.SelfHostedNetworkId ?? string.Empty).Trim();
            return ZeroTierSettings.IsValidNetworkId(selfHosted) ? selfHosted : string.Empty;
        }

        string official = (_snapshot.Preferences.ZeroTierNetworkId ?? string.Empty).Trim();
        return ZeroTierSettings.IsValidNetworkId(official) ? official : ZeroTierSettings.DefaultNetworkId;
    }

    private void OnOpenGameRequested(GameViewModel game)
    {
        SelectedGame = game;
        IsInGameBoard = true;
        StatusText = IDLE_BOARD_TEXT;
        ActivePageContent = new GameBoardPageViewModel(this, game);

        // 告诉房间大厅当前板块：创建房间时按板块上报，房间列表也只显示本板块的房间。
        Rooms.SetActiveGame(game.IntegrationId);

        GameIntegrationInfoDto? integration = _launchService.FindIntegration(game.IntegrationId);

        // 客户端引擎走真虚拟网卡，游戏直接互通，不需要注入组件：跳过集成面板与进程监视。
        if (integration is null || IsClientBackend)
        {
            _launchService.StopMonitoring();
            Integration = null;
            return;
        }

        GameIntegrationViewModel viewModel = _integrationFactory.Create(integration);
        Integration = viewModel;

        // 进入板块就开始轮询虚拟局域网，把在线设备列出来。
        LanDevices.StartDiscovery();

        // 开始轮询这个游戏的进程：检测到就自动注入。
        _launchService.BeginMonitoring(integration.IntegrationId);
        _ = viewModel.InitializeAsync();
    }

    /// <summary>联机集成状态回调来自后台线程（进程轮询），转回 UI 线程再更新界面。</summary>
    private void OnIntegrationStatusChanged(object? sender, GameIntegrationStatusDto status)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Integration?.ApplyStatus(status));

    /// <summary>偏好变化（例如切换传输引擎 / 连接模式）时，刷新注入面板与房间大厅的显隐。</summary>
    private void OnSnapshotChanged(object? sender, EventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ShowInjection));
            OnPropertyChanged(nameof(IsRelayServer));
            LanDevices.IsTunnelEnabled = !IsClientBackend;

            // 退出登录后，需要鉴权的联机/房间状态都失效：断开连接并回到游戏列表。
            if (!_snapshot.User.IsSignedIn)
            {
                _ = ResetAfterSignOutAsync();
            }
        });

    /// <summary>退出登录后重置联机页面：断开连接、清房间状态、回到游戏列表。</summary>
    private async Task ResetAfterSignOutAsync()
    {
        try
        {
            if (IsConnected || Rooms.IsConnected || Rooms.IsInRoom)
            {
                await DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "退出登录后重置联机页面失败。");
        }
        finally
        {
            Rooms.Disconnect();
            IsInGameBoard = false;
            SelectedGame = null;
            StatusText = IDLE_BOARD_TEXT;
            ActivePageContent = new GameListPageViewModel(this);
            _launchService.StopMonitoring();
            Integration = null;
            LanDevices.StopDiscovery();
        }
    }

    /// <summary>ZeroTier 事件回调来自原生线程，转回 UI 线程再更新界面。</summary>
    private void OnZeroTierEventRaised(object? sender, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation("ZeroTier 事件：{Message}", message);

            if (_suppressZeroTierEvents)
            {
                return;
            }

            StatusText = message;
            _appNotifier.Info("ZeroTier", message);
        });
}
