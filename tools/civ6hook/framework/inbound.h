// ============================================================================
//  入站：把"隧道收到、来自对端的包"送进游戏。
//
//  两条路同时存在，按情况自动选：
//    · 回环投递 —— 把包用本地 UDP 直接发给游戏那个 socket（前面加 14 字节标记），
//      游戏不管在阻塞收包还是非阻塞轮询都能立刻收到；读出来时再把标记剥掉。
//    · 队列注入 —— 若那个 socket 是"已 connect 的 UDP"（内核会按来源地址过滤，
//      回环包它根本不收），就排进队列，等游戏 recv 时我们直接喂给它。
// ============================================================================

#pragma once

#include "framework/prelude.h"

/// <summary>当前队列里待投递的包数（给 select/WSAPoll 做快速判断用）。</summary>
extern volatile LONG g_queuedCount;

void InboundInit();

/// <summary>入站总入口：优先回环投递，失败再退回队列。由管道读线程调用。</summary>
void DeliverInbound(unsigned short port, unsigned int fromIp, unsigned short fromPort,
    const unsigned char* data, unsigned int length);

/// <summary>游戏调 recv 时试着把排队的数据喂给它。返回 true 表示已经喂了一个包。</summary>
bool TryDeliver(SOCKET socket, char* buffer, int bufferLength, int* bytesWritten,
    struct sockaddr_in* from);

/// <summary>这个本地端口当前有没有排队数据。</summary>
bool HasQueuedPacket(unsigned short port);

/// <summary>
/// 收包返回后统一处理：如果这个包是我们回环投递进去的（带魔术标记），
/// 就剥掉标记、把 from 改成对端地址、并把长度改回真实长度。
/// </summary>
bool TryUnwrapLoopback(SOCKET socket, const char* api, char* data, int* length,
    struct sockaddr* from, int* fromLength);
