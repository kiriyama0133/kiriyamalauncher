// ============================================================================
//  框架用来"拿到当前游戏档案"的统一入口。
//
//  换游戏只需改 KIRIYAMA_GAME_PROFILE（build-hook.ps1 里用 /D 传，或改这里的默认值），
//  以及把 games 目录下的档案文件换掉。
// ============================================================================

#pragma once

#include "framework/game_profile.h"

#ifndef KIRIYAMA_GAME_PROFILE
#define KIRIYAMA_GAME_PROFILE "games/civilization6.h"
#endif

#include KIRIYAMA_GAME_PROFILE

/// <summary>这个端口是不是当前游戏的局域网端口段。</summary>
inline bool IsLanPort(unsigned int port)
{
    const KiriyamaHook::GameProfile& profile = KiriyamaHook::GetGameProfile();
    return port >= profile.LanPortFirst && port <= profile.LanPortLast;
}
