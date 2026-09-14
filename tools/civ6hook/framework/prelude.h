// ============================================================================
//  所有框架头文件的第一行都会包含它。
//
//  只做一件事：保证 <winsock2.h> 一定排在 <windows.h> 前面
//  （顺序反了会把 winsock1 的头拉进来，编译直接炸）。
// ============================================================================

#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")
