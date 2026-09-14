// ============================================================================
//  端口追踪 Hook：维护"句柄 → 本地端口"这张表，顺带把关键事件记下来。
//
//    socket / WSASocketW —— 建 socket 时记下类型（UDP or TCP）；
//    bind                 —— 绑了端口就记下来；有些游戏的监听口要靠它播报给隧道；
//    connect              —— UDP connect 之后游戏改用 send/recv，且内核会过滤来源地址，
//                            这是"加入房间"最关键的一步，必须单独记；
//    getsockname          —— 顺手补全端口；
//    closesocket          —— 删表项。
// ============================================================================

#include "framework/hook_functions.h"

#include "framework/game.h"
#include "framework/local_net.h"
#include "framework/logging.h"
#include "framework/net_utils.h"
#include "framework/socket_table.h"
#include "framework/tunnel_pipe.h"
#include "framework/winsock_api.h"

SOCKET WSAAPI Hook_socket(int family, int type, int protocol)
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

SOCKET WSAAPI Hook_WSASocketW(int family, int type, int protocol,
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

int WSAAPI Hook_bind(SOCKET socket, const struct sockaddr* address, int addressLength)
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
            // 否则对端发到「监听端口」的包会在虚拟网那侧被丢掉。
            // 具体哪些端口要报，交给游戏档案决定。
            if (KiriyamaHook::GetGameProfile().ShouldAnnounceListenPort(port, IsDatagramSocket(socket)))
            {
                TunnelAnnounceListenPort(port);
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
int WSAAPI Hook_connect(SOCKET socket, const struct sockaddr* address, int addressLength)
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
        TunnelAnnounceListenPort(localPort);
    }

    char text[32] = { 0 };
    FormatIpv4(remoteIp, text, sizeof(text));
    Log("游戏把 UDP socket 连到 %s:%u（本地端口 %u）—— 加入房间的流量走这条路。",
        text, (unsigned)remotePort, (unsigned)localPort);

    return result;
}

int WSAAPI Hook_getsockname(SOCKET socket, struct sockaddr* address, int* addressLength)
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

int WSAAPI Hook_closesocket(SOCKET socket)
{
    if (original_closesocket == nullptr)
    {
        return SOCKET_ERROR;
    }

    ForgetSocket(socket);
    return original_closesocket(socket);
}
