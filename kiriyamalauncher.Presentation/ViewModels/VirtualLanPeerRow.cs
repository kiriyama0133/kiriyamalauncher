using CommunityToolkit.Mvvm.ComponentModel;
using kiriyamalauncher.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>虚拟局域网设备列表里的一行。</summary>
public partial class VirtualLanPeerRow : ObservableObject
{
    public VirtualLanPeerRow(VirtualLanPeer peer)
    {
        Update(peer);
    }

    /// <summary>设备在虚拟网络里的 IP。</summary>
    public string VirtualIp { get; private set; } = string.Empty;

    /// <summary>探测延迟（毫秒）。</summary>
    [ObservableProperty]
    private long _latencyMilliseconds;

    /// <summary>最后一次发现的时间。</summary>
    [ObservableProperty]
    private string _lastSeenText = string.Empty;

    /// <summary>延迟的显示文本。</summary>
    public string LatencyText => $"{LatencyMilliseconds} ms";

    partial void OnLatencyMillisecondsChanged(long value) => OnPropertyChanged(nameof(LatencyText));

    /// <summary>用新的探测结果刷新这一行。</summary>
    public void Update(VirtualLanPeer peer)
    {
        VirtualIp = peer.VirtualIp;
        LatencyMilliseconds = peer.LatencyMilliseconds;
        LastSeenText = peer.LastSeen.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        OnPropertyChanged(nameof(VirtualIp));
    }

    /// <summary>
    /// 把一轮扫描结果合并进列表：已知 IP 原地更新（保留选中项），不在线的移除。
    /// </summary>
    public static void Apply(ObservableCollection<VirtualLanPeerRow> rows, IReadOnlyList<VirtualLanPeer> peers)
    {
        foreach (VirtualLanPeer peer in peers)
        {
            VirtualLanPeerRow? row = Find(rows, peer.VirtualIp);

            if (row is null)
            {
                rows.Add(new VirtualLanPeerRow(peer));
            }
            else
            {
                row.Update(peer);
            }
        }

        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!Contains(peers, rows[index].VirtualIp))
            {
                rows.RemoveAt(index);
            }
        }
    }

    private static VirtualLanPeerRow? Find(ObservableCollection<VirtualLanPeerRow> rows, string address)
    {
        foreach (VirtualLanPeerRow row in rows)
        {
            if (string.Equals(row.VirtualIp, address, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    private static bool Contains(IReadOnlyList<VirtualLanPeer> peers, string address)
    {
        foreach (VirtualLanPeer peer in peers)
        {
            if (string.Equals(peer.VirtualIp, address, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
