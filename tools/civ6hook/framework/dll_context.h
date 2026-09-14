// ============================================================================
//  进程级运行状态。
//
//  谁在读：
//    · enabled / dryRun / verbose —— 几乎每个模块的开关；
//    · dllPath   —— 日志目录、诊断信息；
//    · selfModule —— 挂载 Hook 时用来"跳过自己"。
// ============================================================================

#pragma once

#include "framework/prelude.h"

/// <summary>0 = 完全不启动（等于没注入）。由环境变量 KIRIYAMA_LAN_DISABLE 控制。</summary>
extern volatile LONG g_enabled;

/// <summary>1 = 只记录、不转发也不注入（排查用）。由 KIRIYAMA_LAN_DRYRUN 控制。</summary>
extern volatile LONG g_dryRun;

/// <summary>1 = 详细日志。由 KIRIYAMA_LAN_VERBOSE 控制。</summary>
extern volatile LONG g_verbose;

/// <summary>本 DLL 的完整路径。</summary>
extern char g_dllPath[MAX_PATH];

/// <summary>本 DLL 的模块句柄。</summary>
extern HMODULE g_selfModule;

/// <summary>记下"自己是谁"（模块句柄 + 完整路径）。在 DllMain 里最先调用。</summary>
void DllContextInit(HMODULE module);
