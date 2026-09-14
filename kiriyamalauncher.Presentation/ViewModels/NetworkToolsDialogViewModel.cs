using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using SukiUI.Dialogs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 网络工具对话框：查看本机虚拟 IP、轮询发现同一虚拟局域网里的设备、以及对设备做 Ping 探测。
/// </summary>
public partial class NetworkToolsDialogViewModel : BaseViewModel
{
    private static readonly TimeSpan PING_TIMEOUT = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SCAN_INTERVAL = TimeSpan.FromSeconds(5);
    private const int MAX_PING_LINES = 50;

    private readonly ISukiDialog _dialog;
    private readonly IZeroTierService _zeroTier;
    private readonly IVirtualLanDiscoveryService _discovery;
    private readonly IAppNotifier _notifier;
    private readonly ILogger<NetworkToolsDialogViewModel> _logger;
    private readonly List<string> _pingLines = [];

    /// <summary>本机在虚拟局域网里的 IP。</summary>
    [ObservableProperty]
    private string _localVirtualIp = string.Empty;

    /// <summary>本机网段前缀（扫描用）。</summary>
    [ObservableProperty]
    private string _localNetworkPrefix = string.Empty;

    /// <summary>要扫描的网段前缀，留空表示用本机网段。</summary>
    [ObservableProperty]
    private string _scanPrefix = string.Empty;

    /// <summary>是否有操作在进行。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>扫描状态文字。</summary>
    [ObservableProperty]
    private string _scanStatusText = "点「扫描一次」查找同一虚拟局域网里的设备。";

    /// <summary>发现到的设备。</summary>
    public ObservableCollection<VirtualLanPeerRow> Peers { get; } = [];

    /// <summary>选中的设备。</summary>
    [ObservableProperty]
    private VirtualLanPeerRow? _selectedPeer;

    /// <summary>Ping 目标 IP。</summary>
    [ObservableProperty]
    private string _pingTargetIp = string.Empty;

    /// <summary>Ping 端口（双方都运行本程序时是本程序监听的端口）。</summary>
    [ObservableProperty]
    private string _pingPortText = IZeroTierService.PingPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>正在 Ping。</summary>
    [ObservableProperty]
    private bool _isPinging;

    /// <summary>Ping 状态。</summary>
    [ObservableProperty]
    private string _pingSummaryText = string.Empty;

    /// <summary>Ping 历史。</summary>
    [ObservableProperty]
    private string _pingLogText = string.Empty;

    public NetworkToolsDialogViewModel(
        ISukiDialog dialog,
        IZeroTierService zeroTier,
        IVirtualLanDiscoveryService discovery,
        IAppNotifier notifier,
        ILogger<NetworkToolsDialogViewModel> logger)
    {
        _dialog = dialog;
        _zeroTier = zeroTier;
        _discovery = discovery;
        _notifier = notifier;
        _logger = logger;

        _discovery.ScanCompleted += OnScanCompleted;
        _discovery.ScanningChanged += OnScanningChanged;

        RefreshLocalInfo();
        ApplyPeers(_discovery.Peers);
    }

    public bool HasPeers => Peers.Count > 0;

    public bool HasPingLog => _pingLines.Count > 0;

    public bool CanScan => !IsBusy;

    public bool CanPing => !IsPinging;

    /// <summary>轮询按钮上的文字。</summary>
    public string ScanButtonText => IsScanning ? "停止轮询" : "开始轮询";

    /// <summary>是否正在周期轮询（直接读服务状态，和房间里的设备卡片共用）。</summary>
    public bool IsScanning => _discovery.IsScanning;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanScan));

    partial void OnIsPingingChanged(bool value) => OnPropertyChanged(nameof(CanPing));

    /// <summary>刷新本机虚拟 IP / 网段。</summary>
    [RelayCommand]
    private void RefreshLocalInfo()
    {
        LocalVirtualIp = _zeroTier.LocalVirtualIp;
        LocalNetworkPrefix = _zeroTier.LocalNetworkPrefix;

        if (string.IsNullOrWhiteSpace(ScanPrefix))
        {
            ScanPrefix = LocalNetworkPrefix;
        }
    }

    /// <summary>扫描一次整个网段。</summary>
    [RelayCommand]
    private async Task ScanOnceAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ScanStatusText = "正在扫描……";

        try
        {
            RefreshLocalInfo();

            IReadOnlyList<VirtualLanPeer> peers = await _discovery.ScanOnceAsync(NormalizePrefix());
            ApplyPeers(peers);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描虚拟局域网设备失败。");
            ScanStatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>开始 / 停止周期轮询。</summary>
    [RelayCommand]
    private void ToggleScanning()
    {
        if (IsScanning)
        {
            _discovery.StopScanning();
            ScanStatusText = "已停止轮询。";
            return;
        }

        RefreshLocalInfo();

        _discovery.StartScanning(SCAN_INTERVAL, NormalizePrefix());
        ScanStatusText = $"正在每 {SCAN_INTERVAL.TotalSeconds:0} 秒轮询一次……";
    }

    /// <summary>清空设备列表。</summary>
    [RelayCommand]
    private void ClearPeers()
    {
        _discovery.Clear();
        Peers.Clear();
        SelectedPeer = null;
        ScanStatusText = "已清空设备列表。";
        OnPropertyChanged(nameof(HasPeers));
    }

    /// <summary>把选中的设备填入 Ping 目标。</summary>
    [RelayCommand]
    private void UseSelectedPeer()
    {
        if (SelectedPeer is null)
        {
            PingSummaryText = "请先在设备列表里选一台设备。";
            return;
        }

        PingTargetIp = SelectedPeer.VirtualIp;
        PingSummaryText = $"已把 {PingTargetIp} 设为 Ping 目标。";
    }

    /// <summary>Ping 一次目标。</summary>
    [RelayCommand]
    private async Task PingAsync()
    {
        if (IsPinging)
        {
            return;
        }

        string target = PingTargetIp.Trim();

        if (target.Length == 0)
        {
            PingSummaryText = "请先填写要 Ping 的虚拟 IP。";
            return;
        }

        if (!int.TryParse(PingPortText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            PingSummaryText = "端口需要是 1–65535 之间的数字。";
            return;
        }

        IsPinging = true;
        PingSummaryText = $"正在 Ping {target}:{port} ……";

        try
        {
            ZeroTierPingResult result = await _zeroTier.PingAsync(target, port, PING_TIMEOUT);

            AddPingLine($"[{DateTime.Now:HH:mm:ss}] {result.Message}");
            PingSummaryText = (result.IsSuccess ? "✓ " : "✗ ") + result.Message;

            if (result.IsSuccess)
            {
                _notifier.Info("Ping", result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ping {Target} 失败。", target);
            PingSummaryText = $"Ping 失败：{ex.Message}";
        }
        finally
        {
            IsPinging = false;
        }
    }

    /// <summary>清空 Ping 历史。</summary>
    [RelayCommand]
    private void ClearPingLog()
    {
        _pingLines.Clear();
        PingLogText = string.Empty;
        PingSummaryText = string.Empty;
        OnPropertyChanged(nameof(HasPingLog));
    }

    /// <summary>关闭对话框（顺便停止轮询）。</summary>
    [RelayCommand]
    private void Close()
    {
        _discovery.StopScanning();
        _dialog.Dismiss();
    }

    private void OnScanningChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(ScanButtonText));
        });

    private string? NormalizePrefix()
    {
        string prefix = ScanPrefix.Trim();

        if (prefix.Length == 0)
        {
            return null;
        }

        return prefix.EndsWith('.') ? prefix : prefix + ".";
    }

    /// <summary>扫描结果来自后台线程，转回 UI 线程再更新列表。</summary>
    private void OnScanCompleted(object? sender, IReadOnlyList<VirtualLanPeer> peers)
        => Dispatcher.UIThread.Post(() => ApplyPeers(peers));

    private void ApplyPeers(IReadOnlyList<VirtualLanPeer> peers)
    {
        // 按 IP 原地更新，避免每轮都重建列表导致选中项丢失。
        VirtualLanPeerRow.Apply(Peers, peers);

        ScanStatusText = peers.Count == 0
            ? "还没有发现在线设备（对方也要运行本程序并接入同一个网络）。"
            : $"在线设备：{peers.Count} 台。";

        OnPropertyChanged(nameof(HasPeers));
    }

    private void AddPingLine(string line)
    {
        _pingLines.Insert(0, line);

        while (_pingLines.Count > MAX_PING_LINES)
        {
            _pingLines.RemoveAt(_pingLines.Count - 1);
        }

        PingLogText = string.Join(Environment.NewLine, _pingLines);
        OnPropertyChanged(nameof(HasPingLog));
    }
}
