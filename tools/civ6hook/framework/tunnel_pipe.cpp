#include "framework/tunnel_pipe.h"

#include <stdio.h>
#include <string.h>
#include <vector>

#include "framework/counters.h"
#include "framework/dll_context.h"
#include "framework/game.h"
#include "framework/inbound.h"
#include "framework/logging.h"
#include "framework/net_utils.h"

static char g_pipeName[256] = "\\\\.\\pipe\\kiriyama-lan-tunnel";
static const int kFrameHeaderLength = 12;

static HANDLE g_pipe = INVALID_HANDLE_VALUE;
volatile LONG g_pipeConnected = 0;
static CRITICAL_SECTION g_pipeWriteLock;
static OVERLAPPED g_readOverlapped = { 0 };
static OVERLAPPED g_writeOverlapped = { 0 };
static HANDLE g_readEvent = nullptr;
static HANDLE g_writeEvent = nullptr;

unsigned int g_localVirtualIp = 0;

static volatile LONG g_dropLogTick = 0;
static volatile LONG g_connectFailLogTick = 0;

// ---- 需要请隧道跟着打开的本地端口 ----
//
// 为什么必须记着：游戏经常在"隧道还没开"的时候就把监听端口绑好了
//（比如先进了局域网界面、之后才开隧道）。所以这里先记下来，
// 等管道一连上就全部补发一遍，不能只发一次。
static const int kMaxListenPorts = 64;
static unsigned short g_listenPorts[kMaxListenPorts];
static volatile LONG g_listenPortCount = 0;
static CRITICAL_SECTION g_listenLock;

void TunnelPipeInit()
{
    InitializeCriticalSection(&g_pipeWriteLock);
    InitializeCriticalSection(&g_listenLock);
}

void TunnelPipeSetName(const char* shortName)
{
    if (shortName != nullptr && shortName[0] != '\0')
    {
        sprintf_s(g_pipeName, "\\\\.\\pipe\\%s", shortName);
    }
}

/// <summary>丢包日志限流：最多每 5 秒记一条，免得把日志刷爆。</summary>
static void LogDropThrottled(const char* reason)
{
    DWORD now = GetTickCount();
    LONG last = InterlockedExchange(&g_dropLogTick, (LONG)now);

    if (last == 0 || (DWORD)(now - (DWORD)last) > 5000)
    {
        Log("出站帧被丢弃：%s（累计已丢 %ld 个）。隧道没连上时先不管，连上就会恢复。",
            reason, (long)g_outboundDropped);
    }
}

/// <summary>
/// 管道句柄是带 FILE_FLAG_OVERLAPPED 打开的：所有读写都必须用 OVERLAPPED 结构，
/// 否则「一个线程在阻塞读」会把「另一个线程的写」一起卡死（同步句柄的 I/O 是串行的）。
/// </summary>
static bool WaitForOverlapped(HANDLE pipe, OVERLAPPED* overlapped, HANDLE eventHandle, DWORD* transferred)
{
    if (WaitForSingleObject(eventHandle, INFINITE) != WAIT_OBJECT_0)
    {
        return false;
    }

    return GetOverlappedResult(pipe, overlapped, transferred, FALSE) != FALSE;
}

static bool WriteAll(HANDLE pipe, const void* data, DWORD length)
{
    const BYTE* cursor = (const BYTE*)data;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD written = 0;

        ResetEvent(g_writeEvent);

        if (WriteFile(pipe, cursor, remaining, &written, &g_writeOverlapped))
        {
            if (written == 0)
            {
                return false;
            }
        }
        else
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }

            if (!WaitForOverlapped(pipe, &g_writeOverlapped, g_writeEvent, &written) || written == 0)
            {
                return false;
            }
        }

        cursor += written;
        remaining -= written;
    }

    return true;
}

static bool ReadAll(HANDLE pipe, void* data, DWORD length)
{
    BYTE* cursor = (BYTE*)data;
    DWORD remaining = length;

    while (remaining > 0)
    {
        DWORD read = 0;

        ResetEvent(g_readEvent);

        if (ReadFile(pipe, cursor, remaining, &read, &g_readOverlapped))
        {
            if (read == 0)
            {
                return false;
            }
        }
        else
        {
            if (GetLastError() != ERROR_IO_PENDING)
            {
                return false;
            }

            if (!WaitForOverlapped(pipe, &g_readOverlapped, g_readEvent, &read) || read == 0)
            {
                return false;
            }
        }

        cursor += read;
        remaining -= read;
    }

    return true;
}

// ---------------------------------------------------------------- 监听端口播报

static bool RememberListenPort(unsigned short port)
{
    bool added = false;

    EnterCriticalSection(&g_listenLock);

    bool exists = false;

    for (LONG index = 0; index < g_listenPortCount && index < kMaxListenPorts; ++index)
    {
        if (g_listenPorts[index] == port)
        {
            exists = true;
            break;
        }
    }

    if (!exists && g_listenPortCount < kMaxListenPorts)
    {
        g_listenPorts[g_listenPortCount] = port;
        InterlockedIncrement(&g_listenPortCount);
        added = true;
    }

    LeaveCriticalSection(&g_listenLock);
    return added;
}

/// <summary>把某个端口写一条"请隧道监听"的帧（管道没连就直接返回 false）。</summary>
static bool SendListenFrameNow(unsigned short port)
{
    BYTE frame[kFrameHeaderLength] = { 0 };
    frame[0] = 4;   // type = 4：请隧道监听这个端口
    frame[4] = (BYTE)(port & 0xFF);
    frame[5] = (BYTE)(port >> 8);

    EnterCriticalSection(&g_pipeWriteLock);
    bool ok = WriteAll(g_pipe, frame, kFrameHeaderLength);
    LeaveCriticalSection(&g_pipeWriteLock);

    if (ok)
    {
        InterlockedIncrement(&g_listenFrames);
    }

    return ok;
}

/// <summary>管道刚连上（或重连）时，把记下来的监听端口全部补发一遍。</summary>
static void ReplayListenFrames()
{
    unsigned short ports[kMaxListenPorts];
    LONG count = 0;

    EnterCriticalSection(&g_listenLock);

    count = g_listenPortCount < kMaxListenPorts ? g_listenPortCount : kMaxListenPorts;

    for (LONG index = 0; index < count; ++index)
    {
        ports[index] = g_listenPorts[index];
    }

    LeaveCriticalSection(&g_listenLock);

    for (LONG index = 0; index < count; ++index)
    {
        if (SendListenFrameNow(ports[index]))
        {
            Log("补发监听请求：虚拟网上打开端口 %u。", (unsigned)ports[index]);
        }
    }
}

void TunnelAnnounceListenPort(unsigned short port)
{
    if (port == 0)
    {
        return;
    }

    bool added = RememberListenPort(port);

    if (g_dryRun != 0 || g_pipeConnected == 0)
    {
        return;   // 记下来了，等管道连上会补发
    }

    if (SendListenFrameNow(port) && added)
    {
        Log("已请隧道在虚拟网上打开端口 %u（游戏在这个端口上收包）。", (unsigned)port);
    }
}

bool TunnelIsLanRelatedPort(unsigned short port)
{
    if (port == 0)
    {
        return false;
    }

    if (IsLanPort(port))
    {
        return true;
    }

    bool found = false;

    EnterCriticalSection(&g_listenLock);

    for (LONG index = 0; index < g_listenPortCount && index < kMaxListenPorts; ++index)
    {
        if (g_listenPorts[index] == port)
        {
            found = true;
            break;
        }
    }

    LeaveCriticalSection(&g_listenLock);
    return found;
}

// ---------------------------------------------------------------- 出站帧

void TunnelSendOutboundFrame(unsigned short sourcePort, unsigned short destinationPort,
    unsigned int destinationIp, bool isBroadcast, const char* data, int length)
{
    if (g_pipeConnected == 0 || length <= 0 || g_dryRun != 0)
    {
        InterlockedIncrement(&g_outboundDropped);

        char reason[96] = { 0 };
        sprintf_s(reason, "管道未连接=%d，长度=%d，只记录模式=%d",
            g_pipeConnected == 0, length, g_dryRun != 0);
        LogDropThrottled(reason);
        return;
    }

    if (length > 65507)
    {
        length = 65507;
    }

    BYTE frame[kFrameHeaderLength + 65507];
    frame[0] = 1;                     // type = 1：出站
    frame[1] = isBroadcast ? 1 : 0;   // flags bit0 = 广播
    frame[2] = (BYTE)(sourcePort & 0xFF);
    frame[3] = (BYTE)(sourcePort >> 8);
    frame[4] = (BYTE)(destinationPort & 0xFF);
    frame[5] = (BYTE)(destinationPort >> 8);
    frame[6] = (BYTE)(length & 0xFF);
    frame[7] = (BYTE)((length >> 8) & 0xFF);

    unsigned int networkIp = htonl(destinationIp);
    memcpy(frame + 8, &networkIp, 4);

    memcpy(frame + kFrameHeaderLength, data, length);

    EnterCriticalSection(&g_pipeWriteLock);

    LogDebug("写入隧道帧：本地 %u → 目标 %u，%d 字节……", sourcePort, destinationPort, length);
    bool ok = WriteAll(g_pipe, frame, kFrameHeaderLength + (DWORD)length);
    LogDebug("写入隧道帧返回：%d（Win32 错误码 %lu）", ok, GetLastError());

    LeaveCriticalSection(&g_pipeWriteLock);

    if (ok)
    {
        InterlockedIncrement(&g_outboundFrames);

        // 广播一次扫描 100 个（太吵，只进详细日志）；单播很少，一律记下来，
        // 这样"加入房间"这类流程发了什么、发给谁，一眼就能看到。
        if (isBroadcast)
        {
            LogDebug("→ 隧道（广播）：本地 %u → :%u，%d 字节。", sourcePort, destinationPort, length);
        }
        else
        {
            char text[32] = { 0 };
            FormatIpv4(destinationIp, text, sizeof(text));
            Log("→ 隧道（单播）：本地 %u → %s:%u，%d 字节。", sourcePort, text, destinationPort, length);
        }
    }
    else
    {
        InterlockedIncrement(&g_outboundDropped);
        Log("写入隧道失败，Win32 错误码 %lu —— 标记管道为断开，等下一次重连。", GetLastError());
        g_pipeConnected = 0;
    }
}

// ---------------------------------------------------------------- 连接 / 收帧线程

static DWORD WINAPI PipeReaderThread(LPVOID parameter)
{
    HANDLE pipe = (HANDLE)parameter;

    BYTE header[kFrameHeaderLength];
    std::vector<BYTE> payload;

    while (true)
    {
        if (!ReadAll(pipe, header, kFrameHeaderLength))
        {
            break;
        }

        unsigned int sourcePort = (unsigned int)(header[2] | (header[3] << 8));
        unsigned int destinationPort = (unsigned int)(header[4] | (header[5] << 8));
        unsigned int length = (unsigned int)(header[6] | (header[7] << 8));

        unsigned int networkIp = 0;
        memcpy(&networkIp, header + 8, 4);
        unsigned int hostIp = ntohl(networkIp);

        if (length > 65507)
        {
            break;
        }

        payload.resize(length);

        if (length > 0 && !ReadAll(pipe, payload.data(), length))
        {
            break;
        }

        if (header[0] == 2)
        {
            // 入站：对端 hostIp:sourcePort → 本地 destinationPort
            DeliverInbound((unsigned short)destinationPort, hostIp, (unsigned short)sourcePort,
                payload.data(), length);
            InterlockedIncrement(&g_inboundFrames);
            LogDebug("← 隧道：来自 %u，投给本地 %u，%u 字节。", hostIp, destinationPort, length);
        }
        else if (header[0] == 3)
        {
            // 控制帧：本机虚拟 IP —— 用它算出虚拟网段，判断哪些单播包该转发。
            g_localVirtualIp = hostIp;

            char text[32] = { 0 };
            FormatIpv4(g_localVirtualIp, text, sizeof(text));
            Log("隧道已告知本机虚拟 IP：%s（该 IP 所在 /24 网段里的单播也会转发给对端）", text);
        }
    }

    g_pipeConnected = 0;

    // 关闭这次连接用的句柄（连接线程下次会建新的），避免句柄泄漏。
    if (g_pipe == pipe)
    {
        g_pipe = INVALID_HANDLE_VALUE;
    }

    CloseHandle(pipe);

    Log("与启动器的管道已断开。");
    return 0;
}

static DWORD WINAPI PipeConnectThread(LPVOID parameter)
{
    (void)parameter;

    while (g_enabled != 0)
    {
        if (g_pipeConnected != 0)
        {
            Sleep(1000);
            continue;
        }

        HANDLE pipe = CreateFileA(g_pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
            OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);

        if (pipe == INVALID_HANDLE_VALUE)
        {
            DWORD now = GetTickCount();
            LONG last = InterlockedExchange(&g_connectFailLogTick, (LONG)now);

            if (last == 0 || (DWORD)(now - (DWORD)last) > 30000)
            {
                Log("还没连接到启动器的游戏隧道（错误码 %lu）—— 启动器没开、或者隧道没启动？",
                    GetLastError());
            }

            Sleep(1000);
            continue;
        }

        if (g_readEvent == nullptr)
        {
            g_readEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
            g_writeEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
            g_readOverlapped.hEvent = g_readEvent;
            g_writeOverlapped.hEvent = g_writeEvent;
        }

        g_pipe = pipe;
        g_pipeConnected = 1;

        Log("已连接到启动器的游戏隧道管道。");

        // 打个招呼（type=9，空帧）：一是验证管道确实能写，二是让启动器知道 Hook 已就位。
        BYTE hello[kFrameHeaderLength] = { 9, 0, 0, 0, 0, 0, 0, 0 };

        EnterCriticalSection(&g_pipeWriteLock);
        bool helloOk = WriteAll(pipe, hello, kFrameHeaderLength);
        LeaveCriticalSection(&g_pipeWriteLock);

        Log("握手帧写入结果：%d（Win32 错误码 %lu）。", helloOk, GetLastError());

        // 游戏可能在我们连上之前就把监听端口绑好了，这里补发一遍。
        ReplayListenFrames();

        HANDLE reader = CreateThread(nullptr, 0, PipeReaderThread, pipe, 0, nullptr);

        if (reader == nullptr)
        {
            g_pipeConnected = 0;
            CloseHandle(pipe);
            Sleep(1000);
            continue;
        }

        CloseHandle(reader);
    }

    return 0;
}

void TunnelPipeStartConnectThread()
{
    HANDLE thread = CreateThread(nullptr, 0, PipeConnectThread, nullptr, 0, nullptr);

    if (thread != nullptr)
    {
        CloseHandle(thread);
    }
}
