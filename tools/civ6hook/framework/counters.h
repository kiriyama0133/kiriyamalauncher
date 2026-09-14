// ============================================================================
//  全局计数器（只增不减，供周期统计日志使用）。
//  单独放一个模块，省得各个模块互相 include 只为拿一个数。
// ============================================================================

#pragma once

#include "framework/prelude.h"

extern volatile LONG g_outboundFrames;    // 交给隧道的出站帧
extern volatile LONG g_inboundFrames;     // 隧道给回来的入站帧
extern volatile LONG g_inboundInjected;   // 真正喂进游戏的包
extern volatile LONG g_outboundDropped;   // 因为管道没连上而丢掉的出站帧
extern volatile LONG g_queuedTotal;       // 历史上入过队的包总数

extern volatile LONG g_loopbackDelivered; // 回环投递成功次数
extern volatile LONG g_loopbackFailed;    // 回环投递失败、改走队列的次数
extern volatile LONG g_loopbackStripped;  // 剥掉回环标记的次数

extern volatile LONG g_listenFrames;      // 发出去的"请隧道监听端口"帧
extern volatile LONG g_lanSendLogs;       // 局域网 socket 出站日志条数（限流用）
extern volatile LONG g_missedTargetLogs;  // "没转发但像是目标"的日志条数（限流用）
