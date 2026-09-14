// ============================================================================
//  我们接管的那批函数的"原始版本"。
//
//  所有 Hook 都遵循同一个套路：先调 original_xxx 干活，再顺手记点东西 / 转发。
//  original_xxx 由挂载器（injector.cpp）在替换 IAT 槽时填上，这里只声明、不定义内容。
// ============================================================================

#pragma once

#include "framework/prelude.h"

typedef SOCKET (WSAAPI* SocketFn)(int, int, int);
typedef SOCKET (WSAAPI* WSASocketWFn)(int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);
typedef int (WSAAPI* BindFn)(SOCKET, const struct sockaddr*, int);
typedef int (WSAAPI* ConnectFn)(SOCKET, const struct sockaddr*, int);
typedef int (WSAAPI* GetSockNameFn)(SOCKET, struct sockaddr*, int*);
typedef int (WSAAPI* CloseSocketFn)(SOCKET);
// sendto(SOCKET, const char* buf, int len, int flags, const sockaddr* to, int tolen)
typedef int (WSAAPI* SendToFn)(SOCKET, const char*, int, int, const struct sockaddr*, int);
typedef int (WSAAPI* WSASendToFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* SendFn)(SOCKET, const char*, int, int);
typedef int (WSAAPI* WSASendFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* RecvFromFn)(SOCKET, char*, int, int, struct sockaddr*, int*);
typedef int (WSAAPI* WSARecvFromFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* RecvFn)(SOCKET, char*, int, int);
typedef int (WSAAPI* WSARecvFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* SelectFn)(int, fd_set*, fd_set*, fd_set*, const struct timeval*);
typedef int (WSAAPI* WSAPollFn)(LPWSAPOLLFD, ULONG, INT);
typedef FARPROC (WINAPI* GetProcAddressFn)(HMODULE, LPCSTR);

extern SocketFn original_socket;
extern WSASocketWFn original_WSASocketW;
extern BindFn original_bind;
extern ConnectFn original_connect;
extern GetSockNameFn original_getsockname;
extern CloseSocketFn original_closesocket;
extern SendToFn original_sendto;
extern WSASendToFn original_WSASendTo;
extern SendFn original_send;
extern WSASendFn original_WSASend;
extern RecvFromFn original_recvfrom;
extern WSARecvFromFn original_WSARecvFrom;
extern RecvFn original_recv;
extern WSARecvFn original_WSARecv;
extern SelectFn original_select;
extern WSAPollFn original_WSAPoll;
extern GetProcAddressFn original_GetProcAddress;
