#include "framework/counters.h"

volatile LONG g_outboundFrames = 0;
volatile LONG g_inboundFrames = 0;
volatile LONG g_inboundInjected = 0;
volatile LONG g_outboundDropped = 0;
volatile LONG g_queuedTotal = 0;

volatile LONG g_loopbackDelivered = 0;
volatile LONG g_loopbackFailed = 0;
volatile LONG g_loopbackStripped = 0;

volatile LONG g_listenFrames = 0;
volatile LONG g_lanSendLogs = 0;
volatile LONG g_missedTargetLogs = 0;
