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

    /// <summary>页面顶部的状态提示。</summary>
    [ObservableProperty]
    private string _statusText = "正在获取游戏列表……";

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

    /// <summary>局域网设备卡片（轮询虚拟局域网，列出发现的设备）。</summary>
    public LanDevicesViewModel LanDevices { get; }

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

        _ = LoadGamesAsync();
    }

    partial void OnIsInGameBoardChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));

    partial void OnIntegrationChanged(GameIntegrationViewModel? value) => OnPropertyChanged(nameof(HasIntegration));

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

            StatusText = Games.Count > 0 ? IDLE_LIST_TEXT : "暂时没有支持联机的游戏。";
        }
        catch (Exception ex)
        {
            StatusText = $"获取游戏列表失败：{ex.Message}";
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

            await DisconnectAsync();
            _appNotifier.Info("已断开虚拟局域网", "已断开连接并返回游戏列表。");
            }

            IsInGameBoard = false;
            SelectedGame = null;
            StatusText = IDLE_LIST_TEXT;

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
        try
        {
            await DisconnectAsync();
            StatusText = IDLE_BOARD_TEXT;
            _appNotifier.Info("已断开虚拟局域网", "已断开连接（后台节点保持在线，可以直接重新连接）。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "断开虚拟局域网时出错。");
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

    /// <summary>把偏好里的网络 ID 解析成数字；无效时返回 0。</summary>
    private ulong ParseNetworkIdOrDefault()
        => ulong.TryParse(ResolveNetworkId(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong networkId)
            ? networkId
            : 0;

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
            StatusText = "正在启动 ZeroTier 节点……";
            string storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kiriyamalauncher",
                "zerotier");

            ZeroTierStatus status = await _zeroTier.StartAsync(storagePath);
            _logger.LogInformation("ZeroTier 节点已启动：{NodeId}", status.NodeId);
            _appNotifier.Info("ZeroTier 节点已启动", $"节点 ID：{status.NodeId}");

            // moon 是自己搭的根节点：只有「自建控制器」模式才需要绕月，
            // my.zerotier.com 模式用官方根节点就够了。
            bool isSelfHosted = string.Equals(
                _snapshot.Preferences.ZeroTierConnectionMode,
                ZeroTierSettings.SelfHostedController,
                StringComparison.OrdinalIgnoreCase);

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
            StatusText = $"正在加入网络 {networkIdText}……";
            ulong networkId = ulong.Parse(networkIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ZeroTierStatus joined = await _zeroTier.JoinNetworkAsync(networkId);
            _logger.LogInformation(
                "加入网络 {NetworkId:x} 结果：ready = {Ready}，虚拟 IP = {VirtualIp}，状态 = {StatusText}",
                networkId, joined.IsTransportReady, joined.VirtualIp, joined.StatusText);

            _connectedNetworkId = networkId;
            IsConnected = joined.IsTransportReady;
            VirtualIp = joined.IsTransportReady ? joined.VirtualIp : string.Empty;

            // 接入虚拟局域网后就开启常驻自动注入：即使不在游戏板块，游戏进程一出现也会自动注入。
            if (joined.IsTransportReady)
            {
                _launchService.StartAutoInject();

                // 隧道是 Hook ↔ 启动器之间的管道：自动开一下，免得用户忘了点
                // （忘了开的话，游戏里的广播只能被丢弃）。
                LanDevices.TryAutoStartTunnel();
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

                _appNotifier.Warning("网络尚未就绪", $"{joined.StatusText}\n{hint}");
            }
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

    /// <summary>取偏好里的网络 ID；没有设置或者格式不对时回退到默认网络。</summary>
    private string ResolveNetworkId()
    {
        string configured = (_snapshot.Preferences.ZeroTierNetworkId ?? string.Empty).Trim();
        return ZeroTierSettings.IsValidNetworkId(configured) ? configured : ZeroTierSettings.DefaultNetworkId;
    }

    private void OnOpenGameRequested(GameViewModel game)
    {
        SelectedGame = game;
        IsInGameBoard = true;
        StatusText = IDLE_BOARD_TEXT;

        GameIntegrationInfoDto? integration = _launchService.FindIntegration(game.IntegrationId);

        if (integration is null)
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
