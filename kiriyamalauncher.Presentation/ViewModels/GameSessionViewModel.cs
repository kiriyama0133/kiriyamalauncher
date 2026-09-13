using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;
using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using Material.Icons;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using System.Globalization;
using System.IO;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly IAppSnapshot _snapshot;

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

    /// <summary>连接按钮上的文字。</summary>
    public string ConnectButtonText => IsConnected ? "已接入虚拟局域网" : "连接到虚拟局域网";

    public GameSessionViewModel(
        IGameService gameService,
        IAppNotifier appNotifier,
        IZeroTierService zeroTier,
        IAppSnapshot snapshot)
    {
        _gameService = gameService;
        _appNotifier = appNotifier;
        _zeroTier = zeroTier;
        _snapshot = snapshot;
        _zeroTier.EventRaised += OnZeroTierEventRaised;

        _ = LoadGamesAsync();
    }

    partial void OnIsInGameBoardChanged(bool value) => OnPropertyChanged(nameof(CanGoBack));

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectButtonText));

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

    /// <summary>返回游戏类别列表。</summary>
    protected override void OnGoBack()
    {
        IsInGameBoard = false;
        SelectedGame = null;
        StatusText = IDLE_LIST_TEXT;
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

        try
        {
            StatusText = "正在启动 ZeroTier 节点……";
            string storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kiriyamalauncher",
                "zerotier");

            ZeroTierStatus status = await _zeroTier.StartAsync(storagePath);
            _appNotifier.Info("ZeroTier 节点已启动", $"节点 ID：{status.NodeId}");

            string moonIdText = string.IsNullOrWhiteSpace(_snapshot.Profile.MoonServerIp)
                ? ZeroTierSettings.DefaultMoonId
                : _snapshot.Profile.MoonServerIp.Trim();

            if (ulong.TryParse(moonIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong moonId))
            {
                StatusText = $"正在围绕 moon {moonIdText} 建立轨道……";
                await _zeroTier.OrbitMoonAsync(moonId);
            }
            else
            {
                _appNotifier.Warning("Moon 设置有误", $"「{moonIdText}」不是合法的 moon 节点 ID，已跳过绕月。");
            }

            string networkIdText = ResolveNetworkId();
            StatusText = $"正在加入网络 {networkIdText}……";
            ulong networkId = ulong.Parse(networkIdText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ZeroTierStatus joined = await _zeroTier.JoinNetworkAsync(networkId);

            IsConnected = joined.IsTransportReady;
            VirtualIp = joined.IsTransportReady ? joined.VirtualIp : string.Empty;

            StatusText = joined.IsTransportReady
                ? $"已接入虚拟局域网，虚拟 IP：{joined.VirtualIp}"
                : $"网络状态：{joined.StatusText}";

            if (joined.IsTransportReady)
            {
                _appNotifier.Success("已连接到虚拟局域网", $"虚拟 IP：{joined.VirtualIp}");
            }
            else
            {
                string hint = string.Equals(
                    _snapshot.Profile.ZeroTierConnectionMode,
                    ZeroTierSettings.SelfHostedController,
                    StringComparison.OrdinalIgnoreCase)
                    ? "自建控制器模式下，请确认控制器已经启动并授权了这个节点。"
                    : "请到 my.zerotier.com 打开这个网络，把本机节点（见上一条通知里的节点 ID）勾选 Auth。";

                _appNotifier.Warning("网络尚未就绪", $"{joined.StatusText}\n{hint}");
            }
        }
        catch (Exception ex)
        {
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
        string configured = (_snapshot.Profile.ZeroTierNetworkId ?? string.Empty).Trim();
        return ZeroTierSettings.IsValidNetworkId(configured) ? configured : ZeroTierSettings.DefaultNetworkId;
    }

    private void OnOpenGameRequested(GameViewModel game)
    {
        SelectedGame = game;
        IsInGameBoard = true;
        StatusText = IDLE_BOARD_TEXT;
    }

    /// <summary>ZeroTier 事件回调来自原生线程，转回 UI 线程再更新界面。</summary>
    private void OnZeroTierEventRaised(object? sender, string message)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText = message;
            _appNotifier.Info("ZeroTier", message);
        });
}
