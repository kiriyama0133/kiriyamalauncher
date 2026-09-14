// ============================================================================
//  与启动器之间的命名管道（"游戏隧道"）。
//
//  帧格式（与启动器侧的 LanTunnelService 约定，12 字节头 + 可选负载）：
//    type(1) | flags(1) | srcPort(2) | dstPort(2) | length(2) | ipv4(4) | payload
//      type 1 = 出站：本地 srcPort → 目标 dstPort/ipv4，要先看 flags bit0 决定广播还是单播
//      type 2 = 入站：对端 srcPort/ipv4 → 本地 dstPort
//      type 3 = 控制：ipv4 = 本机虚拟 IP
//      type 4 = 监听：请隧道在虚拟网上也打开 dstPort
//      type 9 = 握手
// ============================================================================

#pragma once

#include "framework/prelude.h"

/// <summary>本机虚拟 IP（由启动器用控制帧告知）。用来算虚拟网段，决定哪些单播包该交给隧道。</summary>
extern unsigned int g_localVirtualIp;

/// <summary>管道是否已连上。</summary>
extern volatile LONG g_pipeConnected;

void TunnelPipeInit();

/// <summary>覆盖管道名（测试用；KIRIYAMA_LAN_PIPE=xxx → \\.\pipe\xxx）。</summary>
void TunnelPipeSetName(const char* shortName);

/// <summary>起"连接 + 保持"线程。</summary>
void TunnelPipeStartConnectThread();

/// <summary>把一个出站报文交给启动器。</summary>
void TunnelSendOutboundFrame(unsigned short sourcePort, unsigned short destinationPort,
    unsigned int destinationIp, bool isBroadcast, const char* data, int length);

/// <summary>告诉启动器：游戏在这个端口上监听（UDP），请在虚拟网上也打开同一个端口。</summary>
void TunnelAnnounceListenPort(unsigned short port);

/// <summary>这个本地端口是不是"和局域网有关"（端口段，或者游戏 bind 过的端口）。</summary>
bool TunnelIsLanRelatedPort(unsigned short port);
