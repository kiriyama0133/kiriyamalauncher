using System;

namespace kiriyamalauncher.Data;

/// <summary>在虚拟局域网里发现的一台设备。</summary>
/// <param name="VirtualIp">设备在虚拟网络里的 IP。</param>
/// <param name="LatencyMilliseconds">探测往返耗时（毫秒）。</param>
/// <param name="LastSeen">最后一次发现的时间。</param>
public record VirtualLanPeer(string VirtualIp, long LatencyMilliseconds, DateTime LastSeen)
{
    /// <summary>给界面显示的文本。</summary>
    public string DisplayText => $"{VirtualIp}　{LatencyMilliseconds} ms";
}
