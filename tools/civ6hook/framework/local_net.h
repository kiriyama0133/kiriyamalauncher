// ============================================================================
//  本机网段表。
//
//  用途：判断一个地址"是不是本机自己局域网里的地址"。
//  这在跨网络联机里很关键 —— 游戏房间信息里经常带着**房主自己的局域网 IP**，
//  那个地址在我们这边并不存在，需要靠这张表把它和"本机真局域网"区分开。
// ============================================================================

#pragma once

#include "framework/prelude.h"
#include "framework/game_profile.h"

extern volatile LONG g_localSubnetCount;

void LocalNetInit();

/// <summary>重新枚举本机网卡，刷新网段表；顺便请游戏档案挑一个对端别名。</summary>
void RefreshLocalSubnets();

/// <summary>这个地址是不是落在本机某个网段里。</summary>
bool IsInOwnSubnet(unsigned int hostOrderIp);

/// <summary>拷一份网段快照（返回实际条数）。</summary>
int SnapshotLocalSubnets(KiriyamaHook::LocalSubnet* out, int max);
