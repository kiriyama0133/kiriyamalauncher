// ============================================================================
//  游戏档案接口 —— 框架和"具体游戏"之间唯一的契约。
//
//  框架完全不认识任何具体游戏，它只会：
//    · 挂钩 Winsock、维护 socket 表、跑命名管道、把包搬进搬出游戏；
//    · 遇到"这个包要不要转发""这个端口要不要在虚拟网上也打开"这类问题时，
//      回头问当前游戏档案（games/<游戏>.h 里实现的 GetGameProfile）。
//
//  接入新游戏 = 新写一份 games/<游戏>.h（实现下面 4 个回调 + 3 个字段），
//  编译时用 /DKIRIYAMA_GAME_PROFILE="games/xxx.h" 换掉即可，框架代码一行不动。
// ============================================================================

#pragma once

namespace KiriyamaHook
{

/// <summary>本机的一个网段（网络号 + 掩码，都是 host order）。</summary>
struct LocalSubnet
{
    unsigned int Network;
    unsigned int Mask;
};

/// <summary>一份游戏档案需要提供的全部信息。</summary>
struct GameProfile
{
    /// <summary>游戏名（只用于日志）。</summary>
    const char* DisplayName;

    /// <summary>这个游戏的局域网端口段（含端点）。没有固定端口段就填 0/0。</summary>
    unsigned short LanPortFirst;
    unsigned short LanPortLast;

    /// <summary>
    /// 这个出站 UDP 要不要转发给对端？
    /// 前三个参数由框架算好（目标是 hostOrderIp:port，isBroadcast 已判定）；
    /// 后面几个是框架提供的本机信息（对端别名 / 本机虚拟 IP / 是否私有地址 / 是否属于本机网段）。
    /// </summary>
    bool (*ShouldForward)(unsigned int hostOrderIp, unsigned short port, bool isBroadcast,
        unsigned int peerAliasIp, unsigned int localVirtualIp,
        bool isPrivateAddress, bool isInOwnSubnet);

    /// <summary>游戏 bind 了一个套接字，要不要请隧道在虚拟网上也打开这个端口？</summary>
    bool (*ShouldAnnounceListenPort)(unsigned short port, bool isDatagramSocket);

    /// <summary>
    /// 一个"没被转发"的包，要不要专门记一条日志？
    /// 用来标出"差一步就能连上"的目标（各游戏关心的特征不一样）。
    /// </summary>
    bool (*ShouldLogMissedTarget)(unsigned int hostOrderIp, bool isPrivateAddress, bool isInOwnSubnet);

    /// <summary>
    /// 给对端挑一个"伪装地址"（也就是让游戏看到的对端地址）。
    /// 返回 0 表示不伪装，直接用对端真实地址。
    /// </summary>
    unsigned int (*ChoosePeerAlias)(const LocalSubnet* subnets, int count);
};

/// <summary>
/// 当前游戏档案。定义在 games/<游戏>.h 里（inline），框架只负责调用。
/// </summary>
const GameProfile& GetGameProfile();

} // namespace KiriyamaHook
