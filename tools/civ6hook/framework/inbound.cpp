#include "framework/inbound.h"

#include <deque>
#include <stdlib.h>
#include <string.h>
#include <vector>

#include "framework/counters.h"
#include "framework/dll_context.h"
#include "framework/logging.h"
#include "framework/net_utils.h"
#include "framework/peer_alias.h"
#include "framework/socket_table.h"
#include "framework/winsock_api.h"

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
volatile LONG g_queuedCount = 0;

// 回环投递：把「对端来的包」用本地 UDP 直接发给游戏那个 socket（目标 127.0.0.1:<游戏端口>），
// 前面加 14 字节标记（8 字节魔术 + 4 字节对端 IP + 2 字节对端端口）。
// 这样不管游戏是在非阻塞轮询、还是在阻塞 recv 里等，都能正常收到；
// 游戏侧读出来时，我们再把这 14 字节剥掉，并把 from 改成对端地址。
static const char kLoopbackMagic[8] = { 'K', 'I', 'R', 'I', 'Y', 'A', 'M', 'A' };
static const int kLoopbackTagLength = 14;
static SOCKET g_loopbackSender = INVALID_SOCKET;
static CRITICAL_SECTION g_loopbackLock;
static volatile LONG g_readBackLogs = 0;

void InboundInit()
{
    InitializeCriticalSection(&g_queueLock);
    InitializeCriticalSection(&g_loopbackLock);
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

bool HasQueuedPacket(unsigned short port)
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

// ---------------------------------------------------------------- 回环投递

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

void DeliverInbound(unsigned short port, unsigned int fromIp, unsigned short fromPort,
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

// ---------------------------------------------------------------- 剥标记 / 喂数据

/// <summary>读出来的包如果带我们的回环标记，就剥掉标记并把 from 改成对端地址。</summary>
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

bool TryUnwrapLoopback(SOCKET socket, const char* api, char* data, int* length,
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

    // 和对端队列那条路保持一致：把来源 IP 换成"游戏档案指定的对端别名地址"
    //（有些游戏的会话层只认同网段地址，见 peer_alias.h）。
    if (g_peerAliasIp != 0)
    {
        sender.sin_addr.s_addr = htonl(g_peerAliasIp);
    }

    // 注意：这里**不再**要求调用方的 fromLength 足够大。
    // 很多引擎把 fromLength 传成 0（"我不关心长度"），旧代码在这种情况下不会改写来源，
    // 游戏就会看到来源是 127.0.0.1 —— 而实测某些游戏的会话层对 127.0.0.1 来的连接请求是不回的。
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

bool TryDeliver(SOCKET socket, char* buffer, int bufferLength, int* bytesWritten,
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
        // 对端地址换成"游戏档案指定的别名地址"，理由同上。
        from->sin_addr.s_addr = htonl(g_peerAliasIp != 0 ? g_peerAliasIp : packet.fromIp);
    }

    InterlockedIncrement(&g_inboundInjected);
    LogDebug("★ 注入：本地 %u ← %u 字节（来自对端端口 %u）。", port, (unsigned)packet.length, packet.fromPort);
    return true;
}
