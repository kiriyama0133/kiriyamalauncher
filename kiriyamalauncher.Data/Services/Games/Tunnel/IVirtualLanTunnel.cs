using System;
using System.Collections.Generic;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏隧道：把注入到游戏里的 Hook 送来的 UDP 报文，通过内嵌 ZeroTier 节点
/// 转发给同一虚拟网络里的其他机器；反向把对端来的报文交回 Hook。
///
/// 多人规则（和真实局域网一致）：
///   · 广播包 → 发给所有对端；
///   · 单播包 → 只发给目标 IP 那台对端；
///   · 入站帧带真实来源地址，游戏才知道回包该发给谁。
///
/// 两侧的端口都沿用游戏自己的端口，所以不需要事先知道对方用哪个端口。
/// </summary>
public interface IVirtualLanTunnel
{
    /// <summary>管道服务是否已启动。</summary>
    bool IsRunning { get; }

    /// <summary>游戏侧 Hook 是否已经连上管道。</summary>
    bool IsClientConnected { get; }

    /// <summary>第一个对端虚拟 IP（没有对端时为空，给界面显示用）。</summary>
    string PeerVirtualIp { get; }

    /// <summary>所有对端虚拟 IP。</summary>
    IReadOnlyList<string> PeerVirtualIps { get; }

    /// <summary>状态变化（连接、断开、错误），可能在后台线程触发。</summary>
    event EventHandler<string>? StateChanged;

    /// <summary>
    /// 开始监听游戏侧管道。
    /// <paramref name="localVirtualIp"/> 是本机虚拟 IP（Hook 用它认虚拟网段），
    /// <paramref name="peerVirtualIps"/> 是要互通的对端（可以是一台，也可以是多台）。
    /// </summary>
    void Start(string localVirtualIp, IReadOnlyList<string> peerVirtualIps);

    /// <summary>运行中动态更新对端列表（设备上下线时调用）。</summary>
    void UpdatePeers(IReadOnlyList<string> peerVirtualIps);

    /// <summary>停止隧道（关闭管道与所有虚拟网 socket）。</summary>
    void Stop();
}
