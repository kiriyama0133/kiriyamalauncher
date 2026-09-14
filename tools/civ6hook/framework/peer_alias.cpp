#include "framework/peer_alias.h"

#include "framework/game.h"
#include "framework/logging.h"
#include "framework/net_utils.h"

unsigned int g_peerAliasIp = 0;

void UpdatePeerAliasFromSubnets(const KiriyamaHook::LocalSubnet* subnets, int count)
{
    unsigned int alias = KiriyamaHook::GetGameProfile().ChoosePeerAlias(subnets, count);

    if (alias == 0 || alias == g_peerAliasIp)
    {
        return;
    }

    g_peerAliasIp = alias;

    char text[32] = { 0 };
    FormatIpv4(alias, text, sizeof(text));
    Log("对端别名地址：%s（注入在这个地址上，让游戏以为对端就在本机局域网里）。", text);
}
