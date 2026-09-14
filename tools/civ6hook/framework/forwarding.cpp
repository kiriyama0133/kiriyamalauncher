// ============================================================================
//  出站转发。
//
//  原则：**照原样放行**，只是"额外"组一帧交给隧道 —— 同局域网里正常联机不受影响。
//
//  覆盖 4 个入口，缺一不可：
//    sendto / WSASendTo —— 游戏广播找房间、房主回房间信息走这两个；
//    send   / WSASend   —— 游戏把 UDP connect 到对端之后改用这两个（加入房间那一步）。
// ============================================================================

#include "framework/hook_functions.h"

#include <string.h>
#include <vector>

#include "framework/counters.h"
#include "framework/dll_context.h"
#include "framework/game.h"
#include "framework/local_net.h"
#include "framework/logging.h"
#include "framework/net_utils.h"
#include "framework/peer_alias.h"
#include "framework/socket_table.h"
#include "framework/tunnel_pipe.h"
#include "framework/winsock_api.h"

/// <summary>这个出站包要不要转发给对端？</summary>
static bool ShouldForwardToPeer(const struct sockaddr* to)
{
    if (g_enabled == 0 || to == nullptr || to->sa_family != AF_INET)
    {
        return false;
    }

    unsigned int ip = AddressToHostOrder(to);
    unsigned int port = AddressPort(to);

    // 框架层面就排除的：回环 / 无效地址（各游戏都成立）
    if (IsLoopbackAddress(ip) || ip == 0)
    {
        return false;
    }

    // 具体规则交给"游戏档案"（换游戏只改 games/*.h）
    return KiriyamaHook::GetGameProfile().ShouldForward(ip, (unsigned short)port, IsBroadcastTarget(ip),
        g_peerAliasIp, g_localVirtualIp, IsPrivateAddress(ip), IsInOwnSubnet(ip));
}

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

    TunnelSendOutboundFrame(localPort, destinationPort, destinationIp, IsBroadcastTarget(destinationIp), data, length);
}

/// <summary>
/// 局域网相关 socket 发出的每一个包都记一条（有上限）。
/// 排查"房主到底有没有回包"这种事，光看转发的包不够 —— 这个能看全。
/// </summary>
static void LogLanRelatedSend(const char* api, SOCKET socket, const struct sockaddr* to, const char* data, int length)
{
    if (to == nullptr || to->sa_family != AF_INET || length <= 0)
    {
        return;
    }

    unsigned short localPort = GetSocketPort(socket);

    if (!TunnelIsLanRelatedPort(localPort))
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

    // 大包（比如房间广告）再单独打一段更长的头，
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

/// <summary>
/// 有些包我们没转发、但游戏档案说"这个目标值得看一眼" —— 专门记一条（有上限，不刷屏）。
/// </summary>
static void LogMissedPrivateTarget(const char* api, const struct sockaddr* to)
{
    if (to == nullptr || to->sa_family != AF_INET)
    {
        return;
    }

    unsigned int ip = AddressToHostOrder(to);

    if (IsLoopbackAddress(ip))
    {
        return;
    }

    if (!KiriyamaHook::GetGameProfile().ShouldLogMissedTarget(ip, IsPrivateAddress(ip), IsInOwnSubnet(ip)))
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

int WSAAPI Hook_sendto(SOCKET socket, const char* data, int dataLength, int flags,
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

int WSAAPI Hook_WSASendTo(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent,
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

int WSAAPI Hook_send(SOCKET socket, const char* data, int dataLength, int flags)
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

int WSAAPI Hook_WSASend(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent,
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
