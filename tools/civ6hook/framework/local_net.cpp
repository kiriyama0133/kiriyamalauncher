#include "framework/local_net.h"

#include <iphlpapi.h>
#include <vector>

#include "framework/peer_alias.h"

static const int kMaxLocalSubnets = 16;
static unsigned int g_localNetworks[kMaxLocalSubnets];
static unsigned int g_localMasks[kMaxLocalSubnets];
volatile LONG g_localSubnetCount = 0;
static CRITICAL_SECTION g_subnetLock;

void LocalNetInit()
{
    InitializeCriticalSection(&g_subnetLock);
}

void RefreshLocalSubnets()
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

    EnterCriticalSection(&g_subnetLock);

    for (int index = 0; index < count; ++index)
    {
        g_localNetworks[index] = networks[index];
        g_localMasks[index] = masks[index];
    }

    InterlockedExchange(&g_localSubnetCount, count);
    LeaveCriticalSection(&g_subnetLock);

    // 具体挑哪个地址当"对端别名"，是游戏相关的业务 —— 交给游戏档案决定。
    KiriyamaHook::LocalSubnet subnets[kMaxLocalSubnets];
    int snapshotCount = count < kMaxLocalSubnets ? count : kMaxLocalSubnets;

    for (int index = 0; index < snapshotCount; ++index)
    {
        subnets[index].Network = networks[index];
        subnets[index].Mask = masks[index];
    }

    UpdatePeerAliasFromSubnets(subnets, snapshotCount);
}

bool IsInOwnSubnet(unsigned int hostOrderIp)
{
    LONG count = InterlockedCompareExchange(&g_localSubnetCount, 0, 0);

    EnterCriticalSection(&g_subnetLock);

    bool inside = false;

    for (LONG index = 0; index < count; ++index)
    {
        if ((hostOrderIp & g_localMasks[index]) == g_localNetworks[index])
        {
            inside = true;
            break;
        }
    }

    LeaveCriticalSection(&g_subnetLock);
    return inside;
}

int SnapshotLocalSubnets(KiriyamaHook::LocalSubnet* out, int max)
{
    if (out == nullptr || max <= 0)
    {
        return 0;
    }

    LONG count = InterlockedCompareExchange(&g_localSubnetCount, 0, 0);

    EnterCriticalSection(&g_subnetLock);

    int written = 0;

    for (LONG index = 0; index < count && written < max; ++index)
    {
        out[written].Network = g_localNetworks[index];
        out[written].Mask = g_localMasks[index];
        ++written;
    }

    LeaveCriticalSection(&g_subnetLock);
    return written;
}
