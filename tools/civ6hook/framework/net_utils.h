// ============================================================================
//  纯函数工具箱：地址换算、IPv4 格式化、地址分类。
//
//  这里全是"和游戏无关"的判断，抽成 header-only 的 inline，谁都能用。
// ============================================================================

#pragma once

#include "framework/prelude.h"

#include <stdio.h>
#include <string.h>

inline unsigned int AddressToHostOrder(const struct sockaddr* address)
{
    if (address == nullptr || address->sa_family != AF_INET)
    {
        return 0;
    }

    const struct sockaddr_in* ipv4 = reinterpret_cast<const struct sockaddr_in*>(address);
    return ntohl(ipv4->sin_addr.s_addr);
}

inline unsigned int AddressPort(const struct sockaddr* address)
{
    if (address == nullptr || address->sa_family != AF_INET)
    {
        return 0;
    }

    return ntohs(reinterpret_cast<const struct sockaddr_in*>(address)->sin_port);
}

inline void FormatIpv4(unsigned int hostOrderAddress, char* buffer, size_t length)
{
    sprintf_s(buffer, length, "%u.%u.%u.%u",
        (hostOrderAddress >> 24) & 0xFF,
        (hostOrderAddress >> 16) & 0xFF,
        (hostOrderAddress >> 8) & 0xFF,
        hostOrderAddress & 0xFF);
}

/// <summary>把报文前 16 字节打成 "AA BB CC " 形式，方便日志里认包。</summary>
inline void FormatPayloadPrefix(const char* data, int length, char* buffer, size_t bufferLength)
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

inline bool IsBroadcastTarget(unsigned int hostOrderIp)
{
    return hostOrderIp == 0xFFFFFFFFu || (hostOrderIp & 0xFFu) == 0xFFu;
}

inline bool IsLoopbackAddress(unsigned int hostOrderIp)
{
    return (hostOrderIp & 0xFF000000u) == 0x7F000000u;
}

/// <summary>私有地址（10/8、172.16/12、192.168/16、169.254/16）。</summary>
inline bool IsPrivateAddress(unsigned int hostOrderIp)
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
