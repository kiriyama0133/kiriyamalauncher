// ============================================================================
//  文明 6 的「游戏档案」
//
//  这个文件是**唯一**放"文明 6 专属规则"的地方。Hook 框架不认识任何具体游戏，
//  它只负责挂钩 Winsock、维护 socket 表、跑命名管道、把包搬进搬出游戏；
//  遇到"这个包要不要转发""这个端口要不要在虚拟网上也打开"时，回头问这里。
//
//  要给别的游戏做注入，只需要：
//    1) 复制这个文件改成 <游戏名>.h，改掉端口段和下面几个判定函数；
//    2) 编译时换掉 /DKIRIYAMA_GAME_PROFILE="games/xxx.h"（见 build-hook.ps1）。
//  框架代码一行都不用动。
//
//  —— 这些规则怎么来的（实测文明 6 得到的事实）：
//    · 找房间：游戏 socket() 一个 UDP socket，往 255.255.255.255:62900-62999
//      连发 100 个 4 字节广播（载荷固定为 8D E8 FC 70），源端口是临时端口；
//    · 当房主：bind(0.0.0.0:62900) 收探测，另有 bind(0.0.0.0:62056) 的会话 socket；
//    · 房间信息里带着房主**自己的局域网 IP**，客户端点「加入」时会直接往那个 IP 发；
//    · 会话层（ENet 风格）**只认同一个局域网段里的地址**，
//      所以注入时要把对端伪装成"本机网段里的一个地址"（对端别名）。
// ============================================================================

#pragma once

#include "framework/game_profile.h"

namespace KiriyamaHook
{

/// <summary>文明 6 的局域网端口段。</summary>
inline bool Civ6IsLanPort(unsigned short port)
{
    return port >= 62900 && port <= 62999;
}

/// <summary>文明 6 的出站转发规则（按可靠性从高到低）。</summary>
inline bool Civ6ShouldForward(unsigned int hostOrderIp, unsigned short port, bool isBroadcast,
    unsigned int peerAliasIp, unsigned int localVirtualIp,
    bool isPrivateAddress, bool isInOwnSubnet)
{
    (void)hostOrderIp;

    // 1) 局域网端口段：探测广播和房主的房间信息都走这里
    if (Civ6IsLanPort(port))
    {
        return true;
    }

    // 2) 广播地址
    if (isBroadcast)
    {
        return true;
    }

    // 3) 对端虚拟网段内的单播（两台机器在 ZeroTier 的同一个 /24 里）
    if (localVirtualIp != 0 && (hostOrderIp & 0xFFFFFF00u) == (localVirtualIp & 0xFFFFFF00u))
    {
        return true;
    }

    // 4) 发给"对端别名地址"的包：游戏以为对端就在自己局域网里，发给它的都是要转的
    if (peerAliasIp != 0 && hostOrderIp == peerAliasIp)
    {
        return true;
    }

    // 5) 房间信息里带着房主**自己的局域网 IP**，客户端点「加入」时会直接往那个 IP 发：
    //    它是私有地址、又不属于本机任何网段 → 那就是对端那边的地址
    if (isPrivateAddress && !isInOwnSubnet)
    {
        return true;
    }

    return false;
}

/// <summary>
/// 文明 6 只需要"局域网端口段"和 UDP socket 被播报出去。
/// （62900 那种监听口必须在虚拟网上也打开，否则对端发过来的探测会被直接丢掉。）
/// </summary>
inline bool Civ6ShouldAnnounceListenPort(unsigned short port, bool isDatagramSocket)
{
    return Civ6IsLanPort(port) || isDatagramSocket;
}

/// <summary>
/// "没转发但目标值得看一眼"的判定：私有地址。
/// 文明 6 的房间信息里带着房主的局域网 IP，所以只要是私有地址又没被转发，
/// 就说明"差一步就能连上"，值得单独记一条（框架已经排除了回环地址）。
/// </summary>
inline bool Civ6ShouldLogMissedTarget(unsigned int hostOrderIp, bool isPrivateAddress, bool isInOwnSubnet)
{
    (void)hostOrderIp;
    (void)isInOwnSubnet;
    return isPrivateAddress;
}

/// <summary>
/// 给对端挑"伪装地址"：取本机第一个 /24（或更窄）网段，主机位用 240。
/// 这样游戏看到的对端就在自己局域网里，会话层才会接受（见文件头的实测结论）。
/// </summary>
inline unsigned int Civ6ChoosePeerAlias(const LocalSubnet* subnets, int count)
{
    if (subnets == nullptr || count <= 0)
    {
        return 0;
    }

    for (int index = 0; index < count; ++index)
    {
        if (subnets[index].Mask < 0xFFFFFF00u)
        {
            continue;   // 比 /24 还大的网段不用
        }

        unsigned int candidate = (subnets[index].Network & 0xFFFFFF00u) | 0x000000F0u;

        if ((candidate >> 24) == 127 || candidate == 0)
        {
            continue;
        }

        return candidate;
    }

    return 0;
}

inline const GameProfile& GetGameProfile()
{
    static const GameProfile profile =
    {
        "Sid Meier's Civilization VI",
        62900,
        62999,
        &Civ6ShouldForward,
        &Civ6ShouldAnnounceListenPort,
        &Civ6ShouldLogMissedTarget,
        &Civ6ChoosePeerAlias
    };

    return profile;
}

} // namespace KiriyamaHook
