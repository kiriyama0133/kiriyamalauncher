// ============================================================================
//  入站 Hook：让游戏"以为"这些包是从系统 socket 收到的。
//
//    recvfrom / WSARecvFrom   —— 有队列数据就直接喂；否则调原始函数，再看要不要剥回环标记
//    recv     / WSARecv       —— 同上（面向连接的 socket）
//    select   / WSAPoll       —— 让游戏"看见"那个 socket 可读，否则它永远不会去读
//
//  select / WSAPoll 这几行是关键：游戏大量使用非阻塞轮询，
//  只要它认为"没数据可读"，我们排队排得再整齐也没人取。
// ============================================================================

#include "framework/hook_functions.h"

#include "framework/inbound.h"
#include "framework/logging.h"
#include "framework/socket_table.h"
#include "framework/winsock_api.h"

int WSAAPI Hook_recvfrom(SOCKET socket, char* data, int dataLength, int flags,
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

int WSAAPI Hook_WSARecvFrom(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
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

int WSAAPI Hook_recv(SOCKET socket, char* data, int dataLength, int flags)
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

int WSAAPI Hook_WSARecv(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
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

int WSAAPI Hook_select(int nfds, fd_set* readSet, fd_set* writeSet, fd_set* exceptSet,
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

int WSAAPI Hook_WSAPoll(LPWSAPOLLFD descriptors, ULONG count, INT timeoutMilliseconds)
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
