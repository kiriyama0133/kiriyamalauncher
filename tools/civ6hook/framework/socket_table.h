// ============================================================================
//  socket 表：句柄 → 本地端口 / 类型（UDP or TCP）/ 是否 connect 过对端。
//
//  为什么需要它：
//    · 出站时要写"本地源端口"，可 Winsock 的 sendto 里根本没有本地端口；
//    · 入站时要按"本地端口"找到该喂给哪个 socket；
//    · 已 connect 的 UDP socket 内核会按来源地址过滤，注入必须改走队列。
// ============================================================================

#pragma once

#include "framework/prelude.h"

void SocketTableInit();

void RememberSocketInfo(SOCKET socket, unsigned short port, int type);
void RememberSocketPort(SOCKET socket, unsigned short port);
void RememberSocketRemote(SOCKET socket, unsigned int remoteIp, unsigned short remotePort);
void ForgetSocket(SOCKET socket);

bool IsConnectedDatagram(SOCKET socket, unsigned int* remoteIp, unsigned short* remotePort);
bool IsDatagramSocket(SOCKET socket);

SOCKET FindSocketByPort(unsigned short port);

/// <summary>问内核要端口（会在内部记录原始 getsockname）。</summary>
unsigned short QuerySocketPort(SOCKET socket);

/// <summary>拿本地端口（先查表、查不到再问内核）。</summary>
unsigned short GetSocketPort(SOCKET socket);
