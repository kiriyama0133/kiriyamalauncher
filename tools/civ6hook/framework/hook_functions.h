// ============================================================================
//  我们自己的 Hook 实现（替换掉游戏看到的那批函数）。
//
//  分成三组、各管一段：
//    · 端口追踪  port_hooks.cpp    socket/bind/connect/getsockname/closesocket
//    · 出站转发  forwarding.cpp    sendto/WSASendTo/send/WSASend
//    · 入站注入  receiving.cpp     recvfrom/WSARecvFrom/recv/WSARecv/select/WSAPoll
//    · 动态解析  injector.cpp      GetProcAddress
// ============================================================================

#pragma once

#include "framework/prelude.h"

// ---- 端口追踪（port_hooks.cpp）----
SOCKET WSAAPI Hook_socket(int, int, int);
SOCKET WSAAPI Hook_WSASocketW(int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);
int WSAAPI Hook_bind(SOCKET, const struct sockaddr*, int);
int WSAAPI Hook_connect(SOCKET, const struct sockaddr*, int);
int WSAAPI Hook_getsockname(SOCKET, struct sockaddr*, int*);
int WSAAPI Hook_closesocket(SOCKET);

// ---- 出站转发（forwarding.cpp）----
int WSAAPI Hook_sendto(SOCKET, const char*, int, int, const struct sockaddr*, int);
int WSAAPI Hook_WSASendTo(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
int WSAAPI Hook_send(SOCKET, const char*, int, int);
int WSAAPI Hook_WSASend(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);

// ---- 入站注入（receiving.cpp）----
int WSAAPI Hook_recvfrom(SOCKET, char*, int, int, struct sockaddr*, int*);
int WSAAPI Hook_WSARecvFrom(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
int WSAAPI Hook_recv(SOCKET, char*, int, int);
int WSAAPI Hook_WSARecv(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
int WSAAPI Hook_select(int, fd_set*, fd_set*, fd_set*, const struct timeval*);
int WSAAPI Hook_WSAPoll(LPWSAPOLLFD, ULONG, INT);

// ---- 动态解析（injector.cpp）----
FARPROC WINAPI Hook_GetProcAddress(HMODULE, LPCSTR);
