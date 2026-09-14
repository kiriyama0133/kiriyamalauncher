#include "framework/socket_table.h"

#include "framework/winsock_api.h"

struct SocketEntry
{
    SOCKET handle;
    unsigned short port;
    bool portKnown;
    int type;      // SOCK_DGRAM / SOCK_STREAM，用来判断"这个 bind 是不是 UDP"
    bool connected;       // 调过 connect（UDP 连上对端）
    unsigned int remoteIp;
    unsigned short remotePort;
};

static const int kMaxSockets = 96;
static SocketEntry g_sockets[kMaxSockets];
static CRITICAL_SECTION g_socketLock;

// 线程级缓存：同一线程里连续对同一个 socket 取端口，免去反复进锁查表。
static __declspec(thread) SOCKET t_lastSocket = INVALID_SOCKET;
static __declspec(thread) unsigned short t_lastPort = 0;

void SocketTableInit()
{
    InitializeCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        g_sockets[index].handle = INVALID_SOCKET;
    }
}

void RememberSocketInfo(SOCKET socket, unsigned short port, int type)
{
    // 端口变了就把线程级缓存清掉，否则会一直用旧的（尤其是"绑定前查过一次"的情况）。
    if (port != 0 && t_lastSocket == socket && t_lastPort != port)
    {
        t_lastPort = port;
    }

    EnterCriticalSection(&g_socketLock);

    int freeIndex = -1;

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            if (port != 0)
            {
                g_sockets[index].port = port;
                g_sockets[index].portKnown = true;
            }

            if (type >= 0)
            {
                g_sockets[index].type = type;
            }

            LeaveCriticalSection(&g_socketLock);
            return;
        }

        if (freeIndex < 0 && g_sockets[index].handle == INVALID_SOCKET)
        {
            freeIndex = index;
        }
    }

    if (freeIndex >= 0)
    {
        g_sockets[freeIndex].handle = socket;
        g_sockets[freeIndex].port = port;
        g_sockets[freeIndex].portKnown = port != 0;
        g_sockets[freeIndex].type = type;
    }

    LeaveCriticalSection(&g_socketLock);
}

void RememberSocketPort(SOCKET socket, unsigned short port)
{
    RememberSocketInfo(socket, port, -1);
}

void RememberSocketRemote(SOCKET socket, unsigned int remoteIp, unsigned short remotePort)
{
    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            g_sockets[index].connected = true;
            g_sockets[index].remoteIp = remoteIp;
            g_sockets[index].remotePort = remotePort;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
}

/// <summary>这个 socket 是不是"连上对端的 UDP"；是的话要用队列注入（回环投递会被内核按源地址丢掉）。</summary>
bool IsConnectedDatagram(SOCKET socket, unsigned int* remoteIp, unsigned short* remotePort)
{
    bool connected = false;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            connected = g_sockets[index].connected;

            if (remoteIp != nullptr)
            {
                *remoteIp = g_sockets[index].remoteIp;
            }

            if (remotePort != nullptr)
            {
                *remotePort = g_sockets[index].remotePort;
            }

            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
    return connected;
}

/// <summary>按端口找一个本地 socket（用来判断这个端口上是不是"已连接的 UDP"）。</summary>
SOCKET FindSocketByPort(unsigned short port)
{
    SOCKET found = INVALID_SOCKET;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle != INVALID_SOCKET
            && g_sockets[index].portKnown
            && g_sockets[index].port == port)
        {
            found = g_sockets[index].handle;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
    return found;
}

/// <summary>这个 socket 是不是 UDP（bind 时决定要不要请隧道也开这个端口）。</summary>
bool IsDatagramSocket(SOCKET socket)
{
    bool datagram = false;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            datagram = g_sockets[index].type == SOCK_DGRAM;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
    return datagram;
}

void ForgetSocket(SOCKET socket)
{
    if (t_lastSocket == socket)
    {
        t_lastSocket = INVALID_SOCKET;
        t_lastPort = 0;
    }

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            g_sockets[index].handle = INVALID_SOCKET;
            g_sockets[index].port = 0;
            g_sockets[index].portKnown = false;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
}

unsigned short QuerySocketPort(SOCKET socket)
{
    if (socket == INVALID_SOCKET || original_getsockname == nullptr)
    {
        return 0;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    int length = sizeof(address);

    if (original_getsockname(socket, reinterpret_cast<struct sockaddr*>(&address), &length) != 0)
    {
        return 0;
    }

    return (unsigned short)ntohs(address.sin_port);
}

unsigned short GetSocketPort(SOCKET socket)
{
    if (socket == t_lastSocket)
    {
        return t_lastPort;
    }

    unsigned short port = 0;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == socket)
        {
            port = g_sockets[index].portKnown ? g_sockets[index].port : 0;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);

    if (port == 0)
    {
        port = QuerySocketPort(socket);

        if (port != 0)
        {
            RememberSocketPort(socket, port);
        }
        else
        {
            // 查不到（还没绑定）就不要缓存 0：绑定之后还会再查一次。
            return 0;
        }
    }

    t_lastSocket = socket;
    t_lastPort = port;
    return port;
}
