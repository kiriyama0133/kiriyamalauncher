// ============================================================================
//  日志。同一份内容会同时写进两个地方：
//    · DLL 所在目录（跟着游戏走，方便定位"到底加载了哪一份 DLL"）；
//    · %LOCALAPPDATA%\kiriyamalauncher\logs（和启动器日志放一起）。
// ============================================================================

#pragma once

/// <summary>建好日志锁。必须在任何 Log 调用之前、DllMain 里调一次。</summary>
void LogInitialize();

/// <summary>打开日志文件（幂等）。首次会依据 g_dllPath 推断目录。</summary>
void LogOpen();

void Log(const char* format, ...);

/// <summary>只有 KIRIYAMA_LAN_VERBOSE=1 时才写。</summary>
void LogDebug(const char* format, ...);
