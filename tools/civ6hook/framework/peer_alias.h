// ============================================================================
//  "对端别名"：注入给游戏看的对端地址。
//
//  有些游戏的会话层只认同一个局域网段里的地址，收到别的网段来的连接请求根本不理。
//  这时候就得把对端伪装成"本机局域网里的一台机器"。
//
//  具体怎么挑、要不要挑，是游戏相关的业务 —— 这里只是个容器 + 调用游戏档案。
// ============================================================================

#pragma once

#include "framework/game_profile.h"

/// <summary>对端别名地址（host order）。0 = 不伪装，用对端真实地址。</summary>
extern unsigned int g_peerAliasIp;

/// <summary>拿最新的网段表，问游戏档案要一个别名并记下来。</summary>
void UpdatePeerAliasFromSubnets(const KiriyamaHook::LocalSubnet* subnets, int count);
