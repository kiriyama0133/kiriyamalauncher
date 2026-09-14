// ============================================================================
//  civ6hook —— 文明 6 局域网联机的正式版 Hook（配合启动器的 ZeroTier 隧道）
//
//  它做的事很简单：让「局域网广播找房间」这套机制在网络层之外也成立。
//
//  依据（来自 tools/civ6hook-diag 的实测日志）：
//    · 找房间：游戏 socket() 一个 UDP socket，往 255.255.255.255:62900-62999
//      连发 100 个 4 字节广播（+0x85D698），源端口是临时端口，socket 约 2 秒后关闭。
//    · 当房主：bind(0.0.0.0:62900)（+0x85D4F3）收探测，另有 bind(0.0.0.0:<临时端口>)
//      的会话 socket，用非阻塞 WSARecvFrom 疯狂轮询等数据（+0xC73450）。
//
//  所以本 Hook 只做两件事：
//    出站：凡是发往「广播地址」或「端口 62900-62999」或「对端虚拟 IP」的 UDP，
//          额外组一帧（带本地源端口 + 目标端口）写进命名管道，交给启动器用
//          libzt 单播给对端；同时**照原样放行**，不破坏同局域网正常联机。
//    入站：启动器从虚拟网收到的包（带对端源端口 + 本地目标端口）写回来，
//          按「本地端口」入队；游戏在 recvfrom / WSARecvFrom / recv / WSARecv
//          上收包时直接把排队的数据喂给它（from 填对端虚拟 IP:源端口），
//          并且让 select / WSAPoll 也能看到「这个 socket 可读」。
//
//  环境变量：
//    KIRIYAMA_LAN_DISABLE=1   完全不启动（等于没注入）
//    KIRIYAMA_LAN_DRYRUN=1    只记录、不转发也不注入（排查用）
//    KIRIYAMA_LAN_VERBOSE=1   详细日志
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <share.h>
#include <stdarg.h>
#include <string.h>
#include <string>
#include <deque>
#include <vector>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")

// ---------------------------------------------------------------- 配置 / 日志

static CRITICAL_SECTION g_logLock;
static std::vector<FILE*> g_logs;
static char g_dllPath[MAX_PATH] = { 0 };
static HMODULE g_selfModule = nullptr;

static volatile LONG g_enabled = 1;
static volatile LONG g_dryRun = 0;
static volatile LONG g_verbose = 0;

/// <summary>
/// 给对端用的「别名地址」：放在**本机自己的局域网段**里。
///
/// 原因（来自文明 6 自己的 net_connection_debug.log）：房主的会话层根本不认
/// 来自别的网段的连接 —— 它收到了我们的包，却没建立任何连接。
/// 所以把对端伪装成「本网段里的一台机器」，会话层才会正常处理。
/// </summary>
static unsigned int g_peerAliasIp = 0;

static void LogV(const char* format, va_list args)
{
    if (g_logs.empty())
    {
        return;
    }

    EnterCriticalSection(&g_logLock);

    for (FILE* log : g_logs)
    {
        SYSTEMTIME time;
        GetLocalTime(&time);
        fprintf(log, "[%02d:%02d:%02d.%03d] ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        vfprintf(log, format, args);
        fputc('\n', log);
        fflush(log);
    }

    LeaveCriticalSection(&g_logLock);
}

static void Log(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    LogV(format, args);
    va_end(args);
}

static void LogDebug(const char* format, ...)
{
    if (g_verbose == 0)
    {
        return;
    }

    va_list args;
    va_start(args, format);
    LogV(format, args);
    va_end(args);
}

static void OpenLog()
{
    if (!g_logs.empty())
    {
        return;
    }

    SYSTEMTIME time;
    GetLocalTime(&time);

    char directory[MAX_PATH] = { 0 };
    strcpy_s(directory, g_dllPath);
    char* slash = strrchr(directory, '\\');
    if (slash != nullptr)
    {
        *slash = '\0';
    }

    char path[MAX_PATH] = { 0 };
    sprintf_s(path, "%s\\civ6hook-%04d%02d%02d-%02d%02d%02d.log",
        directory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    FILE* file = _fsopen(path, "a", _SH_DENYNO);
    if (file != nullptr)
    {
        setvbuf(file, nullptr, _IONBF, 0);
        g_logs.push_back(file);
    }

    char userDirectory[MAX_PATH] = { 0 };
    char* localAppData = nullptr;
    size_t localAppDataLength = 0;

    if (_dupenv_s(&localAppData, &localAppDataLength, "LOCALAPPDATA") == 0 && localAppData != nullptr)
    {
        sprintf_s(userDirectory, "%s\\kiriyamalauncher\\logs", localAppData);
        free(localAppData);
    }
    else
    {
        strcpy_s(userDirectory, "C:\\kiriyamalauncher-logs");
    }

    CreateDirectoryA(userDirectory, nullptr);

    sprintf_s(path, "%s\\civ6hook-%04d%02d%02d-%02d%02d%02d.log",
        userDirectory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    file = _fsopen(path, "a", _SH_DENYNO);
    if (file != nullptr)
    {
        setvbuf(file, nullptr, _IONBF, 0);
        g_logs.push_back(file);
    }
}

// ---------------------------------------------------------------- 小工具

static const unsigned short kLanPortLow = 62900;
static const unsigned short kLanPortHigh = 62999;

static bool IsLanPort(unsigned int port)
{
    return port >= kLanPortLow && port <= kLanPortHigh;
}

static unsigned int AddressToHostOrder(const struct sockaddr* address)
{
    if (address == nullptr || address->sa_family != AF_INET)
    {
        return 0;
    }

    const struct sockaddr_in* ipv4 = reinterpret_cast<const struct sockaddr_in*>(address);
    return ntohl(ipv4->sin_addr.s_addr);
}

static unsigned int AddressPort(const struct sockaddr* address)
{
    if (address == nullptr || address->sa_family != AF_INET)
    {
        return 0;
    }

    return ntohs(reinterpret_cast<const struct sockaddr_in*>(address)->sin_port);
}

static void FormatIpv4(unsigned int hostOrderAddress, char* buffer, size_t length)
{
    sprintf_s(buffer, length, "%u.%u.%u.%u",
        (hostOrderAddress >> 24) & 0xFF,
        (hostOrderAddress >> 16) & 0xFF,
        (hostOrderAddress >> 8) & 0xFF,
        hostOrderAddress & 0xFF);
}

// ---------------------------------------------------------------- 原始函数

// 日志里打印报文前 16 字节（实现在后面，这里先声明一下）。
static void FormatPayloadPrefix(const char* data, int length, char* buffer, size_t bufferLength);

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

static SocketFn original_socket = nullptr;
static WSASocketWFn original_WSASocketW = nullptr;
static BindFn original_bind = nullptr;
static ConnectFn original_connect = nullptr;
static GetSockNameFn original_getsockname = nullptr;
static CloseSocketFn original_closesocket = nullptr;
static SendToFn original_sendto = nullptr;
static WSASendToFn original_WSASendTo = nullptr;
static SendFn original_send = nullptr;
static WSASendFn original_WSASend = nullptr;
static RecvFromFn original_recvfrom = nullptr;
static WSARecvFromFn original_WSARecvFrom = nullptr;
static RecvFn original_recv = nullptr;
static WSARecvFn original_WSARecv = nullptr;
static SelectFn original_select = nullptr;
static WSAPollFn original_WSAPoll = nullptr;
static GetProcAddressFn original_GetProcAddress = nullptr;

// ---------------------------------------------------------------- socket 表（句柄 → 本地端口）

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

static __declspec(thread) SOCKET t_lastSocket = INVALID_SOCKET;
static __declspec(thread) unsigned short t_lastPort = 0;

static void RememberSocketInfo(SOCKET socket, unsigned short port, int type)
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

static void RememberSocketPort(SOCKET socket, unsigned short port)
{
    RememberSocketInfo(socket, port, -1);
}

static void RememberSocketRemote(SOCKET socket, unsigned int remoteIp, unsigned short remotePort)
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
static bool IsConnectedDatagram(SOCKET socket, unsigned int* remoteIp, unsigned short* remotePort)
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
static SOCKET FindSocketByPort(unsigned short port)
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
static bool IsDatagramSocket(SOCKET socket)
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

static void ForgetSocket(SOCKET socket)
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

static unsigned short QuerySocketPort(SOCKET socket)
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

static unsigned short GetSocketPort(SOCKET socket)
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

// ---------------------------------------------------------------- 入站队列（按本地端口）

struct InboundPacket
{
    unsigned int fromIp;
    unsigned short fromPort;
    unsigned int length;
    unsigned char* data;
};

struct PortQueue
{
    bool used;
    unsigned short port;
    unsigned long long lastUsed;
    std::deque<InboundPacket> packets;
};

static const int kMaxQueues = 48;
static const int kMaxPacketsPerQueue = 64;
static PortQueue g_queues[kMaxQueues];
static CRITICAL_SECTION g_queueLock;
static volatile LONG g_queuedCount = 0;
static volatile LONG g_queuedTotal = 0;

// 回环投递：把「对端来的包」用本地 UDP 直接发给游戏那个 socket（目标 127.0.0.1:<游戏端口>），
// 前面加 14 字节标记（8 字节魔术 + 4 字节对端 IP + 2 字节对端端口）。
// 这样不管游戏是在非阻塞轮询、还是在阻塞 recv 里等，都能正常收到；
// 游戏侧读出来时，我们再把这 14 字节剥掉，并把 from 改成对端虚拟 IP:端口。
static const char kLoopbackMagic[8] = { 'K', 'I', 'R', 'I', 'Y', 'A', 'M', 'A' };
static const int kLoopbackTagLength = 14;
static SOCKET g_loopbackSender = INVALID_SOCKET;
static CRITICAL_SECTION g_loopbackLock;
static volatile LONG g_loopbackDelivered = 0;
static volatile LONG g_loopbackFailed = 0;
static volatile LONG g_loopbackStripped = 0;

static bool IsLoopbackAddress(unsigned int hostOrderIp)
{
    return (hostOrderIp & 0xFF000000u) == 0x7F000000u;
}

static PortQueue* FindQueueLocked(unsigned short port, bool create)
{
    int freeIndex = -1;
    int oldestIndex = 0;

    for (int index = 0; index < kMaxQueues; ++index)
    {
        if (g_queues[index].used && g_queues[index].port == port)
        {
            return &g_queues[index];
        }

        if (!g_queues[index].used && freeIndex < 0)
        {
            freeIndex = index;
        }

        if (g_queues[index].lastUsed < g_queues[oldestIndex].lastUsed)
        {
            oldestIndex = index;
        }
    }

    if (!create)
    {
        return nullptr;
    }

    if (freeIndex < 0)
    {
        freeIndex = oldestIndex;

        for (InboundPacket& packet : g_queues[freeIndex].packets)
        {
            free(packet.data);
            InterlockedDecrement(&g_queuedCount);
        }

        g_queues[freeIndex].packets.clear();
    }

    g_queues[freeIndex].used = true;
    g_queues[freeIndex].port = port;
    return &g_queues[freeIndex];
}

static void QueueInbound(unsigned short port, unsigned int fromIp, unsigned short fromPort,
    const unsigned char* data, unsigned int length)
{
    if (length == 0 || length > 65507 || g_dryRun != 0)
    {
        return;
    }

    unsigned char* copy = (unsigned char*)malloc(length);

    if (copy == nullptr)
    {
        return;
    }

    memcpy(copy, data, length);

    EnterCriticalSection(&g_queueLock);

    PortQueue* queue = FindQueueLocked(port, true);

    if (queue == nullptr)
    {
        LeaveCriticalSection(&g_queueLock);
        free(copy);
        return;
    }

    queue->lastUsed = GetTickCount64();

    while (queue->packets.size() >= (size_t)kMaxPacketsPerQueue)
    {
        free(queue->packets.front().data);
        queue->packets.pop_front();
        InterlockedDecrement(&g_queuedCount);
    }

    InboundPacket packet;
    packet.fromIp = fromIp;
    packet.fromPort = fromPort;
    packet.length = length;
    packet.data = copy;
    queue->packets.push_back(packet);

    InterlockedIncrement(&g_queuedCount);
    InterlockedIncrement(&g_queuedTotal);

    LeaveCriticalSection(&g_queueLock);
}

static bool HasQueuedPacket(unsigned short port)
{
    if (port == 0 || g_queuedCount == 0)
    {
        return false;
    }

    return true;
}

static bool DequeueInbound(unsigned short port, InboundPacket* out)
{
    if (port == 0 || g_queuedCount == 0)
    {
        return false;
    }

    EnterCriticalSection(&g_queueLock);

    PortQueue* queue = FindQueueLocked(port, false);

    if (queue == nullptr || queue->packets.empty())
    {
        LeaveCriticalSection(&g_queueLock);
        return false;
    }

    *out = queue->packets.front();
    queue->packets.pop_front();
    queue->lastUsed = GetTickCount64();
    InterlockedDecrement(&g_queuedCount);

    LeaveCriticalSection(&g_queueLock);
    return true;
}

/// <summary>把对端来的包用回环 UDP 直接送进游戏那个 socket（这样阻塞收包也能收到）。</summary>
static bool DeliverViaLoopback(unsigned short port, unsigned int fromIp, unsigned short fromPort,
    const unsigned char* data, unsigned int length)
{
    if (port == 0 || original_socket == nullptr || original_sendto == nullptr)
    {
        return false;
    }

    // 已经 connect 的 UDP socket 会被内核按来源地址过滤：
    // 127.0.0.1 发过去的包它根本不收，只能走队列注入（游戏 recv 时喂给它）。
    SOCKET listeningSocket = FindSocketByPort(port);

    if (listeningSocket != INVALID_SOCKET && IsConnectedDatagram(listeningSocket, nullptr, nullptr))
    {
        LogDebug("目标端口 %u 上的 socket 已连接对端，改用队列注入。", port);
        return false;
    }

    EnterCriticalSection(&g_loopbackLock);

    if (g_loopbackSender == INVALID_SOCKET)
    {
        g_loopbackSender = original_socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    }

    SOCKET sender = g_loopbackSender;

    if (sender == INVALID_SOCKET)
    {
        LeaveCriticalSection(&g_loopbackLock);
        return false;
    }

    std::vector<unsigned char> packet(kLoopbackTagLength + length);
    memcpy(packet.data(), kLoopbackMagic, 8);

    unsigned int networkIp = htonl(fromIp);
    memcpy(packet.data() + 8, &networkIp, 4);

    unsigned short networkPort = htons(fromPort);
    memcpy(packet.data() + 12, &networkPort, 2);

    memcpy(packet.data() + kLoopbackTagLength, data, length);

    struct sockaddr_in target;
    memset(&target, 0, sizeof(target));
    target.sin_family = AF_INET;
    target.sin_port = htons(port);
    target.sin_addr.s_addr = htonl(0x7F000001u);   // 127.0.0.1

    int sent = original_sendto(sender, (const char*)packet.data(), (int)packet.size(), 0,
        reinterpret_cast<const struct sockaddr*>(&target), sizeof(target));

    // 有些 socket 是绑在具体网卡地址上的（不是 0.0.0.0），回环包它收不到，
    // 所以再往它自己绑的那个地址发一份。
    if (listeningSocket != INVALID_SOCKET && original_getsockname != nullptr)
    {
        struct sockaddr_in local;
        memset(&local, 0, sizeof(local));
        int localLength = sizeof(local);

        if (original_getsockname(listeningSocket, reinterpret_cast<struct sockaddr*>(&local), &localLength) == 0
            && local.sin_family == AF_INET
            && local.sin_addr.s_addr != 0)
        {
            unsigned int localIp = ntohl(local.sin_addr.s_addr);

            if ((localIp >> 24) != 127)
            {
                target.sin_addr.s_addr = local.sin_addr.s_addr;

                int second = original_sendto(sender, (const char*)packet.data(), (int)packet.size(), 0,
                    reinterpret_cast<const struct sockaddr*>(&target), sizeof(target));

                if (second == (int)packet.size())
                {
                    sent = second;
                }
            }
        }
    }

    LeaveCriticalSection(&g_loopbackLock);
    return sent == (int)packet.size();
}

/// <summary>入站总入口：优先回环投递（阻塞/非阻塞都行），失败再退回队列。</summary>
static void DeliverInbound(unsigned short port, unsigned int fromIp, unsigned short fromPort,
    const unsigned char* data, unsigned int length)
{
    if (length == 0 || length > 65000 || g_dryRun != 0)
    {
        return;
    }

    if (DeliverViaLoopback(port, fromIp, fromPort, data, length))
    {
        InterlockedIncrement(&g_loopbackDelivered);
        char hex[64] = { 0 };
        FormatPayloadPrefix((const char*)data, (int)length, hex, sizeof(hex));
        Log("← 对端 → 本地 %u（来自 %u），%u 字节，已投进 socket 队列，payload=[%s]。",
            port, fromPort, length, hex);
        LogDebug("↘ 回环投递：对端 %u → 本地 %u，%u 字节。", fromPort, port, length);
        return;
    }

    InterlockedIncrement(&g_loopbackFailed);
    char queueHex[64] = { 0 };
    FormatPayloadPrefix((const char*)data, (int)length, queueHex, sizeof(queueHex));
    Log("← 对端 → 本地 %u（来自 %u），%u 字节，走队列注入（等游戏 recv），payload=[%s]。",
        port, fromPort, length, queueHex);
    QueueInbound(port, fromIp, fromPort, data, length);
}

/// <summary>读出来的包如果带我们的回环标记，就剥掉标记并把 from 改成对端虚拟 IP:端口。</summary>
static bool StripLoopbackTag(char* data, int* length, struct sockaddr_in* from)
{
    if (data == nullptr || length == nullptr || *length <= kLoopbackTagLength)
    {
        return false;
    }

    if (memcmp(data, kLoopbackMagic, 8) != 0)
    {
        return false;
    }

    unsigned int networkIp = 0;
    unsigned short networkPort = 0;
    memcpy(&networkIp, data + 8, 4);
    memcpy(&networkPort, data + 12, 2);

    int payloadLength = *length - kLoopbackTagLength;
    memmove(data, data + kLoopbackTagLength, payloadLength);
    *length = payloadLength;

    if (from != nullptr)
    {
        memset(from, 0, sizeof(*from));
        from->sin_family = AF_INET;
        from->sin_addr.s_addr = networkIp;
        from->sin_port = networkPort;
    }

    InterlockedIncrement(&g_loopbackStripped);
    return true;
}

/// <summary>
/// 收包返回后统一处理：如果这个包是我们回环投递进来的（带魔术标记），
/// 就剥掉标记、把 from 改成对端的虚拟 IP:端口、并把长度改回真实长度。
///
/// 带 from 时要求来源确实是回环地址，避免误伤正常局域网数据；
/// from 为 null 时（recv / WSARecv 这种）只看魔术标记。
/// </summary>
static volatile LONG g_readBackLogs = 0;

static bool TryUnwrapLoopback(SOCKET socket, const char* api, char* data, int* length,
    struct sockaddr* from, int* fromLength)
{
    if (data == nullptr || length == nullptr || *length <= kLoopbackTagLength)
    {
        return false;
    }

    if (memcmp(data, kLoopbackMagic, 8) != 0)
    {
        return false;
    }

    if (from != nullptr)
    {
        struct sockaddr_in address;
        memcpy(&address, from, sizeof(address));

        if (address.sin_family != AF_INET || !IsLoopbackAddress(ntohl(address.sin_addr.s_addr)))
        {
            return false;
        }
    }

    struct sockaddr_in sender;
    memset(&sender, 0, sizeof(sender));

    if (!StripLoopbackTag(data, length, &sender))
    {
        return false;
    }

    // 和对端队列那条路保持一致：把来源 IP 换成"本机局域网里的别名地址"。
    if (g_peerAliasIp != 0)
    {
        sender.sin_addr.s_addr = htonl(g_peerAliasIp);
    }

    // 注意：这里**不再**要求调用方的 fromLength 足够大。
    // 很多引擎把 fromLength 传成 0（"我不关心长度"），旧代码在这种情况下不会改写来源，
    // 游戏就会看到来源是 127.0.0.1 —— 而实测文明 6 的会话层对 127.0.0.1 来的连接请求是不回的。
    int requestedLength = (fromLength != nullptr) ? *fromLength : -1;

    if (from != nullptr)
    {
        memcpy(from, &sender, sizeof(sender));

        if (fromLength != nullptr)
        {
            *fromLength = sizeof(sender);
        }
    }

    // 记一条"游戏真的把这个注入包读走了"，并写明它看到的来源 —— 这是判断
    // "会话层为什么装看不见"最直接的一行日志。
    if (InterlockedIncrement(&g_readBackLogs) <= 120)
    {
        char text[32] = { 0 };
        FormatIpv4(ntohl(sender.sin_addr.s_addr), text, sizeof(text));

        Log("↖ %s 读走了注入包：%d 字节（本地端口 %u），来源=%s:%u，调用方 from=%s / fromLength=%d。",
            api, *length, (unsigned)GetSocketPort(socket),
            text, (unsigned)ntohs(sender.sin_port),
            from != nullptr ? "有" : "无", requestedLength);
    }

    return true;
}

// ---------------------------------------------------------------- 管道（与启动器通信）

static char g_pipeName[256] = "\\\\.\\pipe\\kiriyama-lan-tunnel";
static const int kFrameHeaderLength = 12;

static HANDLE g_pipe = INVALID_HANDLE_VALUE;
static volatile LONG g_pipeConnected = 0;
static CRITICAL_SECTION g_pipeWriteLock;
static OVERLAPPED g_readOverlapped = { 0 };
static OVERLAPPED g_writeOverlapped = { 0 };
static HANDLE g_readEvent = nullptr;
static HANDLE g_writeEvent = nullptr;
/// <summary>本机虚拟 IP（由启动器用控制帧告知）。Hook 用它算出虚拟网段，决定哪些单播包该交给隧道。</summary>
static unsigned int g_localVirtualIp = 0;
static volatile LONG g_outboundFrames = 0;
static volatile LONG g_inboundFrames = 0;
static volatile LONG g_inboundInjected = 0;
static volatile LONG g_outboundDropped = 0;
static volatile LONG g_dropLogTick = 0;
static volatile LONG g_connectFailLogTick = 0;

/// <summary>丢包日志限流：最多每 5 秒记一条，免得把日志刷爆。</summary>
static void LogDropThrottled(const char* reason)
{
    DWORD now = GetTickCount();
    LONG last = InterlockedExchange(&g_dropLogTick, (LONG)now);

    if (last == 0 || (DWORD)(now - (DWORD)last) > 5000)
    {
        Log("出站帧被丢弃：%s（累计已丢 %ld 个）。如果游戏隧道没开着，请先在启动器里开启「游戏隧道」。",
            reason, (long)g_outboundDropped);
    }
}

/// <summary>
/// 管道句柄是带 FILE_FLAG_OVERLAPPED 打开的：所有读写都必须用 OVERLAPPED 结构，
/// 否则「一个线程在阻塞读」会把「另一个线程的写」一起卡死（同步句柄的 I/O 是串行的）。
/// </summary>
static bool WaitForOverlapped(HANDLE pipe, OVERLAPPED* overlapped, HANDLE eventHandle, DWORD* transferred)
{
    if (WaitForSingleObject(eventHandle, INFINITE) != WAIT_OBJECT_0)
    {
        return false;
    }

    return GetOverlappedResult(pipe, overlapped, transferred, FALSE) != FALSE;
}

static bool WriteAll(HANDLE pipe, const void* data, DWORD length)
{
    const BYTE* cursor = (const BYTE*)data;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD written = 0;

        ResetEvent(g_writeEvent);

        if (WriteFile(pipe, cursor, remaining, &written, &g_writeOverlapped))
        {
            if (written == 0)
            {
                return false;
            }
        }
        else
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }

            if (!WaitForOverlapped(pipe, &g_writeOverlapped, g_writeEvent, &written) || written == 0)
            {
                return false;
            }
        }

        cursor += written;
        remaining -= written;
    }

    return true;
}

static bool ReadAll(HANDLE pipe, void* data, DWORD length)
{
    BYTE* cursor = (BYTE*)data;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD read = 0;

        ResetEvent(g_readEvent);

        if (ReadFile(pipe, cursor, remaining, &read, &g_readOverlapped))
        {
            if (read == 0)
            {
                return false;
            }
        }
        else
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }

            if (!WaitForOverlapped(pipe, &g_readOverlapped, g_readEvent, &read) || read == 0)
            {
                return false;
            }
        }

        cursor += read;
        remaining -= read;
    }

    return true;
}

/// <summary>
/// 把「出站报文」交给启动器：本地源端口 + 目标端口 + 目标地址 + 是否广播 + 内容。
/// 启动器据此决定是发给所有对端（广播）还是只发给目标那台（单播）。
/// </summary>
static volatile LONG g_listenFrames = 0;

/// <summary>
/// 需要请隧道跟着打开的本地端口（游戏 bind 过、又是 UDP 的那些）。
///
/// 为什么必须记着：游戏经常在"隧道还没开"的时候就把监听端口绑好了
///（比如先进了局域网界面、之后才开隧道）。所以这里先记下来，
/// 等管道一连上就全部补发一遍，不能只发一次。
/// </summary>
static const int kMaxListenPorts = 64;
static unsigned short g_listenPorts[kMaxListenPorts];
static volatile LONG g_listenPortCount = 0;
static CRITICAL_SECTION g_listenLock;

static bool RememberListenPort(unsigned short port)
{
    bool added = false;

    EnterCriticalSection(&g_listenLock);

    bool exists = false;

    for (LONG index = 0; index < g_listenPortCount && index < kMaxListenPorts; ++index)
    {
        if (g_listenPorts[index] == port)
        {
            exists = true;
            break;
        }
    }

    if (!exists && g_listenPortCount < kMaxListenPorts)
    {
        g_listenPorts[g_listenPortCount] = port;
        InterlockedIncrement(&g_listenPortCount);
        added = true;
    }

    LeaveCriticalSection(&g_listenLock);
    return added;
}

/// <summary>把某个端口写一条"请隧道监听"的帧（管道没连就直接返回 false）。</summary>
static bool SendListenFrameNow(unsigned short port)
{
    BYTE frame[kFrameHeaderLength] = { 0 };
    frame[0] = 4;   // type = 4：请隧道监听这个端口
    frame[4] = (BYTE)(port & 0xFF);
    frame[5] = (BYTE)(port >> 8);

    EnterCriticalSection(&g_pipeWriteLock);
    bool ok = WriteAll(g_pipe, frame, kFrameHeaderLength);
    LeaveCriticalSection(&g_pipeWriteLock);

    if (ok)
    {
        InterlockedIncrement(&g_listenFrames);
    }

    return ok;
}

/// <summary>
/// 告诉启动器：游戏在这个端口上监听（UDP），请在虚拟网上也打开同一个端口。
/// 少了这一步，对端发到房主监听端口（62900）的探测包会被隧道那侧直接丢掉。
/// </summary>
static void SendListenFrame(unsigned short port)
{
    if (port == 0)
    {
        return;
    }

    bool added = RememberListenPort(port);

    if (g_dryRun != 0 || g_pipeConnected == 0)
    {
        return;   // 记下来了，等管道连上会补发
    }

    if (SendListenFrameNow(port) && added)
    {
        Log("已请隧道在虚拟网上打开端口 %u（游戏在这个端口上收包）。", (unsigned)port);
    }
}

/// <summary>管道刚连上（或重连）时，把记下来的监听端口全部补发一遍。</summary>
static void ReplayListenFrames()
{
    unsigned short ports[kMaxListenPorts];
    LONG count = 0;

    EnterCriticalSection(&g_listenLock);

    count = g_listenPortCount < kMaxListenPorts ? g_listenPortCount : kMaxListenPorts;

    for (LONG index = 0; index < count; ++index)
    {
        ports[index] = g_listenPorts[index];
    }

    LeaveCriticalSection(&g_listenLock);

    for (LONG index = 0; index < count; ++index)
    {
        if (SendListenFrameNow(ports[index]))
        {
            Log("补发监听请求：虚拟网上打开端口 %u。", (unsigned)ports[index]);
        }
    }
}

/// <summary>这个本地端口是不是"局域网相关"（62900-62999，或者游戏 bind 过的端口）。</summary>
static bool IsLanRelatedPort(unsigned short port)
{
    if (port == 0)
    {
        return false;
    }

    if (IsLanPort(port))
    {
        return true;
    }

    bool found = false;

    EnterCriticalSection(&g_listenLock);

    for (LONG index = 0; index < g_listenPortCount && index < kMaxListenPorts; ++index)
    {
        if (g_listenPorts[index] == port)
        {
            found = true;
            break;
        }
    }

    LeaveCriticalSection(&g_listenLock);
    return found;
}

static volatile LONG g_lanSendLogs = 0;

/// <summary>
/// 局域网相关 socket 发出的每一个包都记一条（有上限）。
/// 排查"房主到底有没有回包"这种事，光看转发的包不够 —— 这个能看全。
/// </summary>
static void FormatPayloadPrefix(const char* data, int length, char* buffer, size_t bufferLength)
{
    buffer[0] = '\0';

    if (data == nullptr || length <= 0)
    {
        return;
    }

    int count = length < 16 ? length : 16;
    size_t offset = 0;

    for (int index = 0; index < count && offset + 4 < bufferLength; ++index)
    {
        offset += sprintf_s(buffer + offset, bufferLength - offset, "%02X ", (unsigned char)data[index]);
    }
}

static void LogLanRelatedSend(const char* api, SOCKET socket, const struct sockaddr* to, const char* data, int length)
{
    if (to == nullptr || to->sa_family != AF_INET || length <= 0)
    {
        return;
    }

    unsigned short localPort = GetSocketPort(socket);

    if (!IsLanRelatedPort(localPort))
    {
        return;
    }

    if (InterlockedIncrement(&g_lanSendLogs) > 200)
    {
        return;
    }

    char text[32] = { 0 };
    FormatIpv4(AddressToHostOrder(to), text, sizeof(text));

    char hex[64] = { 0 };
    FormatPayloadPrefix(data, length, hex, sizeof(hex));

    // 把前 16 字节也记下来：局域网协议的关键阶段（8 字节请求之类）看内容就能认出来。
    Log("%s（局域网 socket）：本地 %u → %s:%u，%d 字节，payload=[%s]。",
        api, localPort, text, (unsigned)AddressPort(to), length, hex);

    // 大包（比如 1104 字节的房间广告）再单独打一段更长的头，
    // 里面通常带着「该连到哪个端口 / 哪个会话 id」这类关键字段。
    if (length > 100)
    {
        static volatile LONG s_bigDumps = 0;

        if (InterlockedIncrement(&s_bigDumps) <= 8)
        {
            char big[3 * 160 + 4] = { 0 };
            int count = length < 160 ? length : 160;
            size_t offset = 0;

            for (int index = 0; index < count && offset + 4 < sizeof(big); ++index)
            {
                offset += sprintf_s(big + offset, sizeof(big) - offset, "%02X ", (unsigned char)data[index]);
            }

            Log("    ↑ 大包头 %d 字节：%s", count, big);
        }
    }
}

static void SendOutboundFrame(unsigned short sourcePort, unsigned short destinationPort,
    unsigned int destinationIp, bool isBroadcast, const char* data, int length)
{
    if (g_pipeConnected == 0 || length <= 0 || g_dryRun != 0)
    {
        InterlockedIncrement(&g_outboundDropped);

        char reason[96] = { 0 };
        sprintf_s(reason, "管道未连接=%d，长度=%d，只记录模式=%d",
            g_pipeConnected == 0, length, g_dryRun != 0);
        LogDropThrottled(reason);
        return;
    }

    if (length > 65507)
    {
        length = 65507;
    }

    BYTE frame[kFrameHeaderLength + 65507];
    frame[0] = 1;                     // type = 1：出站
    frame[1] = isBroadcast ? 1 : 0;   // flags bit0 = 广播
    frame[2] = (BYTE)(sourcePort & 0xFF);
    frame[3] = (BYTE)(sourcePort >> 8);
    frame[4] = (BYTE)(destinationPort & 0xFF);
    frame[5] = (BYTE)(destinationPort >> 8);
    frame[6] = (BYTE)(length & 0xFF);
    frame[7] = (BYTE)((length >> 8) & 0xFF);

    unsigned int networkIp = htonl(destinationIp);
    memcpy(frame + 8, &networkIp, 4);

    memcpy(frame + kFrameHeaderLength, data, length);

    EnterCriticalSection(&g_pipeWriteLock);

    LogDebug("写入隧道帧：本地 %u → 目标 %u，%d 字节……", sourcePort, destinationPort, length);
    bool ok = WriteAll(g_pipe, frame, kFrameHeaderLength + (DWORD)length);
    LogDebug("写入隧道帧返回：%d（Win32 错误码 %lu）", ok, GetLastError());

    LeaveCriticalSection(&g_pipeWriteLock);

    if (ok)
    {
        InterlockedIncrement(&g_outboundFrames);

        // 广播一次扫描 100 个（太吵，只进详细日志）；单播很少，一律记下来，
        // 这样"加入房间"这类流程发了什么、发给谁，一眼就能看到。
        if (isBroadcast)
        {
            LogDebug("→ 隧道（广播）：本地 %u → :%u，%d 字节。", sourcePort, destinationPort, length);
        }
        else
        {
            char text[32] = { 0 };
            FormatIpv4(destinationIp, text, sizeof(text));
            Log("→ 隧道（单播）：本地 %u → %s:%u，%d 字节。", sourcePort, text, destinationPort, length);
        }
    }
    else
    {
        InterlockedIncrement(&g_outboundDropped);
        Log("写入隧道失败，Win32 错误码 %lu —— 标记管道为断开，等下一次重连。", GetLastError());
        g_pipeConnected = 0;
    }
}

static DWORD WINAPI PipeReaderThread(LPVOID parameter)
{
    HANDLE pipe = (HANDLE)parameter;

    BYTE header[kFrameHeaderLength];
    std::vector<BYTE> payload;

    while (true)
    {
        if (!ReadAll(pipe, header, kFrameHeaderLength))
        {
            break;
        }

        unsigned int sourcePort = (unsigned int)(header[2] | (header[3] << 8));
        unsigned int destinationPort = (unsigned int)(header[4] | (header[5] << 8));
        unsigned int length = (unsigned int)(header[6] | (header[7] << 8));

        unsigned int networkIp = 0;
        memcpy(&networkIp, header + 8, 4);
        unsigned int hostIp = ntohl(networkIp);

        if (length > 65507)
        {
            break;
        }

        payload.resize(length);

        if (length > 0 && !ReadAll(pipe, payload.data(), length))
        {
            break;
        }

        if (header[0] == 2)
        {
            // 入站：对端 hostIp:sourcePort → 本地 destinationPort
            DeliverInbound((unsigned short)destinationPort, hostIp, (unsigned short)sourcePort,
                payload.data(), length);
            InterlockedIncrement(&g_inboundFrames);
            LogDebug("← 隧道：来自 %u，投给本地 %u，%u 字节。", hostIp, destinationPort, length);
        }
        else if (header[0] == 3)
        {
            // 控制帧：本机虚拟 IP —— 用它算出虚拟网段，判断哪些单播包该转发。
            g_localVirtualIp = hostIp;

            char text[32] = { 0 };
            FormatIpv4(g_localVirtualIp, text, sizeof(text));
            Log("隧道已告知本机虚拟 IP：%s（该 IP 所在 /24 网段里的单播也会转发给对端）", text);
        }
    }

    g_pipeConnected = 0;

    // 关闭这次连接用的句柄（连接线程下次会建新的），避免句柄泄漏。
    if (g_pipe == pipe)
    {
        g_pipe = INVALID_HANDLE_VALUE;
    }

    CloseHandle(pipe);

    Log("与启动器的管道已断开。");
    return 0;
}

static DWORD WINAPI PipeConnectThread(LPVOID parameter)
{
    (void)parameter;

    while (g_enabled != 0)
    {
        if (g_pipeConnected != 0)
        {
            Sleep(1000);
            continue;
        }

        HANDLE pipe = CreateFileA(g_pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            DWORD now = GetTickCount();
            LONG last = InterlockedExchange(&g_connectFailLogTick, (LONG)now);

            if (last == 0 || (DWORD)(now - (DWORD)last) > 30000)
            {
                Log("还没连接到启动器的游戏隧道（错误码 %lu）—— 启动器没开、或者隧道没启动？",
                    GetLastError());
            }

            Sleep(1000);
            continue;
        }

        if (g_readEvent == nullptr)
        {
            g_readEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
            g_writeEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
            g_readOverlapped.hEvent = g_readEvent;
            g_writeOverlapped.hEvent = g_writeEvent;
        }

        g_pipe = pipe;
        g_pipeConnected = 1;

        Log("已连接到启动器的游戏隧道管道。");

        // 打个招呼（type=9，空帧）：一是验证管道确实能写，二是让启动器知道 Hook 已就位。
        BYTE hello[kFrameHeaderLength] = { 9, 0, 0, 0, 0, 0, 0, 0 };

        EnterCriticalSection(&g_pipeWriteLock);
        bool helloOk = WriteAll(pipe, hello, kFrameHeaderLength);
        LeaveCriticalSection(&g_pipeWriteLock);

        Log("握手帧写入结果：%d（Win32 错误码 %lu）。", helloOk, GetLastError());

        // 游戏可能在我们连上之前就把监听端口绑好了，这里补发一遍。
        ReplayListenFrames();

        HANDLE reader = CreateThread(nullptr, 0, PipeReaderThread, pipe, 0, nullptr);

        if (reader == nullptr)
        {
            g_pipeConnected = 0;
            CloseHandle(pipe);
            Sleep(1000);
            continue;
        }

        CloseHandle(reader);
    }

    return 0;
}

// ---------------------------------------------------------------- 判断要不要转发

// 本机网段判断（实现在下面「本机网段」一节）
static bool IsInOwnSubnet(unsigned int hostOrderIp);
static bool IsPrivateAddress(unsigned int hostOrderIp);

static bool ShouldForwardToPeer(const struct sockaddr* to)
{
    if (g_enabled == 0 || to == nullptr || to->sa_family != AF_INET)
    {
        return false;
    }

    unsigned int ip = AddressToHostOrder(to);
    unsigned int port = AddressPort(to);

    if (IsLanPort(port))
    {
        return true;
    }

    if (ip == 0xFFFFFFFFu)
    {
        return true;   // 255.255.255.255
    }

    if ((ip & 0xFFu) == 0xFFu)
    {
        return true;   // x.x.x.255 子网广播
    }

    // 单播目标落在虚拟网段里（/24）→ 那是发给另一台玩家的，也交给隧道。
    if (g_localVirtualIp != 0 && (ip & 0xFFFFFF00u) == (g_localVirtualIp & 0xFFFFFF00u))
    {
        return true;
    }

    if ((ip >> 24) == 127 || ip == 0)
    {
        return false;   // 回环 / 本机
    }

    // 发给"对端别名地址"的包（游戏以为对端就在自己局域网里）→ 交给隧道。
    if (g_peerAliasIp != 0 && ip == g_peerAliasIp)
    {
        return true;
    }

    // 关键一条：文明 6 的房间信息里带的是房主**自己的局域网 IP**，
    // 点「加入」时客户端会直接往那个 IP 发查询包。那个地址在本机所在网络里不存在，
    // 但它是一个私有地址、又不属于本机任何一个网段 —— 那就是"对端那边的地址"，转过去。
    if (IsPrivateAddress(ip) && !IsInOwnSubnet(ip))
    {
        return true;
    }

    return false;
}

/// <summary>是不是广播地址（决定要不要发给所有对端）。</summary>
static bool IsBroadcastTarget(unsigned int hostOrderIp)
{
    return hostOrderIp == 0xFFFFFFFFu || (hostOrderIp & 0xFFu) == 0xFFu;
}

// ------------------------------------------------- 本机网段（判断"这个地址是不是对方的局域网 IP"）
//
// 文明 6 的房间信息里带着房主**自己的局域网 IP**（真实局域网里就是它自己）。
// 客户端点「加入」时就会往那个 IP 发查询包 —— 在我们这种跨网络的场景里，
// 那个地址不存在，包就丢了。所以要把「私有网段里、但不是本机自己网段」的地址
// 也认成"发给对端"。

static const int kMaxLocalSubnets = 16;
static unsigned int g_localNetworks[kMaxLocalSubnets];
static unsigned int g_localMasks[kMaxLocalSubnets];
static volatile LONG g_localSubnetCount = 0;

static void RefreshLocalSubnets()
{
    ULONG size = 16 * 1024;
    std::vector<BYTE> buffer(size);

    ULONG result = GetAdaptersAddresses(AF_INET,
        GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
        nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &size);

    if (result == ERROR_BUFFER_OVERFLOW)
    {
        buffer.resize(size);
        result = GetAdaptersAddresses(AF_INET,
            GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER,
            nullptr, reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data()), &size);
    }

    if (result != NO_ERROR)
    {
        return;
    }

    unsigned int networks[kMaxLocalSubnets];
    unsigned int masks[kMaxLocalSubnets];
    int count = 0;

    for (IP_ADAPTER_ADDRESSES* adapter = reinterpret_cast<IP_ADAPTER_ADDRESSES*>(buffer.data());
        adapter != nullptr && count < kMaxLocalSubnets;
        adapter = adapter->Next)
    {
        for (IP_ADAPTER_UNICAST_ADDRESS* unicast = adapter->FirstUnicastAddress;
            unicast != nullptr && count < kMaxLocalSubnets;
            unicast = unicast->Next)
        {
            sockaddr_in* address = reinterpret_cast<sockaddr_in*>(unicast->Address.lpSockaddr);

            if (address == nullptr || address->sin_family != AF_INET)
            {
                continue;
            }

            unsigned int ip = ntohl(address->sin_addr.s_addr);

            // 前缀长度 → 掩码
            unsigned int prefix = (unsigned int)unicast->OnLinkPrefixLength;
            unsigned int mask = prefix == 0 ? 0 : (prefix >= 32 ? 0xFFFFFFFFu : (~0u << (32 - prefix)));

            networks[count] = ip & mask;
            masks[count] = mask;
            ++count;
        }
    }

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < count; ++index)
    {
        g_localNetworks[index] = networks[index];
        g_localMasks[index] = masks[index];
    }

    InterlockedExchange(&g_localSubnetCount, count);
    LeaveCriticalSection(&g_socketLock);

    // 挑一个"别名"给对端用：取本机第一个正常的 /24 网段，主机位用 240。
    // 这样游戏看到的对端就在自己局域网里，会话层才会接受。
    unsigned int alias = 0;

    for (int index = 0; index < count; ++index)
    {
        if (masks[index] < 0xFFFFFF00u)
        {
            continue;   // 太大的网段不用
        }

        unsigned int candidate = (networks[index] & 0xFFFFFF00u) | 0x000000F0u;

        if ((candidate >> 24) == 127 || candidate == 0)
        {
            continue;
        }

        alias = candidate;
        break;
    }

    if (alias != 0 && alias != g_peerAliasIp)
    {
        g_peerAliasIp = alias;

        char text[32] = { 0 };
        FormatIpv4(alias, text, sizeof(text));
        Log("对端别名地址：%s（注入在这个地址上，让游戏以为对端就在本机局域网里）。", text);
    }
}

static bool IsInOwnSubnet(unsigned int hostOrderIp)
{
    LONG count = InterlockedCompareExchange(&g_localSubnetCount, 0, 0);

    EnterCriticalSection(&g_socketLock);

    bool inside = false;

    for (LONG index = 0; index < count; ++index)
    {
        if ((hostOrderIp & g_localMasks[index]) == g_localNetworks[index])
        {
            inside = true;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
    return inside;
}

/// <summary>私有地址（10/8、172.16/12、192.168/16、169.254/16）。</summary>
static bool IsPrivateAddress(unsigned int hostOrderIp)
{
    if ((hostOrderIp & 0xFF000000u) == 0x0A000000u)
    {
        return true;   // 10.0.0.0/8
    }

    if ((hostOrderIp & 0xFFF00000u) == 0xAC100000u)
    {
        return true;   // 172.16.0.0/12
    }

    if ((hostOrderIp & 0xFFFF0000u) == 0xC0A80000u)
    {
        return true;   // 192.168.0.0/16
    }

    if ((hostOrderIp & 0xFFFF0000u) == 0xA9FE0000u)
    {
        return true;   // 169.254.0.0/16
    }

    return false;
}

// ---------------------------------------------------------------- Hook 实现

struct HookTarget
{
    const char* functionName;
    void* hookFunction;
    void** originalFunction;
};

static SOCKET WSAAPI Hook_socket(int, int, int);
static SOCKET WSAAPI Hook_WSASocketW(int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);
static int WSAAPI Hook_bind(SOCKET, const struct sockaddr*, int);
static int WSAAPI Hook_connect(SOCKET, const struct sockaddr*, int);
static int WSAAPI Hook_getsockname(SOCKET, struct sockaddr*, int*);
static int WSAAPI Hook_closesocket(SOCKET);
static int WSAAPI Hook_sendto(SOCKET, const char*, int, int, const struct sockaddr*, int);
static int WSAAPI Hook_WSASendTo(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_send(SOCKET, const char*, int, int);
static int WSAAPI Hook_WSASend(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_recvfrom(SOCKET, char*, int, int, struct sockaddr*, int*);
static int WSAAPI Hook_WSARecvFrom(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_recv(SOCKET, char*, int, int);
static int WSAAPI Hook_WSARecv(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_select(int, fd_set*, fd_set*, fd_set*, const struct timeval*);
static int WSAAPI Hook_WSAPoll(LPWSAPOLLFD, ULONG, INT);
static FARPROC WINAPI Hook_GetProcAddress(HMODULE, LPCSTR);

static const HookTarget g_targets[] =
{
    { "socket",       (void*)Hook_socket,       (void**)&original_socket },
    { "WSASocketW",   (void*)Hook_WSASocketW,   (void**)&original_WSASocketW },
    { "bind",         (void*)Hook_bind,         (void**)&original_bind },
    { "connect",      (void*)Hook_connect,      (void**)&original_connect },
    { "getsockname",  (void*)Hook_getsockname,  (void**)&original_getsockname },
    { "closesocket",  (void*)Hook_closesocket,  (void**)&original_closesocket },
    { "sendto",       (void*)Hook_sendto,       (void**)&original_sendto },
    { "WSASendTo",    (void*)Hook_WSASendTo,    (void**)&original_WSASendTo },
    { "send",         (void*)Hook_send,         (void**)&original_send },
    { "WSASend",      (void*)Hook_WSASend,      (void**)&original_WSASend },
    { "recvfrom",     (void*)Hook_recvfrom,     (void**)&original_recvfrom },
    { "WSARecvFrom",  (void*)Hook_WSARecvFrom,  (void**)&original_WSARecvFrom },
    { "recv",         (void*)Hook_recv,         (void**)&original_recv },
    { "WSARecv",      (void*)Hook_WSARecv,      (void**)&original_WSARecv },
    { "select",       (void*)Hook_select,       (void**)&original_select },
    { "WSAPoll",      (void*)Hook_WSAPoll,      (void**)&original_WSAPoll },
    { "GetProcAddress", (void*)Hook_GetProcAddress, (void**)&original_GetProcAddress }
};

static const int kTargetCount = (int)(sizeof(g_targets) / sizeof(g_targets[0]));

// ---------------------------------------------------------------- 出站 Hook

static void ForwardIfNeeded(SOCKET socket, const struct sockaddr* to, const char* data, int length)
{
    if (g_verbose != 0)
    {
        unsigned int ip = AddressToHostOrder(to);
        unsigned int port = AddressPort(to);
        char text[32] = { 0 };
        FormatIpv4(ip, text, sizeof(text));
        LogDebug("出站检查：sock=%llu dst=%s:%u family=%d 转发=%s",
            (unsigned long long)socket, text, port,
            to != nullptr ? (int)to->sa_family : -1,
            ShouldForwardToPeer(to) ? "是" : "否");
    }

    if (!ShouldForwardToPeer(to))
    {
        return;
    }

    unsigned short localPort = GetSocketPort(socket);
    unsigned short destinationPort = (unsigned short)AddressPort(to);
    unsigned int destinationIp = AddressToHostOrder(to);

    LogDebug("本地端口 = %u（sock=%llu）。", localPort, (unsigned long long)socket);

    if (localPort == 0)
    {
        // 还没绑定（第一次发送前）：先发个 0，让启动器用默认端口兜底。
        LogDebug("出站包本地端口未知（sock=%llu），仍尝试转发。", (unsigned long long)socket);
    }

    SendOutboundFrame(localPort, destinationPort, destinationIp, IsBroadcastTarget(destinationIp), data, length);
}

/// <summary>
/// 有些包我们没转发、但目标是个私有地址 —— 那通常意味着"差一步就能连上"，
/// 所以专门记一条（有上限，不会刷屏），方便定位。
/// </summary>
static volatile LONG g_missedTargetLogs = 0;

static void LogMissedPrivateTarget(const char* api, const struct sockaddr* to)
{
    if (to == nullptr || to->sa_family != AF_INET)
    {
        return;
    }

    unsigned int ip = AddressToHostOrder(to);

    if (!IsPrivateAddress(ip) || (ip >> 24) == 127)
    {
        return;
    }

    if (InterlockedIncrement(&g_missedTargetLogs) > 300)
    {
        return;
    }

    char text[32] = { 0 };
    FormatIpv4(ip, text, sizeof(text));
    Log("（未转发）%s → %s:%u：目标不在虚拟网段、也不是对端那边能识别的地址。",
        api, text, (unsigned)AddressPort(to));
}

static int WSAAPI Hook_sendto(SOCKET socket, const char* data, int dataLength, int flags,
    const struct sockaddr* to, int toLength)
{
    if (original_sendto == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_sendto(socket, data, dataLength, flags, to, toLength);

    if (result >= 0 && socket != INVALID_SOCKET && original_getsockname != nullptr)
    {
        // 隐式绑定之后才拿得到真实端口，这里补一次记录。
        unsigned short port = GetSocketPort(socket);

        if (port != 0)
        {
            RememberSocketPort(socket, port);
        }

        ForwardIfNeeded(socket, to, data, result);
    }

    if (result >= 0 && !ShouldForwardToPeer(to))
    {
        LogMissedPrivateTarget("sendto", to);
    }

    LogLanRelatedSend("sendto", socket, to, data, result > 0 ? result : 0);

    return result;
}

static int WSAAPI Hook_WSASendTo(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent,
    DWORD flags, const struct sockaddr* to, int toLength, LPWSAOVERLAPPED overlapped,
    LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    if (original_WSASendTo == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_WSASendTo(socket, buffers, bufferCount, bytesSent, flags, to, toLength, overlapped, completion);

    // 注意：重叠(overlapped)发送也要转发 —— 之前只处理 overlapped == nullptr，
    // 结果"房主回包"如果是异步发的就会被整包丢掉（客户端只会一直重试）。
    // 重叠调用返回时缓冲区仍然有效（调用方必须保持到完成通知），所以这里读内容是安全的。
    bool accepted = overlapped == nullptr
        ? result == 0
        : (result == 0 || WSAGetLastError() == WSA_IO_PENDING);

    // WSASendTo 允许一次传多个缓冲区（iovec）。只转发 buffers[0] 会把包截断，
    // 所以多缓冲区时先拼成一块再转发。
    std::vector<char> flattened;
    const char* payload = nullptr;
    int payloadLength = 0;

    if (bufferCount > 0 && buffers != nullptr)
    {
        if (bufferCount == 1 && buffers[0].buf != nullptr)
        {
            payload = buffers[0].buf;
            payloadLength = (int)buffers[0].len;
        }
        else
        {
            size_t total = 0;

            for (DWORD index = 0; index < bufferCount; ++index)
            {
                total += buffers[index].len;
            }

            if (total > 0 && total <= 65507)
            {
                flattened.resize(total);
                size_t offset = 0;

                for (DWORD index = 0; index < bufferCount; ++index)
                {
                    if (buffers[index].buf != nullptr && buffers[index].len > 0)
                    {
                        memcpy(flattened.data() + offset, buffers[index].buf, buffers[index].len);
                    }

                    offset += buffers[index].len;
                }

                payload = flattened.data();
                payloadLength = (int)total;
            }
        }
    }

    if (accepted && payload != nullptr)
    {
        ForwardIfNeeded(socket, to, payload, payloadLength);
    }

    if (accepted && !ShouldForwardToPeer(to))
    {
        LogMissedPrivateTarget("WSASendTo", to);
    }

    LogLanRelatedSend("WSASendTo", socket, to, payload, accepted ? payloadLength : 0);

    return result;
}

static int WSAAPI Hook_send(SOCKET socket, const char* data, int dataLength, int flags)
{
    if (original_send == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_send(socket, data, dataLength, flags);

    // send 是面向连接的：只有"连到虚拟网里某台对端"的 UDP socket 才需要转发。
    unsigned int remoteIp = 0;
    unsigned short remotePort = 0;

    if (result > 0 && IsConnectedDatagram(socket, &remoteIp, &remotePort))
    {
        struct sockaddr_in remote;
        memset(&remote, 0, sizeof(remote));
        remote.sin_family = AF_INET;
        remote.sin_addr.s_addr = htonl(remoteIp);
        remote.sin_port = htons(remotePort);

        ForwardIfNeeded(socket, reinterpret_cast<const struct sockaddr*>(&remote), data, result);
        LogLanRelatedSend("send", socket, reinterpret_cast<const struct sockaddr*>(&remote), data, result);
    }

    return result;
}

static int WSAAPI Hook_WSASend(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent,
    DWORD flags, LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    if (original_WSASend == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_WSASend(socket, buffers, bufferCount, bytesSent, flags, overlapped, completion);

    unsigned int remoteIp = 0;
    unsigned short remotePort = 0;

    bool accepted = overlapped == nullptr
        ? result == 0
        : (result == 0 || WSAGetLastError() == WSA_IO_PENDING);

    if (accepted && bufferCount > 0 && buffers != nullptr
        && buffers[0].buf != nullptr && IsConnectedDatagram(socket, &remoteIp, &remotePort))
    {
        struct sockaddr_in remote;
        memset(&remote, 0, sizeof(remote));
        remote.sin_family = AF_INET;
        remote.sin_addr.s_addr = htonl(remoteIp);
        remote.sin_port = htons(remotePort);

        ForwardIfNeeded(socket, reinterpret_cast<const struct sockaddr*>(&remote), buffers[0].buf, (int)buffers[0].len);
        LogLanRelatedSend("WSASend", socket, reinterpret_cast<const struct sockaddr*>(&remote),
            buffers[0].buf, (int)buffers[0].len);
    }

    return result;
}

// ---------------------------------------------------------------- 入站 Hook

/// <summary>把排队的数据喂给游戏（普通缓冲区版本）。</summary>
static bool TryDeliver(SOCKET socket, char* buffer, int bufferLength, int* bytesWritten,
    struct sockaddr_in* from)
{
    unsigned short port = GetSocketPort(socket);

    if (!HasQueuedPacket(port))
    {
        return false;
    }

    InboundPacket packet;

    if (!DequeueInbound(port, &packet))
    {
        return false;
    }

    int copy = (int)packet.length;

    if (copy > bufferLength)
    {
        copy = bufferLength;
    }

    memcpy(buffer, packet.data, copy);
    free(packet.data);

    *bytesWritten = copy;

    if (from != nullptr)
    {
        memset(from, 0, sizeof(*from));
        from->sin_family = AF_INET;
        from->sin_port = htons(packet.fromPort);
        // 对端地址换成"本机局域网里的别名地址"：
        // 文明 6 的会话层不认别的网段的连接（见 net_connection_debug.log），
        // 所以必须让它看起来像同一局域网里的机器。
        from->sin_addr.s_addr = htonl(g_peerAliasIp != 0 ? g_peerAliasIp : packet.fromIp);
    }

    InterlockedIncrement(&g_inboundInjected);
    LogDebug("★ 注入：本地 %u ← %u 字节（来自对端端口 %u）。", port, (unsigned)packet.length, packet.fromPort);
    return true;
}

static int WSAAPI Hook_recvfrom(SOCKET socket, char* data, int dataLength, int flags,
    struct sockaddr* from, int* fromLength)
{
    if (original_recvfrom == nullptr)
    {
        return SOCKET_ERROR;
    }

    int bytes = 0;
    struct sockaddr_in sender;

    if (TryDeliver(socket, data, dataLength, &bytes, &sender))
    {
        if (from != nullptr)
        {
            memcpy(from, &sender, sizeof(sender));

            if (fromLength != nullptr)
            {
                *fromLength = sizeof(sender);
            }
        }

        return bytes;
    }

    int result = original_recvfrom(socket, data, dataLength, flags, from, fromLength);

    if (result > 0)
    {
        int length = result;

        if (TryUnwrapLoopback(socket, "recvfrom", data, &length, from, fromLength))
        {
            return length;
        }
    }

    return result;
}

static int WSAAPI Hook_WSARecvFrom(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
    LPDWORD flags, struct sockaddr* from, LPINT fromLength, LPWSAOVERLAPPED overlapped,
    LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    if (original_WSARecvFrom == nullptr)
    {
        return SOCKET_ERROR;
    }

    if (overlapped == nullptr && bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        int bytes = 0;
        struct sockaddr_in sender;

        if (TryDeliver(socket, buffers[0].buf, (int)buffers[0].len, &bytes, &sender))
        {
            if (bytesReceived != nullptr)
            {
                *bytesReceived = (DWORD)bytes;
            }

            if (flags != nullptr)
            {
                *flags = 0;
            }

            if (from != nullptr)
            {
                memcpy(from, &sender, sizeof(sender));

                if (fromLength != nullptr)
                {
                    *fromLength = sizeof(sender);
                }
            }

            return 0;
        }
    }

    int result = original_WSARecvFrom(socket, buffers, bufferCount, bytesReceived, flags, from, fromLength, overlapped, completion);

    if (overlapped == nullptr && result == 0 && bytesReceived != nullptr && *bytesReceived > 0
        && bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        int length = (int)*bytesReceived;

        if (TryUnwrapLoopback(socket, "WSARecvFrom", buffers[0].buf, &length, from, fromLength))
        {
            *bytesReceived = (DWORD)length;
        }
    }

    return result;
}

static int WSAAPI Hook_recv(SOCKET socket, char* data, int dataLength, int flags)
{
    if (original_recv == nullptr)
    {
        return SOCKET_ERROR;
    }

    int bytes = 0;

    if (TryDeliver(socket, data, dataLength, &bytes, nullptr))
    {
        return bytes;
    }

    int result = original_recv(socket, data, dataLength, flags);

    if (result > 0)
    {
        int length = result;

        if (TryUnwrapLoopback(socket, "recv", data, &length, nullptr, nullptr))
        {
            return length;
        }
    }

    return result;
}

static int WSAAPI Hook_WSARecv(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
    LPDWORD flags, LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    if (original_WSARecv == nullptr)
    {
        return SOCKET_ERROR;
    }

    if (overlapped == nullptr && bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        int bytes = 0;

        if (TryDeliver(socket, buffers[0].buf, (int)buffers[0].len, &bytes, nullptr))
        {
            if (bytesReceived != nullptr)
            {
                *bytesReceived = (DWORD)bytes;
            }

            if (flags != nullptr)
            {
                *flags = 0;
            }

            return 0;
        }
    }

    int result = original_WSARecv(socket, buffers, bufferCount, bytesReceived, flags, overlapped, completion);

    if (overlapped == nullptr && result == 0 && bytesReceived != nullptr && *bytesReceived > 0
        && bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        int length = (int)*bytesReceived;

        if (TryUnwrapLoopback(socket, "WSARecv", buffers[0].buf, &length, nullptr, nullptr))
        {
            *bytesReceived = (DWORD)length;
        }
    }

    return result;
}

// ---------------------------------------------------------------- 等待可读的 Hook

static int WSAAPI Hook_select(int nfds, fd_set* readSet, fd_set* writeSet, fd_set* exceptSet,
    const struct timeval* timeout)
{
    if (original_select == nullptr)
    {
        return SOCKET_ERROR;
    }

    // 如果有排队数据，先把对应 socket 塞进读集合，让 select 立刻返回可读。
    if (readSet != nullptr && g_queuedCount != 0)
    {
        for (u_int index = 0; index < readSet->fd_count; ++index)
        {
            SOCKET socket = readSet->fd_array[index];
            unsigned short port = GetSocketPort(socket);

            if (HasQueuedPacket(port))
            {
                FD_SET(socket, readSet);
            }
        }
    }

    int result = original_select(nfds, readSet, writeSet, exceptSet, timeout);

    if (readSet != nullptr && g_queuedCount != 0)
    {
        for (u_int index = 0; index < readSet->fd_count; ++index)
        {
            SOCKET socket = readSet->fd_array[index];

            if (HasQueuedPacket(GetSocketPort(socket)))
            {
                result = result > 0 ? result : 1;
            }
        }
    }

    return result;
}

static int WSAAPI Hook_WSAPoll(LPWSAPOLLFD descriptors, ULONG count, INT timeoutMilliseconds)
{
    if (original_WSAPoll == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_WSAPoll(descriptors, count, timeoutMilliseconds);

    if (descriptors != nullptr && g_queuedCount != 0)
    {
        for (ULONG index = 0; index < count; ++index)
        {
            if (HasQueuedPacket(GetSocketPort(descriptors[index].fd)))
            {
                descriptors[index].revents |= POLLRDNORM;
                result = result > 0 ? result : 1;
            }
        }
    }

    return result;
}

// ---------------------------------------------------------------- 端口追踪 Hook

static SOCKET WSAAPI Hook_socket(int family, int type, int protocol)
{
    if (original_socket == nullptr)
    {
        return INVALID_SOCKET;
    }

    SOCKET socket = original_socket(family, type, protocol);

    if (socket != INVALID_SOCKET)
    {
        RememberSocketInfo(socket, 0, type);
    }

    return socket;
}

static SOCKET WSAAPI Hook_WSASocketW(int family, int type, int protocol,
    LPWSAPROTOCOL_INFOW protocolInfo, GROUP group, DWORD flags)
{
    if (original_WSASocketW == nullptr)
    {
        return INVALID_SOCKET;
    }

    SOCKET socket = original_WSASocketW(family, type, protocol, protocolInfo, group, flags);

    if (socket != INVALID_SOCKET)
    {
        RememberSocketInfo(socket, 0, type);
    }

    return socket;
}

static int WSAAPI Hook_bind(SOCKET socket, const struct sockaddr* address, int addressLength)
{
    if (original_bind == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_bind(socket, address, addressLength);

    if (result == 0 && socket != INVALID_SOCKET)
    {
        unsigned short port = QuerySocketPort(socket);

        if (port != 0)
        {
            RememberSocketPort(socket, port);

            if (IsLanPort(port))
            {
                Log("游戏绑定了局域网端口 %u（sock=%llu）。", (unsigned)port, (unsigned long long)socket);
            }

            // 告诉启动器：也在虚拟网上把这个端口打开。
            // 否则对端发到「房主的监听端口（62900 这种）」的包会在虚拟网那侧被丢掉。
            //
            // 62900-62999 无条件上报：这个端口段是文明 6 的局域网端口，
            // 哪怕我们没记到这个 socket 的类型（比如创建得比 Hook 早）也不能漏。
            if (IsLanPort(port) || IsDatagramSocket(socket))
            {
                SendListenFrame(port);
            }
        }
    }

    return result;
}

/// <summary>
/// 记录 UDP connect：这类 socket 之后会用 send/recv（而不是 sendto/recvfrom），
/// 而且内核会按来源地址过滤收到的包（所以注入必须走队列）。
///
/// 加入房间那一步就是这种 socket —— 之前完全没覆盖，所以会卡在「正在检索创建人信息」。
/// </summary>
static int WSAAPI Hook_connect(SOCKET socket, const struct sockaddr* address, int addressLength)
{
    if (original_connect == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_connect(socket, address, addressLength);

    if (socket == INVALID_SOCKET || address == nullptr || address->sa_family != AF_INET)
    {
        return result;
    }

    unsigned int remoteIp = AddressToHostOrder(address);
    unsigned short remotePort = (unsigned short)AddressPort(address);

    if (result != 0)
    {
        // connect 失败（比如 UDP 也是立刻返回 0，这里一般是 TCP）—— 不留记录。
        return result;
    }

    if (!IsDatagramSocket(socket))
    {
        // TCP 我们暂时不转发，但如果是往"对端那边的私有地址"连，就记一条，
        // 这能直接说明"加入房间"是不是走 TCP。
        if (IsPrivateAddress(remoteIp) && !IsInOwnSubnet(remoteIp))
        {
            char text[32] = { 0 };
            FormatIpv4(remoteIp, text, sizeof(text));
            Log("（TCP 未转发）游戏 connect 到 %s:%u —— 如果加入房间卡住，这条就是原因。",
                text, (unsigned)remotePort);
        }

        return result;
    }

    RememberSocketRemote(socket, remoteIp, remotePort);

    // connect 会把没绑定的 UDP socket 隐式绑到一个临时端口 —— 把它记下来并请隧道也打开，
    // 否则对端回到这个端口的包会被虚拟网那侧丢掉。
    unsigned short localPort = QuerySocketPort(socket);

    if (localPort != 0)
    {
        RememberSocketPort(socket, localPort);
        SendListenFrame(localPort);
    }

    char text[32] = { 0 };
    FormatIpv4(remoteIp, text, sizeof(text));
    Log("游戏把 UDP socket 连到 %s:%u（本地端口 %u）—— 加入房间的流量走这条路。",
        text, (unsigned)remotePort, (unsigned)localPort);

    return result;
}

static int WSAAPI Hook_getsockname(SOCKET socket, struct sockaddr* address, int* addressLength)
{
    if (original_getsockname == nullptr)
    {
        return SOCKET_ERROR;
    }

    int result = original_getsockname(socket, address, addressLength);

    if (result == 0 && socket != INVALID_SOCKET && address != nullptr)
    {
        unsigned int port = AddressPort(address);

        if (port != 0)
        {
            RememberSocketPort(socket, (unsigned short)port);
        }
    }

    return result;
}

static int WSAAPI Hook_closesocket(SOCKET socket)
{
    if (original_closesocket == nullptr)
    {
        return SOCKET_ERROR;
    }

    ForgetSocket(socket);
    return original_closesocket(socket);
}

// ---------------------------------------------------------------- 挂载（IAT + 延迟导入 + 序号导入 + GetProcAddress）

static bool IsWs2Module(const char* moduleName)
{
    return _stricmp(moduleName, "WS2_32.dll") == 0;
}

static bool IsKernelModule(const char* moduleName)
{
    return _stricmp(moduleName, "KERNEL32.dll") == 0
        || _stricmp(moduleName, "KERNELBASE.dll") == 0;
}

static bool ResolveOrdinalExportName(HMODULE module, WORD ordinal, char* buffer, size_t length)
{
    if (module == nullptr)
    {
        return false;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return false;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    IMAGE_DATA_DIRECTORY directory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];

    if (directory.VirtualAddress == 0)
    {
        return false;
    }

    IMAGE_EXPORT_DIRECTORY* exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + directory.VirtualAddress);
    DWORD* names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);
    WORD* ordinals = reinterpret_cast<WORD*>(base + exports->AddressOfNameOrdinals);

    for (DWORD index = 0; index < exports->NumberOfNames; ++index)
    {
        if ((WORD)(ordinals[index] + exports->Base) == ordinal)
        {
            strcpy_s(buffer, length, reinterpret_cast<const char*>(base + names[index]));
            return true;
        }
    }

    return false;
}

static void* ResolveRealExport(const char* moduleName, const char* functionName)
{
    HMODULE module = GetModuleHandleA(moduleName);

    if (module == nullptr)
    {
        return nullptr;
    }

    void* proc = (void*)GetProcAddress(module, functionName);

    for (int index = 0; index < kTargetCount; ++index)
    {
        if (proc == g_targets[index].hookFunction)
        {
            return nullptr;
        }
    }

    return proc;
}

static bool TryHookSlot(void** slot, const char* functionName, const char* importedModule,
    const char* ownerModuleName, bool preferRealExport)
{
    for (int index = 0; index < kTargetCount; ++index)
    {
        const HookTarget& target = g_targets[index];

        if (strcmp(functionName, target.functionName) != 0)
        {
            continue;
        }

        void* current = *slot;

        if (current == target.hookFunction)
        {
            return false;
        }

        if (*target.originalFunction == nullptr)
        {
            void* real = preferRealExport ? ResolveRealExport(importedModule, functionName) : nullptr;
            *target.originalFunction = (real != nullptr) ? real : current;
        }

        DWORD oldProtect = 0;

        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        {
            return false;
        }

        *slot = target.hookFunction;
        VirtualProtect(slot, sizeof(void*), oldProtect, &oldProtect);
        LogDebug("已挂钩 %s!%s（模块 %s）", importedModule, functionName, ownerModuleName);
        return true;
    }

    return false;
}

static int HookModuleImports(HMODULE module, const char* moduleName)
{
    if (module == nullptr)
    {
        return 0;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return 0;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    IMAGE_DATA_DIRECTORY importDirectory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];

    if (importDirectory.VirtualAddress == 0)
    {
        return 0;
    }

    int patched = 0;
    IMAGE_IMPORT_DESCRIPTOR* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + importDirectory.VirtualAddress);

    for (; descriptor->Name != 0; ++descriptor)
    {
        const char* importedModule = reinterpret_cast<const char*>(base + descriptor->Name);

        if (!IsWs2Module(importedModule) && !IsKernelModule(importedModule))
        {
            continue;
        }

        IMAGE_THUNK_DATA* originalThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->OriginalFirstThunk);
        IMAGE_THUNK_DATA* firstThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->FirstThunk);

        if (descriptor->OriginalFirstThunk == 0)
        {
            originalThunk = firstThunk;
        }

        for (; originalThunk->u1.Function != 0; ++originalThunk, ++firstThunk)
        {
            char ordinalName[128] = { 0 };
            const char* functionName = nullptr;

            if ((originalThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) != 0)
            {
                WORD ordinal = (WORD)(originalThunk->u1.Ordinal & 0xFFFF);

                if (!ResolveOrdinalExportName(GetModuleHandleA(importedModule), ordinal, ordinalName, sizeof(ordinalName)))
                {
                    continue;
                }

                functionName = ordinalName;
            }
            else
            {
                IMAGE_IMPORT_BY_NAME* importByName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + originalThunk->u1.AddressOfData);
                functionName = reinterpret_cast<const char*>(importByName->Name);
            }

            if (TryHookSlot(reinterpret_cast<void**>(&firstThunk->u1.Function), functionName,
                importedModule, moduleName, false))
            {
                ++patched;
            }
        }
    }

    return patched;
}

static int HookModuleDelayImports(HMODULE module, const char* moduleName)
{
    if (module == nullptr)
    {
        return 0;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return 0;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    IMAGE_DATA_DIRECTORY directory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];

    if (directory.VirtualAddress == 0 || directory.Size == 0)
    {
        return 0;
    }

    int patched = 0;
    IMAGE_DELAYLOAD_DESCRIPTOR* descriptor = reinterpret_cast<IMAGE_DELAYLOAD_DESCRIPTOR*>(base + directory.VirtualAddress);

    for (; descriptor->DllNameRVA != 0; ++descriptor)
    {
        DWORD attributes = *reinterpret_cast<const DWORD*>(reinterpret_cast<const BYTE*>(descriptor));
        bool rvaBased = (attributes & 1) != 0;
        const char* importedModule = rvaBased
            ? reinterpret_cast<const char*>(base + descriptor->DllNameRVA)
            : reinterpret_cast<const char*>((uintptr_t)descriptor->DllNameRVA);

        if (importedModule == nullptr || (!IsWs2Module(importedModule) && !IsKernelModule(importedModule)))
        {
            continue;
        }

        BYTE* nameTable = rvaBased
            ? base + descriptor->ImportNameTableRVA
            : reinterpret_cast<BYTE*>((uintptr_t)descriptor->ImportNameTableRVA);
        BYTE* addressTable = rvaBased
            ? base + descriptor->ImportAddressTableRVA
            : reinterpret_cast<BYTE*>((uintptr_t)descriptor->ImportAddressTableRVA);

        if (nameTable == nullptr || addressTable == nullptr)
        {
            continue;
        }

        IMAGE_THUNK_DATA* nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(nameTable);
        IMAGE_THUNK_DATA* addressThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(addressTable);

        for (; nameThunk->u1.Function != 0; ++nameThunk, ++addressThunk)
        {
            char ordinalName[128] = { 0 };
            const char* functionName = nullptr;

            if ((nameThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) != 0)
            {
                WORD ordinal = (WORD)(nameThunk->u1.Ordinal & 0xFFFF);

                if (!ResolveOrdinalExportName(GetModuleHandleA(importedModule), ordinal, ordinalName, sizeof(ordinalName)))
                {
                    continue;
                }

                functionName = ordinalName;
            }
            else
            {
                IMAGE_IMPORT_BY_NAME* importByName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + nameThunk->u1.AddressOfData);
                functionName = reinterpret_cast<const char*>(importByName->Name);
            }

            if (TryHookSlot(reinterpret_cast<void**>(&addressThunk->u1.Function), functionName,
                importedModule, moduleName, true))
            {
                ++patched;
            }
        }
    }

    return patched;
}

static void HookAllLoadedModules()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());

    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return;
    }

    MODULEENTRY32W entry;
    entry.dwSize = sizeof(entry);

    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            if (reinterpret_cast<HMODULE>(entry.hModule) == g_selfModule)
            {
                continue;
            }

            char shortName[MAX_PATH] = { 0 };
            WideCharToMultiByte(CP_UTF8, 0, entry.szModule, -1, shortName, MAX_PATH, nullptr, nullptr);

            if (_strnicmp(shortName, "kernel32", 8) == 0
                || _strnicmp(shortName, "kernelbase", 10) == 0
                || _strnicmp(shortName, "ntdll", 5) == 0
                || _strnicmp(shortName, "ws2_32", 6) == 0)
            {
                continue;
            }

            HMODULE loadedModule = reinterpret_cast<HMODULE>(entry.hModule);
            HookModuleImports(loadedModule, shortName);
            HookModuleDelayImports(loadedModule, shortName);
        } while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
}

// GetProcAddress 拦截：凡是运行时解析我们要看的函数，就把我们自己的实现返回给它。
static FARPROC WINAPI Hook_GetProcAddress(HMODULE module, LPCSTR name)
{
    static __declspec(thread) int inHook = 0;

    if (original_GetProcAddress == nullptr)
    {
        return nullptr;
    }

    if (inHook != 0)
    {
        return original_GetProcAddress(module, name);
    }

    FARPROC real = original_GetProcAddress(module, name);

    if (real == nullptr || name == nullptr || HIWORD((DWORD_PTR)name) == 0 || module == nullptr)
    {
        return real;
    }

    for (int index = 0; index < kTargetCount; ++index)
    {
        const HookTarget& target = g_targets[index];

        if (strcmp(target.functionName, "GetProcAddress") == 0 || strcmp(name, target.functionName) != 0)
        {
            continue;
        }

        if (*target.originalFunction == nullptr)
        {
            *target.originalFunction = (void*)real;
        }

        if ((void*)real == target.hookFunction)
        {
            return real;
        }

        inHook = 1;
        LogDebug("动态解析 %s → 交给 Hook。", name);
        inHook = 0;
        return (FARPROC)target.hookFunction;
    }

    return real;
}

// ---------------------------------------------------------------- 启动

static DWORD WINAPI StartupThread(LPVOID parameter)
{
    (void)parameter;

    OpenLog();

    Log("=== civ6hook 已加载（联机转发%s）===", g_dryRun != 0 ? "，当前是只记录模式" : "");
    Log("DLL：%s", g_dllPath);

    RefreshLocalSubnets();

    // 尽早挂上：游戏一起来就会扫局域网。
    Sleep(200);
    HookAllLoadedModules();

    Sleep(1000);
    HookAllLoadedModules();

    for (int round = 0; round < 200; ++round)
    {
        Sleep(3000);
        HookAllLoadedModules();
        RefreshLocalSubnets();

        if ((round % 20) == 19)
        {
            Log("统计：出站 %ld 帧，入站 %ld 帧，注入 %ld 次，丢弃 %ld 次，待投递 %ld 个包。",
                (long)g_outboundFrames, (long)g_inboundFrames, (long)g_inboundInjected,
                (long)g_outboundDropped, (long)g_queuedCount);

            Log("统计（续）：回环投递 %ld 次，回环失败 %ld 次，剥标记 %ld 次。",
                (long)g_loopbackDelivered, (long)g_loopbackFailed, (long)g_loopbackStripped);

            Log("统计（再续）：查询本机网段 %ld 个；被忽略的私有目标 %ld 次。",
                (long)g_localSubnetCount, (long)g_missedTargetLogs);
        }
    }

    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason != DLL_PROCESS_ATTACH)
    {
        return TRUE;
    }

    g_selfModule = module;

    InitializeCriticalSection(&g_logLock);
    InitializeCriticalSection(&g_socketLock);
    InitializeCriticalSection(&g_queueLock);
    InitializeCriticalSection(&g_pipeWriteLock);
    InitializeCriticalSection(&g_loopbackLock);
    InitializeCriticalSection(&g_listenLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        g_sockets[index].handle = INVALID_SOCKET;
    }

    GetModuleFileNameA(module, g_dllPath, MAX_PATH);
    OpenLog();

    char flag[16] = { 0 };

    if (GetEnvironmentVariableA("KIRIYAMA_LAN_DISABLE", flag, sizeof(flag)) > 0 && flag[0] == '1')
    {
        InterlockedExchange(&g_enabled, 0);
        Log("KIRIYAMA_LAN_DISABLE=1：本次不启用转发。");
        return TRUE;
    }

    if (GetEnvironmentVariableA("KIRIYAMA_LAN_DRYRUN", flag, sizeof(flag)) > 0 && flag[0] == '1')
    {
        InterlockedExchange(&g_dryRun, 1);
    }

    if (GetEnvironmentVariableA("KIRIYAMA_LAN_VERBOSE", flag, sizeof(flag)) > 0 && flag[0] == '1')
    {
        InterlockedExchange(&g_verbose, 1);
    }

    // 测试/调试时可以指定别的管道名，避免和正在运行的启动器抢同一个管道。
    char pipeName[200] = { 0 };

    if (GetEnvironmentVariableA("KIRIYAMA_LAN_PIPE", pipeName, sizeof(pipeName)) > 0 && pipeName[0] != '\0')
    {
        sprintf_s(g_pipeName, "\\\\.\\pipe\\%s", pipeName);
    }

    DisableThreadLibraryCalls(module);

    HANDLE startup = CreateThread(nullptr, 0, StartupThread, nullptr, 0, nullptr);

    if (startup != nullptr)
    {
        CloseHandle(startup);
    }

    HANDLE pipeConnect = CreateThread(nullptr, 0, PipeConnectThread, nullptr, 0, nullptr);

    if (pipeConnect != nullptr)
    {
        CloseHandle(pipeConnect);
    }

    return TRUE;
}
