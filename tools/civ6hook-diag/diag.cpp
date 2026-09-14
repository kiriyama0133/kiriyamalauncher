// ============================================================================
//  civ6hook-diag v2 —— 文明 6 联机 Hook 的「只记录不转发」诊断版
//
//  用途：把本 DLL 改名成 hookdll.dll 交给 injciv6.exe 注入游戏（或让启动器
//        自动注入）后，只记录游戏调用 Winsock 的情况，不做任何转发。
//
//  v2 相比 v1 补上了三件关键的事：
//    1) 【调用者归属】每次调用都记下是哪个模块（exe / GameCore / EOS / Steam）
//       发起的，用 _ReturnAddress + 模块范围表解析，不再「只知道有调用」。
//    2) 【收包结果】recv 类接口记录返回值、收到多少字节、来源地址和前 16 字节内容，
//       这样才能判断「游戏到底有没有正常收到局域网回包」。
//    3) 【建链过程】补挂 socket / WSASocketW / bind / connect / listen /
//       setsockopt / closesocket / send / recv。
//
//  另外每次运行都会写一份「按 socket 汇总」的摘要（civ6-hook-diag-summary.txt），
//  一眼就能看出：局域网 socket 是哪个、绑的哪个端口、有没有发过广播、有没有收到过包。
//
//  动态解析覆盖：很多程序是用 GetProcAddress("sendto") 这类方式拿函数指针的，
//  光改导入表(IAT)抓不到。这里改成 Hook GetProcAddress：一旦有人解析我们要看的
//  函数，就把我们自己的实现返回给它。这样不碰任何代码字节，绝对安全。
//
//  安全开关（环境变量，默认不启用）：
//    KIRIYAMA_DIAG_QUIET=1   只记关键行（不记轮询心跳）
// ============================================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <share.h>
#include <stdarg.h>
#include <string.h>
#include <string>
#include <vector>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "iphlpapi.lib")

// ---------------------------------------------------------------- 日志

static CRITICAL_SECTION g_logLock;
static std::vector<FILE*> g_logs;
static char g_kernelLogPath[MAX_PATH] = { 0 };   // 和 DLL 放一起（最可靠）
static char g_userLogPath[MAX_PATH] = { 0 };     // %LOCALAPPDATA%\kiriyamalauncher\logs
static char g_dllPath[MAX_PATH] = { 0 };
static HMODULE g_selfModule = nullptr;
static volatile LONG g_quietMode = 0;
static HMODULE g_ws2Module = nullptr;
static HMODULE g_win32Module = nullptr;
static HMODULE g_kernelBaseModule = nullptr;

static void LogV(const char* format, va_list args)
{
    if (g_logs.empty())
    {
        return;
    }

    EnterCriticalSection(&g_logLock);

    for (FILE* log : g_logs)
    {
        SYSTEMTIME time;
        GetLocalTime(&time);
        fprintf(log, "[%02d:%02d:%02d.%03d] ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        vfprintf(log, format, args);
        fputc('\n', log);
        fflush(log);
    }

    LeaveCriticalSection(&g_logLock);
}

static void Log(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    LogV(format, args);
    va_end(args);
}

static void OpenLog()
{
    if (!g_logs.empty())
    {
        return;
    }

    SYSTEMTIME time;
    GetLocalTime(&time);

    // 1) 和 DLL 放一起（注入目录一定可写，最可靠）
    char directory[MAX_PATH] = { 0 };
    strcpy_s(directory, g_dllPath);
    char* slash = strrchr(directory, '\\');
    if (slash != nullptr)
    {
        *slash = '\0';
    }

    sprintf_s(g_kernelLogPath, "%s\\civ6-hook-diag-%04d%02d%02d-%02d%02d%02d.log",
        directory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    // 用 _fsopen + _SH_DENYNO：别的进程（脚本 / 编辑器）也能边写边读这个日志。
    FILE* kernelLog = _fsopen(g_kernelLogPath, "a", _SH_DENYNO);
    if (kernelLog != nullptr)
    {
        setvbuf(kernelLog, nullptr, _IONBF, 0);
        g_logs.push_back(kernelLog);
    }

    // 2) 同时写到 %LOCALAPPDATA%\kiriyamalauncher\logs
    char userDirectory[MAX_PATH] = { 0 };
    char* localAppData = nullptr;
    size_t localAppDataLength = 0;

    if (_dupenv_s(&localAppData, &localAppDataLength, "LOCALAPPDATA") == 0 && localAppData != nullptr)
    {
        sprintf_s(userDirectory, "%s\\kiriyamalauncher\\logs", localAppData);
        free(localAppData);
    }
    else
    {
        strcpy_s(userDirectory, "C:\\kiriyamalauncher-logs");
    }

    CreateDirectoryA(userDirectory, nullptr);

    sprintf_s(g_userLogPath, "%s\\civ6-hook-diag-%04d%02d%02d-%02d%02d%02d.log",
        userDirectory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    FILE* userLog = _fsopen(g_userLogPath, "a", _SH_DENYNO);
    if (userLog != nullptr)
    {
        setvbuf(userLog, nullptr, _IONBF, 0);
        g_logs.push_back(userLog);
    }
}

// ---------------------------------------------------------------- 模块范围表（调用者归属）

struct ModuleRange
{
    unsigned long long base;
    unsigned long long end;
    char name[64];
};

static const int kMaxModules = 192;
static ModuleRange g_modules[kMaxModules];
static volatile LONG g_moduleCount = 0;
static CRITICAL_SECTION g_moduleLock;

static void RefreshModuleTable()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());

    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return;
    }

    ModuleRange local[kMaxModules];
    int count = 0;

    MODULEENTRY32W entry;
    entry.dwSize = sizeof(entry);

    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            if (count >= kMaxModules)
            {
                break;
            }

            WideCharToMultiByte(CP_UTF8, 0, entry.szModule, -1, local[count].name, 64, nullptr, nullptr);
            local[count].base = (unsigned long long)(uintptr_t)entry.modBaseAddr;
            local[count].end = local[count].base + (unsigned long long)entry.modBaseSize;
            ++count;
        } while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);

    EnterCriticalSection(&g_moduleLock);

    for (int index = 0; index < count; ++index)
    {
        g_modules[index] = local[index];
    }

    InterlockedExchange(&g_moduleCount, count);
    LeaveCriticalSection(&g_moduleLock);
}

/// <summary>
/// 把调用者地址翻译成「模块名+偏移」。传入的地址来自 _ReturnAddress()，
/// 也就是「谁调用了被 Hook 的 Winsock 函数」。
/// </summary>
static void FormatCaller(char* buffer, size_t length, void* address)
{
    unsigned long long value = (unsigned long long)(uintptr_t)address;

    if (InterlockedCompareExchange(&g_moduleCount, 0, 0) == 0)
    {
        RefreshModuleTable();
    }

    EnterCriticalSection(&g_moduleLock);

    for (int index = 0; index < g_moduleCount; ++index)
    {
        if (value >= g_modules[index].base && value < g_modules[index].end)
        {
            sprintf_s(buffer, length, "%s+0x%llX",
                g_modules[index].name, value - g_modules[index].base);
            LeaveCriticalSection(&g_moduleLock);
            return;
        }
    }

    LeaveCriticalSection(&g_moduleLock);
    sprintf_s(buffer, length, "0x%llX", value);
}

// ---------------------------------------------------------------- 原始函数

typedef SOCKET (WSAAPI* SocketFn)(int, int, int);
typedef SOCKET (WSAAPI* WSASocketWFn)(int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);
typedef int (WSAAPI* BindFn)(SOCKET, const struct sockaddr*, int);
typedef int (WSAAPI* ConnectFn)(SOCKET, const struct sockaddr*, int);
typedef int (WSAAPI* ListenFn)(SOCKET, int);
typedef int (WSAAPI* GetSockNameFn)(SOCKET, struct sockaddr*, int*);
typedef int (WSAAPI* SetSockOptFn)(SOCKET, int, int, const char*, int);
typedef int (WSAAPI* CloseSocketFn)(SOCKET);
// 注意参数顺序：sendto(SOCKET, const char* buf, int len, int flags, const sockaddr* to, int tolen)
typedef int (WSAAPI* SendToFn)(SOCKET, const char*, int, int, const struct sockaddr*, int);
typedef int (WSAAPI* WSASendToFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* SendFn)(SOCKET, const char*, int, int);
typedef int (WSAAPI* WSASendFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* RecvFromFn)(SOCKET, char*, int, int, struct sockaddr*, int*);
typedef int (WSAAPI* RecvFn)(SOCKET, char*, int, int);
typedef int (WSAAPI* WSARecvFromFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* WSARecvFn)(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
typedef int (WSAAPI* SelectFn)(int, fd_set*, fd_set*, fd_set*, const struct timeval*);
typedef int (WSAAPI* WSAPollFn)(LPWSAPOLLFD, ULONG, INT);
typedef int (WSAAPI* WSAEventSelectFn)(SOCKET, WSAEVENT, long);
typedef int (WSAAPI* WSAEnumNetworkEventsFn)(SOCKET, WSAEVENT, LPWSANETWORKEVENTS);
typedef HANDLE (WINAPI* CreateIoCompletionPortFn)(HANDLE, HANDLE, ULONG_PTR, DWORD);
typedef DWORD (WSAAPI* WSAWaitForMultipleEventsFn)(DWORD, const WSAEVENT*, BOOL, DWORD, BOOL);
typedef BOOL (WSAAPI* WSAGetOverlappedResultFn)(SOCKET, LPWSAOVERLAPPED, LPDWORD, BOOL, LPDWORD);
typedef FARPROC (WINAPI* GetProcAddressFn)(HMODULE, LPCSTR);

static SocketFn original_socket = nullptr;
static WSASocketWFn original_WSASocketW = nullptr;
static BindFn original_bind = nullptr;
static ConnectFn original_connect = nullptr;
static ListenFn original_listen = nullptr;
static GetSockNameFn original_getsockname = nullptr;
static SetSockOptFn original_setsockopt = nullptr;
static CloseSocketFn original_closesocket = nullptr;
static SendToFn original_sendto = nullptr;
static WSASendToFn original_WSASendTo = nullptr;
static SendFn original_send = nullptr;
static WSASendFn original_WSASend = nullptr;
static RecvFromFn original_recvfrom = nullptr;
static RecvFn original_recv = nullptr;
static WSARecvFromFn original_WSARecvFrom = nullptr;
static WSARecvFn original_WSARecv = nullptr;
static SelectFn original_select = nullptr;
static WSAPollFn original_WSAPoll = nullptr;
static WSAEventSelectFn original_WSAEventSelect = nullptr;
static WSAEnumNetworkEventsFn original_WSAEnumNetworkEvents = nullptr;
static CreateIoCompletionPortFn original_CreateIoCompletionPort = nullptr;
static WSAWaitForMultipleEventsFn original_WSAWaitForMultipleEvents = nullptr;
static WSAGetOverlappedResultFn original_WSAGetOverlappedResult = nullptr;
static GetProcAddressFn original_GetProcAddress = nullptr;

// ---------------------------------------------------------------- 全局统计

static volatile LONG g_sendToCount = 0;        // sendto + WSASendTo + send
static volatile LONG g_wsSendCount = 0;        // WSASend
static volatile LONG g_recvFromCount = 0;      // recvfrom + WSARecvFrom + recv + WSARecv
static volatile LONG g_overlappedRecvCount = 0;
static volatile LONG g_blockingRecvCount = 0;
static volatile LONG g_dataRecvCount = 0;      // 收到过数据的次数（>0 字节）
static volatile LONG g_broadcastSendCount = 0;
static volatile LONG g_lanPortSendCount = 0;
static volatile LONG g_selectCount = 0;
static volatile LONG g_pollCount = 0;
static volatile LONG g_eventSelectCount = 0;
static volatile LONG g_enumEventsCount = 0;
static volatile LONG g_iocpCount = 0;
static volatile LONG g_waitEventCount = 0;
static volatile LONG g_overlappedResultCount = 0;
static volatile LONG g_socketCreateCount = 0;
static volatile LONG g_bindCount = 0;
static volatile LONG g_connectCount = 0;

// ---------------------------------------------------------------- 发送目标统计

struct SendTargetStat
{
    char text[96];
    char caller[96];
    long count;
    long bytes;
};

static SendTargetStat g_sendTargets[64];
static int g_sendTargetCount = 0;
static CRITICAL_SECTION g_targetLock;

static void RecordSendTarget(const char* target, const char* caller, DWORD length)
{
    EnterCriticalSection(&g_targetLock);

    for (int index = 0; index < g_sendTargetCount; ++index)
    {
        if (strcmp(g_sendTargets[index].text, target) == 0
            && strcmp(g_sendTargets[index].caller, caller) == 0)
        {
            g_sendTargets[index].count++;
            g_sendTargets[index].bytes += (long)length;
            LeaveCriticalSection(&g_targetLock);
            return;
        }
    }

    if (g_sendTargetCount < 64)
    {
        strcpy_s(g_sendTargets[g_sendTargetCount].text, target);
        strcpy_s(g_sendTargets[g_sendTargetCount].caller, caller);
        g_sendTargets[g_sendTargetCount].count = 1;
        g_sendTargets[g_sendTargetCount].bytes = (long)length;
        g_sendTargetCount++;
    }

    LeaveCriticalSection(&g_targetLock);
}

// ---------------------------------------------------------------- socket 明细表

struct SocketStat
{
    unsigned long long handle;
    char creator[96];
    int family;
    int type;
    int protocol;
    bool boundKnown;
    unsigned short boundPort;
    bool lanPort;
    int broadcastOption;        // -1 = 没设置过，0/1 = setsockopt(SO_BROADCAST) 的结果
    bool sawBroadcastSend;
    bool sawData;
    bool closed;                // 已关闭：留档给摘要用，槽位可以被后来的 socket 复用
    bool focus;                 // 重点关注：绑了 62900-62999，或往广播/629xx 发过包
    long sendCalls;
    long sendBytes;
    long recvCalls;
    long recvBytes;
    long recvDataCount;
    long recvLogged;
    long errorCount;
    int lastError;
    char lastFrom[64];
    char lastSendTo[64];
    char lastPayload[80];
    char firstDataFrom[64];
    char firstDataPayload[80];
};

static const int kMaxSockets = 64;
static SocketStat g_sockets[kMaxSockets];
static CRITICAL_SECTION g_socketLock;

/// <summary>拿一个 socket 的统计槽位；拿不到（表满）返回 null。调用者必须已经持有 g_socketLock。</summary>
static SocketStat* TouchSocketLocked(unsigned long long handle, const char* creator)
{
    int freeIndex = -1;
    int recycleIndex = -1;

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == handle)
        {
            return &g_sockets[index];
        }

        if (freeIndex < 0 && g_sockets[index].handle == 0)
        {
            freeIndex = index;
        }
        else if (recycleIndex < 0 && g_sockets[index].closed)
        {
            recycleIndex = index;
        }
    }

    if (freeIndex < 0)
    {
        freeIndex = recycleIndex;
    }

    if (freeIndex < 0)
    {
        return nullptr;
    }

    SocketStat* entry = &g_sockets[freeIndex];
    memset(entry, 0, sizeof(SocketStat));
    entry->handle = handle;
    entry->broadcastOption = -1;
    entry->lastError = 0;
    strcpy_s(entry->creator, creator != nullptr ? creator : "?");
    return entry;
}

/// <summary>socket 关掉了：不删记录，只打个标记，这样摘要里还能看到它干过什么。</summary>
static void MarkSocketClosed(unsigned long long handle)
{
    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == handle)
        {
            g_sockets[index].closed = true;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
}

// ---------------------------------------------------------------- 小工具

static const unsigned short kLanProbePortLow = 62900;
static const unsigned short kLanProbePortHigh = 62999;

static bool IsLanProbePort(unsigned int port)
{
    return port >= kLanProbePortLow && port <= kLanProbePortHigh;
}

static void FormatAddress(const struct sockaddr* address, char* buffer, size_t length)
{
    buffer[0] = '\0';

    if (address == nullptr)
    {
        strcpy_s(buffer, length, "?");
        return;
    }

    if (address->sa_family == AF_INET)
    {
        const struct sockaddr_in* ipv4 = reinterpret_cast<const struct sockaddr_in*>(address);
        char ip[INET_ADDRSTRLEN] = { 0 };
        inet_ntop(AF_INET, &ipv4->sin_addr, ip, sizeof(ip));
        sprintf_s(buffer, length, "%s:%u", ip, (unsigned)ntohs(ipv4->sin_port));
    }
    else if (address->sa_family == AF_INET6)
    {
        strcpy_s(buffer, length, "ipv6");
    }
    else
    {
        sprintf_s(buffer, length, "family=%d", (int)address->sa_family);
    }
}

static unsigned int AddressPort(const struct sockaddr* address)
{
    if (address == nullptr || address->sa_family != AF_INET)
    {
        return 0;
    }

    const struct sockaddr_in* ipv4 = reinterpret_cast<const struct sockaddr_in*>(address);
    return (unsigned int)ntohs(ipv4->sin_port);
}

static bool IsBroadcastOrLanAddress(const char* ip)
{
    if (strcmp(ip, "255.255.255.255") == 0)
    {
        return true;
    }

    size_t length = strlen(ip);
    return length > 4 && strcmp(ip + length - 4, ".255") == 0;
}

static void FormatBytes(const char* data, int length, char* buffer, size_t bufferLength)
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

/// <summary>目标 IP:端口 里取 IP 部分，用于判断是不是广播地址。</summary>
static void SplitAddress(const char* addressText, char* ipOnly, size_t length)
{
    strcpy_s(ipOnly, length, addressText);

    char* colon = strrchr(ipOnly, ':');

    if (colon != nullptr)
    {
        *colon = '\0';
    }
}

// ---------------------------------------------------------------- Hook 实现

struct HookTarget
{
    const char* functionName;
    void* hookFunction;
    void** originalFunction;
};

static SOCKET WSAAPI Hook_socket(int, int, int);
static SOCKET WSAAPI Hook_WSASocketW(int, int, int, LPWSAPROTOCOL_INFOW, GROUP, DWORD);
static int WSAAPI Hook_bind(SOCKET, const struct sockaddr*, int);
static int WSAAPI Hook_connect(SOCKET, const struct sockaddr*, int);
static int WSAAPI Hook_listen(SOCKET, int);
static int WSAAPI Hook_getsockname(SOCKET, struct sockaddr*, int*);
static int WSAAPI Hook_setsockopt(SOCKET, int, int, const char*, int);
static int WSAAPI Hook_closesocket(SOCKET);
static int WSAAPI Hook_sendto(SOCKET, const char*, int, int, const struct sockaddr*, int);
static int WSAAPI Hook_WSASendTo(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, const struct sockaddr*, int, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_send(SOCKET, const char*, int, int);
static int WSAAPI Hook_WSASend(SOCKET, LPWSABUF, DWORD, LPDWORD, DWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_recvfrom(SOCKET, char*, int, int, struct sockaddr*, int*);
static int WSAAPI Hook_WSARecvFrom(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, struct sockaddr*, LPINT, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_recv(SOCKET, char*, int, int);
static int WSAAPI Hook_WSARecv(SOCKET, LPWSABUF, DWORD, LPDWORD, LPDWORD, LPWSAOVERLAPPED, LPWSAOVERLAPPED_COMPLETION_ROUTINE);
static int WSAAPI Hook_select(int, fd_set*, fd_set*, fd_set*, const struct timeval*);
static int WSAAPI Hook_WSAPoll(LPWSAPOLLFD, ULONG, INT);
static int WSAAPI Hook_WSAEventSelect(SOCKET, WSAEVENT, long);
static int WSAAPI Hook_WSAEnumNetworkEvents(SOCKET, WSAEVENT, LPWSANETWORKEVENTS);
static HANDLE WINAPI Hook_CreateIoCompletionPort(HANDLE, HANDLE, ULONG_PTR, DWORD);
static DWORD WSAAPI Hook_WSAWaitForMultipleEvents(DWORD, const WSAEVENT*, BOOL, DWORD, BOOL);
static BOOL WSAAPI Hook_WSAGetOverlappedResult(SOCKET, LPWSAOVERLAPPED, LPDWORD, BOOL, LPDWORD);
static FARPROC WINAPI Hook_GetProcAddress(HMODULE, LPCSTR);

static const HookTarget g_targets[] =
{
    { "socket",                (void*)Hook_socket,                (void**)&original_socket },
    { "WSASocketW",            (void*)Hook_WSASocketW,            (void**)&original_WSASocketW },
    { "bind",                  (void*)Hook_bind,                  (void**)&original_bind },
    { "connect",               (void*)Hook_connect,               (void**)&original_connect },
    { "listen",                (void*)Hook_listen,                (void**)&original_listen },
    { "getsockname",           (void*)Hook_getsockname,           (void**)&original_getsockname },
    { "setsockopt",            (void*)Hook_setsockopt,            (void**)&original_setsockopt },
    { "closesocket",           (void*)Hook_closesocket,           (void**)&original_closesocket },
    { "sendto",                (void*)Hook_sendto,                (void**)&original_sendto },
    { "WSASendTo",             (void*)Hook_WSASendTo,             (void**)&original_WSASendTo },
    { "send",                  (void*)Hook_send,                  (void**)&original_send },
    { "WSASend",               (void*)Hook_WSASend,               (void**)&original_WSASend },
    { "recvfrom",              (void*)Hook_recvfrom,              (void**)&original_recvfrom },
    { "WSARecvFrom",           (void*)Hook_WSARecvFrom,           (void**)&original_WSARecvFrom },
    { "recv",                  (void*)Hook_recv,                  (void**)&original_recv },
    { "WSARecv",               (void*)Hook_WSARecv,               (void**)&original_WSARecv },
    { "select",                (void*)Hook_select,                (void**)&original_select },
    { "WSAPoll",               (void*)Hook_WSAPoll,               (void**)&original_WSAPoll },
    { "WSAEventSelect",        (void*)Hook_WSAEventSelect,        (void**)&original_WSAEventSelect },
    { "WSAEnumNetworkEvents",  (void*)Hook_WSAEnumNetworkEvents,  (void**)&original_WSAEnumNetworkEvents },
    { "CreateIoCompletionPort",(void*)Hook_CreateIoCompletionPort,(void**)&original_CreateIoCompletionPort },
    { "WSAWaitForMultipleEvents", (void*)Hook_WSAWaitForMultipleEvents, (void**)&original_WSAWaitForMultipleEvents },
    { "WSAGetOverlappedResult", (void*)Hook_WSAGetOverlappedResult, (void**)&original_WSAGetOverlappedResult },
    { "GetProcAddress",        (void*)Hook_GetProcAddress,        (void**)&original_GetProcAddress }
};

static const int kTargetCount = (int)(sizeof(g_targets) / sizeof(g_targets[0]));

/// <summary>
/// 直接向 ws2_32 要一个「真正的」函数地址（不走我们自己的 Hook）。
/// 万一解析回来的就是我们自己的 Hook，就当没解析到，避免递归。
/// </summary>
static void* ResolveRealWs2Export(const char* name)
{
    HMODULE module = GetModuleHandleA("ws2_32.dll");

    if (module == nullptr)
    {
        return nullptr;
    }

    void* proc = (void*)GetProcAddress(module, name);

    for (int index = 0; index < kTargetCount; ++index)
    {
        if (proc == g_targets[index].hookFunction)
        {
            return nullptr;
        }
    }

    return proc;
}

/// <summary>自己问一次系统：这个 socket 绑在哪个端口（用真正的 getsockname，不经过 Hook）。</summary>
static bool QuerySocketLocalPort(unsigned long long handle, unsigned short* port, char* text, size_t textLength)
{
    static GetSockNameFn realGetSockName = nullptr;

    if (realGetSockName == nullptr)
    {
        realGetSockName = (original_getsockname != nullptr)
            ? original_getsockname
            : (GetSockNameFn)ResolveRealWs2Export("getsockname");
    }

    if (realGetSockName == nullptr)
    {
        return false;
    }

    struct sockaddr_in actual;
    memset(&actual, 0, sizeof(actual));
    int length = sizeof(actual);

    if (realGetSockName((SOCKET)handle, reinterpret_cast<struct sockaddr*>(&actual), &length) != 0)
    {
        return false;
    }

    *port = (unsigned short)AddressPort(reinterpret_cast<struct sockaddr*>(&actual));
    FormatAddress(reinterpret_cast<struct sockaddr*>(&actual), text, textLength);
    return true;
}

static volatile LONG g_portProbeCount = 0;

// ---------------------------------------------------------------- 重点关注 socket
//
// 只要一个 socket 跟局域网扫描沾边（绑了 62900-62999、或者往广播/629xx 发过包），
// 就把它标成「重点关注」：它的收包、被 select/WSAPoll 等待 都会无条件记下来。
// 这些记录是判断「游戏在哪里等房间应答」的直接证据。

static volatile LONG g_focusLogBudget = 0;

static bool ConsumeFocusBudget()
{
    return InterlockedIncrement(&g_focusLogBudget) <= 4000;
}

/// <summary>把 socket 写成 "6500(62056)★" 这种形式，方便一眼对应上端口。</summary>
static void FormatSocketLabel(unsigned long long handle, char* buffer, size_t length)
{
    unsigned short port = 0;
    bool known = false;
    bool focus = false;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == handle)
        {
            known = g_sockets[index].boundKnown;
            port = g_sockets[index].boundPort;
            focus = g_sockets[index].focus;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);

    if (known && port != 0)
    {
        sprintf_s(buffer, length, "%llu(%u)%s", handle, (unsigned)port, focus ? "★" : "");
    }
    else
    {
        sprintf_s(buffer, length, "%llu(?)%s", handle, focus ? "★" : "");
    }
}

static bool IsSocketFocused(unsigned long long handle)
{
    bool focus = false;

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        if (g_sockets[index].handle == handle)
        {
            focus = g_sockets[index].focus;
            break;
        }
    }

    LeaveCriticalSection(&g_socketLock);
    return focus;
}

/// <summary>第一次见到某个 socket 时，主动查一次它的本地端口并记下来（这是判断「是不是局域网 socket」最快的方法）。</summary>
static void NoteSocketFirstSeen(unsigned long long handle, const char* where)
{
    if (InterlockedIncrement(&g_portProbeCount) > 64)
    {
        return;
    }

    unsigned short port = 0;
    char text[64] = { 0 };
    bool resolved = QuerySocketLocalPort(handle, &port, text, sizeof(text));

    EnterCriticalSection(&g_socketLock);

    SocketStat* entry = TouchSocketLocked(handle, where);

    if (entry != nullptr)
    {
        entry->boundKnown = resolved && port != 0;
        entry->boundPort = port;
        entry->lanPort = IsLanProbePort(port);
    }

    LeaveCriticalSection(&g_socketLock);

    if (resolved && port != 0)
    {
        Log("★ 首次见到 sock=%llu（%s 侧）：本地绑定端口 = %s%s",
            handle, where, text,
            IsLanProbePort(port) ? "  ← 落在 62900-62999 局域网扫描端口段！" : "");
    }
    else
    {
        Log("  首次见到 sock=%llu（%s 侧）：暂时查不到本地绑定端口", handle, where);
    }
}

// 原始函数指针必须有效才可能转发调用：万一某个导出没解析到（比如补挂顺序异常），
// 宁可记一条日志后直接返回错误，也绝不能跳空指针把宿主进程带崩。
#define REQUIRE_ORIGINAL(pointer, fallbackValue)                        \
    if ((pointer) == nullptr)                                           \
    {                                                                   \
        Log("!! 原始函数指针为空：%s —— 本次调用已被跳过", #pointer);      \
        return fallbackValue;                                           \
    }

static SOCKET WSAAPI Hook_socket(int family, int type, int protocol)
{
    REQUIRE_ORIGINAL(original_socket, INVALID_SOCKET);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    SOCKET result = original_socket(family, type, protocol);
    InterlockedIncrement(&g_socketCreateCount);

    Log("socket        -> %llu family=%d type=%d proto=%d 由 %s 创建",
        (unsigned long long)result, family, type, protocol, caller);

    if (result != INVALID_SOCKET)
    {
        EnterCriticalSection(&g_socketLock);
        SocketStat* entry = TouchSocketLocked((unsigned long long)result, caller);

        if (entry != nullptr)
        {
            entry->family = family;
            entry->type = type;
            entry->protocol = protocol;
        }

        LeaveCriticalSection(&g_socketLock);
    }

    return result;
}

static SOCKET WSAAPI Hook_WSASocketW(int family, int type, int protocol,
    LPWSAPROTOCOL_INFOW protocolInfo, GROUP group, DWORD flags)
{
    REQUIRE_ORIGINAL(original_WSASocketW, INVALID_SOCKET);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    SOCKET result = original_WSASocketW(family, type, protocol, protocolInfo, group, flags);
    InterlockedIncrement(&g_socketCreateCount);

    Log("WSASocketW    -> %llu family=%d type=%d proto=%d 由 %s 创建",
        (unsigned long long)result, family, type, protocol, caller);

    if (result != INVALID_SOCKET)
    {
        EnterCriticalSection(&g_socketLock);
        SocketStat* entry = TouchSocketLocked((unsigned long long)result, caller);

        if (entry != nullptr)
        {
            entry->family = family;
            entry->type = type;
            entry->protocol = protocol;
        }

        LeaveCriticalSection(&g_socketLock);
    }

    return result;
}

static int WSAAPI Hook_bind(SOCKET socket, const struct sockaddr* address, int addressLength)
{
    REQUIRE_ORIGINAL(original_bind, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char text[64] = { 0 };
    FormatAddress(address, text, sizeof(text));

    int result = original_bind(socket, address, addressLength);
    int error = WSAGetLastError();
    InterlockedIncrement(&g_bindCount);

    Log("bind          sock=%llu addr=%s result=%d errno=%d 由 %s 调用",
        (unsigned long long)socket, text, result, result == 0 ? 0 : error, caller);

    if (result == 0)
    {
        // 端口写 0 时得再问一次系统，才知道真正绑到了哪个端口。
        struct sockaddr_in actual;
        int actualLength = sizeof(actual);
        unsigned short boundPort = (unsigned short)AddressPort(address);

        if (boundPort == 0 && original_getsockname != nullptr
            && original_getsockname(socket, reinterpret_cast<struct sockaddr*>(&actual), &actualLength) == 0)
        {
            boundPort = (unsigned short)AddressPort(reinterpret_cast<struct sockaddr*>(&actual));
            char actualText[64] = { 0 };
            FormatAddress(reinterpret_cast<struct sockaddr*>(&actual), actualText, sizeof(actualText));
            Log("  └ 实际绑定端口：%s", actualText);
        }

        EnterCriticalSection(&g_socketLock);
        SocketStat* entry = TouchSocketLocked((unsigned long long)socket, caller);

        if (entry != nullptr)
        {
            entry->boundKnown = boundPort != 0;
            entry->boundPort = boundPort;
            entry->lanPort = IsLanProbePort(boundPort);

            if (entry->lanPort)
            {
                entry->focus = true;
            }
        }

        LeaveCriticalSection(&g_socketLock);

        if (IsLanProbePort(boundPort))
        {
            Log("★ 局域网端口提示：sock=%llu 绑定了 %u（文明 6 的房间扫描端口段 62900-62999）",
                (unsigned long long)socket, (unsigned)boundPort);
        }
    }

    return result;
}

static int WSAAPI Hook_connect(SOCKET socket, const struct sockaddr* address, int addressLength)
{
    REQUIRE_ORIGINAL(original_connect, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char text[64] = { 0 };
    FormatAddress(address, text, sizeof(text));

    int result = original_connect(socket, address, addressLength);
    InterlockedIncrement(&g_connectCount);

    Log("connect       sock=%llu -> %s result=%d 由 %s 调用",
        (unsigned long long)socket, text, result, caller);

    return result;
}

static int WSAAPI Hook_listen(SOCKET socket, int backlog)
{
    REQUIRE_ORIGINAL(original_listen, SOCKET_ERROR);

    int result = original_listen(socket, backlog);
    Log("listen        sock=%llu backlog=%d result=%d", (unsigned long long)socket, backlog, result);
    return result;
}

static int WSAAPI Hook_getsockname(SOCKET socket, struct sockaddr* address, int* addressLength)
{
    REQUIRE_ORIGINAL(original_getsockname, SOCKET_ERROR);

    int result = original_getsockname(socket, address, addressLength);

    if (result == 0)
    {
        char text[64] = { 0 };
        FormatAddress(address, text, sizeof(text));
        Log("getsockname   sock=%llu -> %s", (unsigned long long)socket, text);

        EnterCriticalSection(&g_socketLock);
        SocketStat* entry = TouchSocketLocked((unsigned long long)socket, "?");

        if (entry != nullptr && !entry->boundKnown)
        {
            unsigned short port = (unsigned short)AddressPort(address);

            if (port != 0)
            {
                entry->boundKnown = true;
                entry->boundPort = port;
                entry->lanPort = IsLanProbePort(port);
            }
        }

        LeaveCriticalSection(&g_socketLock);
    }

    return result;
}

static int WSAAPI Hook_setsockopt(SOCKET socket, int level, int optionName,
    const char* optionValue, int optionLength)
{
    REQUIRE_ORIGINAL(original_setsockopt, SOCKET_ERROR);

    int result = original_setsockopt(socket, level, optionName, optionValue, optionLength);

    bool interesting = (level == SOL_SOCKET)
        && (optionName == SO_BROADCAST || optionName == SO_REUSEADDR || optionName == SO_RCVBUF || optionName == SO_SNDBUF);

    if (interesting)
    {
        char caller[96] = { 0 };
        FormatCaller(caller, sizeof(caller), _ReturnAddress());

        long value = 0;

        if (optionValue != nullptr && optionLength >= (int)sizeof(long))
        {
            value = *(reinterpret_cast<const long*>(optionValue));
        }
        else if (optionValue != nullptr && optionLength == (int)sizeof(int))
        {
            value = *(reinterpret_cast<const int*>(optionValue));
        }
        else if (optionValue != nullptr && optionLength == 1)
        {
            value = *(reinterpret_cast<const unsigned char*>(optionValue));
        }

        Log("setsockopt    sock=%llu level=%d opt=%d value=%ld result=%d 由 %s 调用",
            (unsigned long long)socket, level, optionName, value, result, caller);

        if (optionName == SO_BROADCAST)
        {
            EnterCriticalSection(&g_socketLock);
            SocketStat* entry = TouchSocketLocked((unsigned long long)socket, caller);

            if (entry != nullptr)
            {
                entry->broadcastOption = (value != 0) ? 1 : 0;
            }

            LeaveCriticalSection(&g_socketLock);
        }
    }

    return result;
}

static int WSAAPI Hook_closesocket(SOCKET socket)
{
    REQUIRE_ORIGINAL(original_closesocket, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    int result = original_closesocket(socket);

    Log("closesocket   sock=%llu result=%d 由 %s 调用", (unsigned long long)socket, result, caller);
    MarkSocketClosed((unsigned long long)socket);

    return result;
}

static int WSAAPI Hook_sendto(SOCKET socket, const char* data, int dataLength, int flags,
    const struct sockaddr* to, int toLength)
{
    REQUIRE_ORIGINAL(original_sendto, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char addressText[64] = { 0 };
    FormatAddress(to, addressText, sizeof(addressText));

    char ipOnly[64] = { 0 };
    SplitAddress(addressText, ipOnly, sizeof(ipOnly));

    char hex[80] = { 0 };
    FormatBytes(data, dataLength, hex, sizeof(hex));

    int result = original_sendto(socket, data, dataLength, flags, to, toLength);
    int error = WSAGetLastError();

    bool broadcast = IsBroadcastOrLanAddress(ipOnly);
    bool lanPort = IsLanProbePort(AddressPort(to));

    Log("%ssendto        sock=%llu dst=%-22s len=%d result=%d errno=%d data=[%s] 由 %s 调用",
        (broadcast || lanPort) ? "★" : "", (unsigned long long)socket, addressText, dataLength,
        result, result < 0 ? error : 0, hex, caller);

    InterlockedIncrement(&g_sendToCount);

    if (broadcast)
    {
        InterlockedIncrement(&g_broadcastSendCount);
    }

    if (lanPort)
    {
        InterlockedIncrement(&g_lanPortSendCount);
    }

    RecordSendTarget(addressText, caller, (DWORD)dataLength);

    bool firstSeen = false;

    EnterCriticalSection(&g_socketLock);
    SocketStat* entry = TouchSocketLocked((unsigned long long)socket, caller);

    if (entry != nullptr)
    {
        entry->sendCalls++;
        firstSeen = entry->sendCalls == 1;
        entry->sendBytes += dataLength;
        entry->sawBroadcastSend = entry->sawBroadcastSend || broadcast;
        strcpy_s(entry->lastSendTo, addressText);
        FormatBytes(data, dataLength, entry->lastPayload, sizeof(entry->lastPayload));

        if (broadcast || lanPort)
        {
            entry->focus = true;
        }
    }

    LeaveCriticalSection(&g_socketLock);

    if (firstSeen)
    {
        NoteSocketFirstSeen((unsigned long long)socket, "sendto");
    }

    return result;
}

static int WSAAPI Hook_WSASendTo(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent, DWORD flags,
    const struct sockaddr* to, int toLength, LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    REQUIRE_ORIGINAL(original_WSASendTo, SOCKET_ERROR);

    DWORD totalLength = 0;
    for (DWORD index = 0; index < bufferCount; ++index)
    {
        totalLength += buffers[index].len;
    }

    int result = original_WSASendTo(socket, buffers, bufferCount, bytesSent, flags, to, toLength, overlapped, completion);
    int error = WSAGetLastError();

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char addressText[64] = { 0 };
    FormatAddress(to, addressText, sizeof(addressText));

    char ipOnly[64] = { 0 };
    SplitAddress(addressText, ipOnly, sizeof(ipOnly));

    char hex[80] = { 0 };
    if (bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        FormatBytes(buffers[0].buf, (int)buffers[0].len, hex, sizeof(hex));
    }

    bool broadcast = IsBroadcastOrLanAddress(ipOnly);
    bool lanPort = IsLanProbePort(AddressPort(to));

    Log("%sWSASendTo     sock=%llu dst=%-22s len=%lu result=%d errno=%d overlapped=%-3s data=[%s] 由 %s 调用",
        (broadcast || lanPort) ? "★" : "", (unsigned long long)socket, addressText, (unsigned long)totalLength,
        result, result != 0 ? error : 0, overlapped != nullptr ? "yes" : "no", hex, caller);

    InterlockedIncrement(&g_sendToCount);

    if (broadcast)
    {
        InterlockedIncrement(&g_broadcastSendCount);
    }

    if (lanPort)
    {
        InterlockedIncrement(&g_lanPortSendCount);
    }

    RecordSendTarget(addressText, caller, totalLength);

    bool firstSeen = false;

    EnterCriticalSection(&g_socketLock);
    SocketStat* entry = TouchSocketLocked((unsigned long long)socket, caller);

    if (entry != nullptr)
    {
        entry->sendCalls++;
        firstSeen = entry->sendCalls == 1;
        entry->sendBytes += (long)totalLength;
        entry->sawBroadcastSend = entry->sawBroadcastSend || broadcast;
        strcpy_s(entry->lastSendTo, addressText);
        strcpy_s(entry->lastPayload, hex);

        if (broadcast || lanPort)
        {
            entry->focus = true;
        }
    }

    LeaveCriticalSection(&g_socketLock);

    if (firstSeen)
    {
        NoteSocketFirstSeen((unsigned long long)socket, "WSASendTo");
    }

    return result;
}

static int WSAAPI Hook_send(SOCKET socket, const char* data, int dataLength, int flags)
{
    REQUIRE_ORIGINAL(original_send, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char hex[80] = { 0 };
    FormatBytes(data, dataLength, hex, sizeof(hex));

    int result = original_send(socket, data, dataLength, flags);
    int error = WSAGetLastError();

    Log("send          sock=%llu len=%d result=%d errno=%d data=[%s] 由 %s 调用",
        (unsigned long long)socket, dataLength, result, result < 0 ? error : 0, hex, caller);

    InterlockedIncrement(&g_sendToCount);

    EnterCriticalSection(&g_socketLock);
    SocketStat* entry = TouchSocketLocked((unsigned long long)socket, caller);

    if (entry != nullptr)
    {
        entry->sendCalls++;
        entry->sendBytes += dataLength;
        strcpy_s(entry->lastPayload, hex);
    }

    LeaveCriticalSection(&g_socketLock);
    return result;
}

static int WSAAPI Hook_WSASend(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesSent, DWORD flags,
    LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    REQUIRE_ORIGINAL(original_WSASend, SOCKET_ERROR);

    DWORD totalLength = 0;
    for (DWORD index = 0; index < bufferCount; ++index)
    {
        totalLength += buffers[index].len;
    }

    int result = original_WSASend(socket, buffers, bufferCount, bytesSent, flags, overlapped, completion);
    int error = WSAGetLastError();

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    char hex[80] = { 0 };
    if (bufferCount > 0 && buffers != nullptr && buffers[0].buf != nullptr)
    {
        FormatBytes(buffers[0].buf, (int)buffers[0].len, hex, sizeof(hex));
    }

    Log("WSASend       sock=%llu len=%lu result=%d errno=%d overlapped=%-3s data=[%s] 由 %s 调用",
        (unsigned long long)socket, (unsigned long)totalLength, result, result != 0 ? error : 0,
        overlapped != nullptr ? "yes" : "no", hex, caller);

    InterlockedIncrement(&g_wsSendCount);
    return result;
}

// ---------------------------------------------------------------- Hook 实现（收包）

/// <summary>收包类 Hook 的公共记账 + 日志（控量，避免又写出 9 MB 日志）。</summary>
static void RecordReceive(unsigned long long handle, const char* caller, const char* apiName,
    int result, DWORD received, const struct sockaddr* from, const char* data, int dataLength,
    bool overlapped, LPWSAOVERLAPPED overlappedStruct, void* eventHandle)
{
    char fromText[64] = { 0 };
    FormatAddress(from, fromText, sizeof(fromText));

    char hex[80] = { 0 };
    FormatBytes(data, dataLength, hex, sizeof(hex));

    bool hasData = result == 0 && received > 0;
    bool shouldLog = false;
    bool firstData = false;
    bool firstSeen = false;
    long callCount = 0;
    int lastError = (result != 0) ? WSAGetLastError() : 0;

    EnterCriticalSection(&g_socketLock);
    SocketStat* entry = TouchSocketLocked(handle, caller);

    if (entry != nullptr)
    {
        entry->recvCalls++;
        callCount = entry->recvCalls;
        firstSeen = callCount == 1;

        if (hasData)
        {
            entry->recvBytes += (long)received;
            entry->recvDataCount++;
            entry->sawData = entry->sawData || entry->recvDataCount == 1;
            strcpy_s(entry->lastFrom, fromText);
            strcpy_s(entry->lastPayload, hex);

            if (entry->recvDataCount == 1)
            {
                firstData = true;
                strcpy_s(entry->firstDataFrom, fromText);
                strcpy_s(entry->firstDataPayload, hex);
            }
        }
        else if (result != 0)
        {
            if (lastError != entry->lastError)
            {
                entry->lastError = lastError;
                shouldLog = true;   // 错误码变了记一条，方便看清「一直 WSAEWOULDBLOCK」还是别的
            }

            entry->errorCount++;
        }

        if (entry->recvLogged < 60 && (hasData || callCount <= 8 || firstData))
        {
            entry->recvLogged++;
            shouldLog = true;
        }
        else if (callCount > 8 && (callCount % 5000) == 0 && g_quietMode == 0)
        {
            shouldLog = true;
        }

        // 重点关注 socket：收包全记（有预算上限，防止刷爆日志）。
        if (entry->focus && ConsumeFocusBudget())
        {
            shouldLog = true;
        }
    }
    else
    {
        shouldLog = true;
    }

    LeaveCriticalSection(&g_socketLock);

    if (!shouldLog)
    {
        if (firstSeen)
        {
            NoteSocketFirstSeen(handle, apiName);
        }

        return;
    }

    char fromPart[80] = { 0 };

    if (result == 0)
    {
        sprintf_s(fromPart, sizeof(fromPart), "from=%-22s ", fromText);
    }

    char socketLabel[40] = { 0 };
    FormatSocketLabel(handle, socketLabel, sizeof(socketLabel));

    Log("%s%-13s sock=%s result=%d errno=%d bytes=%lu %s%sdata=[%s] 由 %s 调用",
        hasData ? "★" : "",
        apiName,
        socketLabel,
        result,
        lastError,
        (unsigned long)received,
        fromPart,
        (overlapped || eventHandle != nullptr) ? "overlapped " : "",
        hex,
        caller);

    (void)overlappedStruct;

    if (firstSeen)
    {
        NoteSocketFirstSeen(handle, apiName);
    }
}

static int WSAAPI Hook_WSARecvFrom(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
    LPDWORD flags, struct sockaddr* from, LPINT fromLength, LPWSAOVERLAPPED overlapped,
    LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    REQUIRE_ORIGINAL(original_WSARecvFrom, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    int result = original_WSARecvFrom(socket, buffers, bufferCount, bytesReceived, flags, from, fromLength, overlapped, completion);

    InterlockedIncrement(&g_recvFromCount);

    if (overlapped != nullptr)
    {
        InterlockedIncrement(&g_overlappedRecvCount);
    }
    else
    {
        InterlockedIncrement(&g_blockingRecvCount);
    }

    DWORD received = (result == 0 && bytesReceived != nullptr) ? *bytesReceived : 0;

    if (received > 0)
    {
        InterlockedIncrement(&g_dataRecvCount);
    }

    const char* data = (bufferCount > 0 && buffers != nullptr) ? buffers[0].buf : nullptr;
    int dataLength = (bufferCount > 0 && buffers != nullptr) ? (int)buffers[0].len : 0;

    RecordReceive((unsigned long long)socket, caller, "WSARecvFrom", result, received, from,
        data, received > 0 ? (int)received : 0, overlapped != nullptr, overlapped,
        overlapped != nullptr ? overlapped->hEvent : nullptr);

    (void)dataLength;
    return result;
}

static int WSAAPI Hook_recvfrom(SOCKET socket, char* data, int dataLength, int flags, struct sockaddr* from, int* fromLength)
{
    REQUIRE_ORIGINAL(original_recvfrom, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    int result = original_recvfrom(socket, data, dataLength, flags, from, fromLength);

    InterlockedIncrement(&g_recvFromCount);
    InterlockedIncrement(&g_blockingRecvCount);

    if (result > 0)
    {
        InterlockedIncrement(&g_dataRecvCount);
    }

    RecordReceive((unsigned long long)socket, caller, "recvfrom", result, (DWORD)(result > 0 ? result : 0),
        from, data, result > 0 ? result : 0, false, nullptr, nullptr);

    return result;
}

static int WSAAPI Hook_WSARecv(SOCKET socket, LPWSABUF buffers, DWORD bufferCount, LPDWORD bytesReceived,
    LPDWORD flags, LPWSAOVERLAPPED overlapped, LPWSAOVERLAPPED_COMPLETION_ROUTINE completion)
{
    REQUIRE_ORIGINAL(original_WSARecv, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    int result = original_WSARecv(socket, buffers, bufferCount, bytesReceived, flags, overlapped, completion);

    InterlockedIncrement(&g_recvFromCount);

    if (overlapped != nullptr)
    {
        InterlockedIncrement(&g_overlappedRecvCount);
    }
    else
    {
        InterlockedIncrement(&g_blockingRecvCount);
    }

    DWORD received = (result == 0 && bytesReceived != nullptr) ? *bytesReceived : 0;

    if (received > 0)
    {
        InterlockedIncrement(&g_dataRecvCount);
    }

    const char* data = (bufferCount > 0 && buffers != nullptr) ? buffers[0].buf : nullptr;

    RecordReceive((unsigned long long)socket, caller, "WSARecv", result, received, nullptr,
        data, received > 0 ? (int)received : 0, overlapped != nullptr, overlapped,
        overlapped != nullptr ? overlapped->hEvent : nullptr);

    return result;
}

static int WSAAPI Hook_recv(SOCKET socket, char* data, int dataLength, int flags)
{
    REQUIRE_ORIGINAL(original_recv, SOCKET_ERROR);

    char caller[96] = { 0 };
    FormatCaller(caller, sizeof(caller), _ReturnAddress());

    int result = original_recv(socket, data, dataLength, flags);

    InterlockedIncrement(&g_recvFromCount);
    InterlockedIncrement(&g_blockingRecvCount);

    if (result > 0)
    {
        InterlockedIncrement(&g_dataRecvCount);
    }

    RecordReceive((unsigned long long)socket, caller, "recv", result, (DWORD)(result > 0 ? result : 0),
        nullptr, data, result > 0 ? result : 0, false, nullptr, nullptr);

    return result;
}

// ---------------------------------------------------------------- Hook 实现（等待可读）

static int WSAAPI Hook_select(int nfds, fd_set* readSet, fd_set* writeSet, fd_set* exceptSet, const struct timeval* timeout)
{
    REQUIRE_ORIGINAL(original_select, SOCKET_ERROR);

    InterlockedIncrement(&g_selectCount);

    int result = original_select(nfds, readSet, writeSet, exceptSet, timeout);

    char readSockets[220] = { 0 };
    char writeSockets[220] = { 0 };
    bool hasFocus = false;
    size_t readOffset = 0;
    size_t writeOffset = 0;

    if (readSet != nullptr)
    {
        for (u_int index = 0; index < readSet->fd_count && readOffset + 24 < sizeof(readSockets); ++index)
        {
            unsigned long long handle = (unsigned long long)readSet->fd_array[index];
            char label[40] = { 0 };
            FormatSocketLabel(handle, label, sizeof(label));
            hasFocus = hasFocus || IsSocketFocused(handle);
            readOffset += sprintf_s(readSockets + readOffset, sizeof(readSockets) - readOffset, "%s ", label);
        }
    }

    if (writeSet != nullptr)
    {
        for (u_int index = 0; index < writeSet->fd_count && writeOffset + 24 < sizeof(writeSockets); ++index)
        {
            unsigned long long handle = (unsigned long long)writeSet->fd_array[index];
            char label[40] = { 0 };
            FormatSocketLabel(handle, label, sizeof(label));
            writeOffset += sprintf_s(writeSockets + writeOffset, sizeof(writeSockets) - writeOffset, "%s ", label);
        }
    }

    if (g_selectCount <= 30 || (hasFocus && ConsumeFocusBudget()))
    {
        char timeoutText[32] = { 0 };

        if (timeout == nullptr)
        {
            strcpy_s(timeoutText, "blocking");
        }
        else
        {
            sprintf_s(timeoutText, "%ld.%06lds", (long)timeout->tv_sec, (long)timeout->tv_usec);
        }

        Log("select        nfds=%d read=%d write=%d except=%d timeout=%s result=%d readSockets=[%s] writeSockets=[%s]",
            nfds,
            readSet != nullptr ? (int)readSet->fd_count : -1,
            writeSet != nullptr ? (int)writeSet->fd_count : -1,
            exceptSet != nullptr ? (int)exceptSet->fd_count : -1,
            timeoutText, result, readSockets, writeSockets);
    }

    return result;
}

static int WSAAPI Hook_WSAPoll(LPWSAPOLLFD descriptors, ULONG count, INT timeoutMilliseconds)
{
    REQUIRE_ORIGINAL(original_WSAPoll, SOCKET_ERROR);

    InterlockedIncrement(&g_pollCount);

    int result = original_WSAPoll(descriptors, count, timeoutMilliseconds);

    char sockets[220] = { 0 };
    size_t offset = 0;
    bool hasFocus = false;

    if (descriptors != nullptr)
    {
        for (ULONG index = 0; index < count && offset + 32 < sizeof(sockets); ++index)
        {
            unsigned long long handle = (unsigned long long)descriptors[index].fd;
            char label[40] = { 0 };
            FormatSocketLabel(handle, label, sizeof(label));
            hasFocus = hasFocus || IsSocketFocused(handle);
            offset += sprintf_s(sockets + offset, sizeof(sockets) - offset, "%s(ev=0x%X re=0x%X) ",
                label, (unsigned)descriptors[index].events, (unsigned)descriptors[index].revents);
        }
    }

    if (g_pollCount <= 30 || (hasFocus && ConsumeFocusBudget()))
    {
        Log("WSAPoll       count=%lu timeout=%d result=%d sockets=[%s]",
            (unsigned long)count, timeoutMilliseconds, result, sockets);
    }

    return result;
}

static int WSAAPI Hook_WSAEventSelect(SOCKET socket, WSAEVENT eventHandle, long networkEvents)
{
    REQUIRE_ORIGINAL(original_WSAEventSelect, SOCKET_ERROR);

    InterlockedIncrement(&g_eventSelectCount);

    Log("WSAEventSelect sock=%llu event=%p mask=0x%X", (unsigned long long)socket, eventHandle, (unsigned)networkEvents);

    return original_WSAEventSelect(socket, eventHandle, networkEvents);
}

static int WSAAPI Hook_WSAEnumNetworkEvents(SOCKET socket, WSAEVENT eventHandle, LPWSANETWORKEVENTS networkEvents)
{
    REQUIRE_ORIGINAL(original_WSAEnumNetworkEvents, SOCKET_ERROR);

    InterlockedIncrement(&g_enumEventsCount);

    int result = original_WSAEnumNetworkEvents(socket, eventHandle, networkEvents);

    if (result == 0 && networkEvents != nullptr && g_enumEventsCount <= 30)
    {
        Log("WSAEnumNetworkEvents sock=%llu events=0x%X", (unsigned long long)socket, (unsigned)networkEvents->lNetworkEvents);
    }

    return result;
}

static HANDLE WINAPI Hook_CreateIoCompletionPort(HANDLE fileHandle, HANDLE existingCompletionPort,
    ULONG_PTR completionKey, DWORD numberOfConcurrentThreads)
{
    REQUIRE_ORIGINAL(original_CreateIoCompletionPort, nullptr);

    InterlockedIncrement(&g_iocpCount);

    Log("CreateIoCompletionPort handle=%p (IOCP 被使用了 %ld 次)", fileHandle, (long)g_iocpCount);

    return original_CreateIoCompletionPort(fileHandle, existingCompletionPort, completionKey, numberOfConcurrentThreads);
}

static DWORD WSAAPI Hook_WSAWaitForMultipleEvents(DWORD eventCount, const WSAEVENT* events, BOOL waitAll,
    DWORD timeout, BOOL alertable)
{
    REQUIRE_ORIGINAL(original_WSAWaitForMultipleEvents, WSA_WAIT_FAILED);

    InterlockedIncrement(&g_waitEventCount);

    DWORD result = original_WSAWaitForMultipleEvents(eventCount, events, waitAll, timeout, alertable);

    if (g_waitEventCount <= 30)
    {
        Log("WSAWaitForMultipleEvents count=%lu waitAll=%d timeout=%lu result=%lu",
            (unsigned long)eventCount, (int)waitAll, (unsigned long)timeout, (unsigned long)result);
    }

    return result;
}

static BOOL WSAAPI Hook_WSAGetOverlappedResult(SOCKET socket, LPWSAOVERLAPPED overlapped,
    LPDWORD bytesTransferred, BOOL wait, LPDWORD flags)
{
    REQUIRE_ORIGINAL(original_WSAGetOverlappedResult, FALSE);

    InterlockedIncrement(&g_overlappedResultCount);

    BOOL result = original_WSAGetOverlappedResult(socket, overlapped, bytesTransferred, wait, flags);

    if (g_overlappedResultCount <= 20)
    {
        Log("WSAGetOverlappedResult sock=%llu wait=%d event=%p bytes=%lu",
            (unsigned long long)socket, (int)wait,
            overlapped != nullptr ? overlapped->hEvent : nullptr,
            (unsigned long)(bytesTransferred != nullptr ? *bytesTransferred : 0));
    }

    return result;
}

// ---------------------------------------------------------------- IAT Hook

static bool IsWs2Module(const char* moduleName)
{
    return _stricmp(moduleName, "WS2_32.dll") == 0
        || _stricmp(moduleName, "ws2_32.dll") == 0;
}

static bool IsKernelModule(const char* moduleName)
{
    return _stricmp(moduleName, "KERNEL32.dll") == 0
        || _stricmp(moduleName, "kernel32.dll") == 0
        || _stricmp(moduleName, "KERNELBASE.dll") == 0;
}

/// <summary>按序号导入的函数，去目标模块的导出表里把名字查回来。</summary>
static bool ResolveOrdinalExportName(HMODULE module, WORD ordinal, char* buffer, size_t length)
{
    if (module == nullptr)
    {
        return false;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return false;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    IMAGE_DATA_DIRECTORY directory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];

    if (directory.VirtualAddress == 0)
    {
        return false;
    }

    IMAGE_EXPORT_DIRECTORY* exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + directory.VirtualAddress);
    DWORD* names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);
    WORD* ordinals = reinterpret_cast<WORD*>(base + exports->AddressOfNameOrdinals);

    for (DWORD index = 0; index < exports->NumberOfNames; ++index)
    {
        if ((WORD)(ordinals[index] + exports->Base) == ordinal)
        {
            strcpy_s(buffer, length, reinterpret_cast<const char*>(base + names[index]));
            return true;
        }
    }

    return false;
}

/// <summary>向某个系统模块要一个「真正的」函数地址（跳过我们自己的 Hook）。</summary>
static void* ResolveRealExport(const char* moduleName, const char* functionName)
{
    HMODULE module = GetModuleHandleA(moduleName);

    if (module == nullptr)
    {
        return nullptr;
    }

    void* proc = (void*)GetProcAddress(module, functionName);

    for (int index = 0; index < kTargetCount; ++index)
    {
        if (proc == g_targets[index].hookFunction)
        {
            return nullptr;
        }
    }

    return proc;
}

/// <summary>把导入表里的一个槽位换成我们的 Hook；命中返回 true。</summary>
static bool TryHookSlot(void** slot, const char* functionName, const char* importedModule,
    const char* ownerModuleName, bool preferRealExport)
{
    for (int index = 0; index < kTargetCount; ++index)
    {
        const HookTarget& target = g_targets[index];

        if (strcmp(functionName, target.functionName) != 0)
        {
            continue;
        }

        void* current = *slot;

        if (current == target.hookFunction)
        {
            return false;
        }

        if (*target.originalFunction == nullptr)
        {
            void* real = preferRealExport ? ResolveRealExport(importedModule, functionName) : nullptr;
            *target.originalFunction = (real != nullptr) ? real : current;
        }

        DWORD oldProtect = 0;

        if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &oldProtect))
        {
            return false;
        }

        *slot = target.hookFunction;
        VirtualProtect(slot, sizeof(void*), oldProtect, &oldProtect);
        Log("已挂钩 %s!%s（模块 %s）", importedModule, functionName, ownerModuleName);
        return true;
    }

    return false;
}

static int HookModuleImports(HMODULE module, const char* moduleName)
{
    if (module == nullptr)
    {
        return 0;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return 0;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);

    if (ntHeaders->Signature != IMAGE_NT_SIGNATURE)
    {
        return 0;
    }

    IMAGE_DATA_DIRECTORY importDirectory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];

    if (importDirectory.VirtualAddress == 0)
    {
        return 0;
    }

    int patched = 0;
    IMAGE_IMPORT_DESCRIPTOR* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + importDirectory.VirtualAddress);

    for (; descriptor->Name != 0; ++descriptor)
    {
        const char* importedModule = reinterpret_cast<const char*>(base + descriptor->Name);
        bool isWs2 = IsWs2Module(importedModule);
        bool isKernel = IsKernelModule(importedModule);

        if (!isWs2 && !isKernel)
        {
            continue;
        }

        IMAGE_THUNK_DATA* originalThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->OriginalFirstThunk);
        IMAGE_THUNK_DATA* firstThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(base + descriptor->FirstThunk);

        if (descriptor->OriginalFirstThunk == 0)
        {
            originalThunk = firstThunk;
        }

        for (; originalThunk->u1.Function != 0; ++originalThunk, ++firstThunk)
        {
            char ordinalName[128] = { 0 };
            const char* functionName = nullptr;

            if ((originalThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) != 0)
            {
                // 按序号导入：先去目标模块的导出表把名字查回来（有些程序就是这么链 ws2_32 的）。
                WORD ordinal = (WORD)(originalThunk->u1.Ordinal & 0xFFFF);

                if (!ResolveOrdinalExportName(GetModuleHandleA(importedModule), ordinal, ordinalName, sizeof(ordinalName)))
                {
                    continue;
                }

                functionName = ordinalName;
            }
            else
            {
                IMAGE_IMPORT_BY_NAME* importByName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + originalThunk->u1.AddressOfData);
                functionName = reinterpret_cast<const char*>(importByName->Name);
            }

            if (TryHookSlot(reinterpret_cast<void**>(&firstThunk->u1.Function), functionName,
                importedModule, moduleName, false))
            {
                ++patched;
            }
        }
    }

    return patched;
}

/// <summary>
/// 延迟导入（/DELAYLOAD）的表在 IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT 里，普通导入表看不到，
/// 但延迟导入的槽位同样可以替换成我们的 Hook。
/// </summary>
static int HookModuleDelayImports(HMODULE module, const char* moduleName)
{
    if (module == nullptr)
    {
        return 0;
    }

    BYTE* base = reinterpret_cast<BYTE*>(module);
    IMAGE_DOS_HEADER* dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(base);

    if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return 0;
    }

    IMAGE_NT_HEADERS* ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dosHeader->e_lfanew);
    IMAGE_DATA_DIRECTORY directory = ntHeaders->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];

    if (directory.VirtualAddress == 0 || directory.Size == 0)
    {
        return 0;
    }

    int patched = 0;
    IMAGE_DELAYLOAD_DESCRIPTOR* descriptor = reinterpret_cast<IMAGE_DELAYLOAD_DESCRIPTOR*>(base + directory.VirtualAddress);

    for (; descriptor->DllNameRVA != 0; ++descriptor)
    {
        // Attributes 是个位域，直接按 DWORD 读第一个字段（dlattrRva = 0x1）。
        DWORD attributes = *reinterpret_cast<const DWORD*>(reinterpret_cast<const BYTE*>(descriptor));
        bool rvaBased = (attributes & 1) != 0;
        const char* importedModule = rvaBased
            ? reinterpret_cast<const char*>(base + descriptor->DllNameRVA)
            : reinterpret_cast<const char*>((uintptr_t)descriptor->DllNameRVA);

        if (importedModule == nullptr || (!IsWs2Module(importedModule) && !IsKernelModule(importedModule)))
        {
            continue;
        }

        BYTE* nameTable = rvaBased
            ? base + descriptor->ImportNameTableRVA
            : reinterpret_cast<BYTE*>((uintptr_t)descriptor->ImportNameTableRVA);
        BYTE* addressTable = rvaBased
            ? base + descriptor->ImportAddressTableRVA
            : reinterpret_cast<BYTE*>((uintptr_t)descriptor->ImportAddressTableRVA);

        if (nameTable == nullptr || addressTable == nullptr)
        {
            continue;
        }

        IMAGE_THUNK_DATA* nameThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(nameTable);
        IMAGE_THUNK_DATA* addressThunk = reinterpret_cast<IMAGE_THUNK_DATA*>(addressTable);

        for (; nameThunk->u1.Function != 0; ++nameThunk, ++addressThunk)
        {
            char ordinalName[128] = { 0 };
            const char* functionName = nullptr;

            if ((nameThunk->u1.Ordinal & IMAGE_ORDINAL_FLAG) != 0)
            {
                WORD ordinal = (WORD)(nameThunk->u1.Ordinal & 0xFFFF);

                if (!ResolveOrdinalExportName(GetModuleHandleA(importedModule), ordinal, ordinalName, sizeof(ordinalName)))
                {
                    continue;
                }

                functionName = ordinalName;
            }
            else
            {
                IMAGE_IMPORT_BY_NAME* importByName = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + nameThunk->u1.AddressOfData);
                functionName = reinterpret_cast<const char*>(importByName->Name);
            }

            // 延迟导入的槽位里放的是「延迟桩」而不是真函数，所以原始地址要另外解析。
            if (TryHookSlot(reinterpret_cast<void**>(&addressThunk->u1.Function), functionName,
                importedModule, moduleName, true))
            {
                ++patched;
            }
        }
    }

    return patched;
}

static void HookAllLoadedModules()
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, GetCurrentProcessId());

    if (snapshot == INVALID_HANDLE_VALUE)
    {
        return;
    }

    MODULEENTRY32W entry;
    entry.dwSize = sizeof(entry);

    if (Module32FirstW(snapshot, &entry))
    {
        do
        {
            char shortName[MAX_PATH] = { 0 };
            WideCharToMultiByte(CP_UTF8, 0, entry.szModule, -1, shortName, MAX_PATH, nullptr, nullptr);

            // 不碰自己：否则我们内部调用的 ws2_32 / GetProcAddress 会被自己的 Hook 拦下来，容易转圈。
            if (reinterpret_cast<HMODULE>(entry.hModule) == g_selfModule)
            {
                continue;
            }

            // 挂所有非系统模块（游戏是分模块的：exe / GameCore / SDL2 各有一套导入表）。
            if (_strnicmp(shortName, "kernel32", 8) == 0
                || _strnicmp(shortName, "kernelbase", 10) == 0
                || _strnicmp(shortName, "ntdll", 5) == 0
                || _strnicmp(shortName, "ws2_32", 6) == 0
                || _strnicmp(shortName, "user32", 6) == 0
                || _strnicmp(shortName, "advapi32", 8) == 0
                || _strnicmp(shortName, "ucrtbase", 8) == 0
                || _strnicmp(shortName, "msvcrt", 6) == 0
                || _strnicmp(shortName, "vcruntime", 9) == 0
                || _strnicmp(shortName, "msvcp", 5) == 0)
            {
                continue;
            }

            HMODULE loadedModule = reinterpret_cast<HMODULE>(entry.hModule);
            HookModuleImports(loadedModule, shortName);
            HookModuleDelayImports(loadedModule, shortName);
        } while (Module32NextW(snapshot, &entry));
    }

    CloseHandle(snapshot);
}

// ------------------------------------------------- 动态解析（GetProcAddress）拦截
//
// 很多代码不是靠导入表调用 Winsock，而是运行时 GetProcAddress("sendto") 拿指针
// 再调用 —— 这种写法 IAT Hook 抓不到。
//
// 早期做法是把 ws2_32 导出表里的「函数地址」当成函数指针去改，结果是直接覆盖了
// 真实函数开头的机器码（导出表里存的是 RVA，不是指针），会把宿主进程写崩。
// 现在改成：Hook GetProcAddress 本身 —— 谁想解析我们要盯的函数，我们就把自己的
// 实现返回给它。全程不改任何代码字节。

static volatile LONG g_dynamicResolveCount = 0;

// 已经确认「不是我们要盯的模块」的小缓存：避免每次 GetProcAddress 都去查文件名。
static const int kMaxOtherModules = 48;
static HMODULE g_otherModules[kMaxOtherModules];
static volatile LONG g_otherModuleCount = 0;

static bool IsKnownOtherModule(HMODULE module)
{
    LONG count = InterlockedCompareExchange(&g_otherModuleCount, 0, 0);

    for (LONG index = 0; index < count && index < kMaxOtherModules; ++index)
    {
        if (g_otherModules[index] == module)
        {
            return true;
        }
    }

    return false;
}

static void RememberOtherModule(HMODULE module)
{
    LONG index = InterlockedIncrement(&g_otherModuleCount) - 1;

    if (index < kMaxOtherModules)
    {
        g_otherModules[index] = module;
    }
}

/// <summary>这个模块句柄是不是我们要盯的 ws2_32 / kernel32 / kernelbase。</summary>
static bool IsInterceptionModule(HMODULE module)
{
    if (module == nullptr)
    {
        return false;
    }

    if (module == g_ws2Module || module == g_win32Module || module == g_kernelBaseModule)
    {
        return true;
    }

    if (IsKnownOtherModule(module))
    {
        return false;
    }

    char path[MAX_PATH] = { 0 };

    if (GetModuleFileNameA(module, path, MAX_PATH) == 0)
    {
        return false;
    }

    const char* name = strrchr(path, '\\');
    name = (name != nullptr) ? name + 1 : path;

    if (_stricmp(name, "ws2_32.dll") == 0)
    {
        g_ws2Module = module;
        return true;
    }

    if (_stricmp(name, "kernel32.dll") == 0)
    {
        g_win32Module = module;
        return true;
    }

    if (_stricmp(name, "kernelbase.dll") == 0)
    {
        g_kernelBaseModule = module;
        return true;
    }

    RememberOtherModule(module);
    return false;
}

static FARPROC WINAPI Hook_GetProcAddress(HMODULE module, LPCSTR name)
{
    static __declspec(thread) int inHook = 0;

    if (inHook != 0 || original_GetProcAddress == nullptr)
    {
        return original_GetProcAddress != nullptr ? original_GetProcAddress(module, name) : nullptr;
    }

    FARPROC real = original_GetProcAddress(module, name);

    if (real == nullptr || name == nullptr || HIWORD((DWORD_PTR)name) == 0)
    {
        return real;   // 失败、空名字、按序号导入 —— 都不管
    }

    if (!IsInterceptionModule(module))
    {
        return real;
    }

    for (int index = 0; index < kTargetCount; ++index)
    {
        const HookTarget& target = g_targets[index];

        if (strcmp(target.functionName, "GetProcAddress") == 0 || strcmp(name, target.functionName) != 0)
        {
            continue;
        }

        if (*target.originalFunction == nullptr)
        {
            *target.originalFunction = (void*)real;
        }

        if ((void*)real == target.hookFunction)
        {
            return real;
        }

        if (InterlockedIncrement(&g_dynamicResolveCount) <= 200)
        {
            inHook = 1;
            char caller[96] = { 0 };
            FormatCaller(caller, sizeof(caller), _ReturnAddress());
            Log("已挂钩（动态解析）%s（%s 解析，真实地址 %p）", name, caller, (void*)real);
            inHook = 0;
        }

        return (FARPROC)target.hookFunction;
    }

    return real;
}

// ---------------------------------------------------------------- 摘要

/// <summary>把每个原始函数指针的值记下来：如果哪个是 0，说明这个 Hook 是「哑」的，得先修它。</summary>
static void LogOriginalPointers()
{
    Log("原始函数指针：socket=%p WSASocketW=%p bind=%p connect=%p listen=%p getsockname=%p setsockopt=%p closesocket=%p",
        (void*)original_socket, (void*)original_WSASocketW, (void*)original_bind, (void*)original_connect,
        (void*)original_listen, (void*)original_getsockname, (void*)original_setsockopt, (void*)original_closesocket);

    Log("原始函数指针：sendto=%p WSASendTo=%p send=%p WSASend=%p recvfrom=%p WSARecvFrom=%p recv=%p WSARecv=%p",
        (void*)original_sendto, (void*)original_WSASendTo, (void*)original_send, (void*)original_WSASend,
        (void*)original_recvfrom, (void*)original_WSARecvFrom, (void*)original_recv, (void*)original_WSARecv);

    Log("原始函数指针：select=%p WSAPoll=%p WSAEventSelect=%p WSAEnumNetworkEvents=%p WSAWaitForMultipleEvents=%p WSAGetOverlappedResult=%p IOCP=%p",
        (void*)original_select, (void*)original_WSAPoll, (void*)original_WSAEventSelect, (void*)original_WSAEnumNetworkEvents,
        (void*)original_WSAWaitForMultipleEvents, (void*)original_WSAGetOverlappedResult, (void*)original_CreateIoCompletionPort);
}

static void WriteSummary()
{
    char path[MAX_PATH] = { 0 };
    strcpy_s(path, g_dllPath);

    char* slash = strrchr(path, '\\');
    if (slash != nullptr)
    {
        *(slash + 1) = '\0';
    }

    strcat_s(path, "civ6-hook-diag-summary.txt");

    FILE* file = _fsopen(path, "w", _SH_DENYNO);
    if (file == nullptr)
    {
        return;
    }

    SYSTEMTIME time;
    GetLocalTime(&time);

    fprintf(file, "civ6hook-diag v2 摘要  %04d-%02d-%02d %02d:%02d:%02d  pid=%lu\n",
        time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
        (unsigned long)GetCurrentProcessId());

    fprintf(file, "\n【结论速览】\n");
    fprintf(file, "  抓到广播/局域网发包：%ld 次（其中目标端口在 62900-62999 的：%ld 次）\n",
        (long)g_broadcastSendCount, (long)g_lanPortSendCount);
    fprintf(file, "  收到过数据的收包次数：%ld 次\n", (long)g_dataRecvCount);

    fprintf(file, "\n【调用统计】\n");
    fprintf(file, "  sendto/WSASendTo/send：%ld    WSASend：%ld\n", (long)g_sendToCount, (long)g_wsSendCount);
    fprintf(file, "  recvfrom/WSARecvFrom/recv/WSARecv：%ld（阻塞式 %ld，重叠式 %ld）\n",
        (long)g_recvFromCount, (long)g_blockingRecvCount, (long)g_overlappedRecvCount);
    fprintf(file, "  socket 创建：%ld    bind：%ld    connect：%ld\n",
        (long)g_socketCreateCount, (long)g_bindCount, (long)g_connectCount);
    fprintf(file, "  select=%ld  WSAPoll=%ld  WSAEventSelect=%ld  WSAEnumNetworkEvents=%ld  CreateIoCompletionPort=%ld\n",
        (long)g_selectCount, (long)g_pollCount, (long)g_eventSelectCount, (long)g_enumEventsCount, (long)g_iocpCount);
    fprintf(file, "  WSAWaitForMultipleEvents=%ld  WSAGetOverlappedResult=%ld\n",
        (long)g_waitEventCount, (long)g_overlappedResultCount);

    fprintf(file, "\n【socket 明细】（哪些 socket 在收发、绑定端口、由哪个模块创建）\n");

    EnterCriticalSection(&g_socketLock);

    for (int index = 0; index < kMaxSockets; ++index)
    {
        SocketStat& entry = g_sockets[index];

        if (entry.handle == 0)
        {
            continue;
        }

        fprintf(file, "  sock=%-6llu 创建者=%-28s 绑定端口=%-6s%s 广播选项=%s\n",
            entry.handle, entry.creator,
            entry.boundKnown ? std::to_string((int)entry.boundPort).c_str() : "?",
            entry.lanPort ? "（★62900-62999）" : "",
            entry.broadcastOption < 0 ? "未设置" : (entry.broadcastOption == 0 ? "关闭" : "打开"));

        fprintf(file, "        发送 %ld 次/%ld 字节  接收 %ld 次/%ld 字节（有效数据 %ld 次）错误 %ld 次（最后 errno=%d）\n",
            entry.sendCalls, entry.sendBytes, entry.recvCalls, entry.recvBytes,
            entry.recvDataCount, entry.errorCount, entry.lastError);

        if (entry.closed)
        {
            fprintf(file, "        （这个 socket 已经关闭）\n");
        }

        if (entry.lastSendTo[0] != '\0')
        {
            fprintf(file, "        最后发送到：%s  payload=[%s]\n", entry.lastSendTo, entry.lastPayload);
        }

        if (entry.sawData)
        {
            fprintf(file, "        首次收到数据来自：%s  payload=[%s]\n", entry.firstDataFrom, entry.firstDataPayload);
            fprintf(file, "        最后收到数据来自：%s  payload=[%s]\n", entry.lastFrom, entry.lastPayload);
        }
    }

    LeaveCriticalSection(&g_socketLock);

    fprintf(file, "\n【发送目标统计】（目标 IP:端口 → 次数 / 总字节，附调用模块）\n");

    EnterCriticalSection(&g_targetLock);

    if (g_sendTargetCount == 0)
    {
        fprintf(file, "  （没有抓到任何发送）\n");
    }

    for (int index = 0; index < g_sendTargetCount; ++index)
    {
        fprintf(file, "  %-26s %-30s %-8ld %ld\n",
            g_sendTargets[index].text, g_sendTargets[index].caller,
            g_sendTargets[index].count, g_sendTargets[index].bytes);
    }

    LeaveCriticalSection(&g_targetLock);

    fclose(file);
}

// ------------------------------------------------- 进程端口快照（完全不依赖 Hook）
//
// Hook 有可能漏掉某些调用路径（延迟导入、序号导入、别的模块代劳……）。
// 但系统自己的端口表不会漏：直接问 Windows「本进程绑了哪些端口」，
// 就能确凿地知道游戏有没有开 62900-62999 的局域网扫描端口。

static char g_lastEndpointText[640] = { 0 };

static void AppendEndpointText(char* buffer, size_t length, const char* text)
{
    size_t used = strlen(buffer);
    size_t extra = strlen(text);

    if (used + extra + 3 >= length)
    {
        return;
    }

    if (used > 0)
    {
        strcat_s(buffer, length, "；");
    }

    strcat_s(buffer, length, text);
}

static void LogProcessEndpoints()
{
    char text[640] = { 0 };
    DWORD pid = GetCurrentProcessId();
    DWORD size = 0;

    if (GetExtendedUdpTable(nullptr, &size, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0) == ERROR_INSUFFICIENT_BUFFER && size > 0)
    {
        std::vector<BYTE> buffer(size);

        if (GetExtendedUdpTable(buffer.data(), &size, FALSE, AF_INET, UDP_TABLE_OWNER_PID, 0) == NO_ERROR)
        {
            MIB_UDPTABLE_OWNER_PID* table = reinterpret_cast<MIB_UDPTABLE_OWNER_PID*>(buffer.data());

            for (DWORD index = 0; index < table->dwNumEntries; ++index)
            {
                if (table->table[index].dwOwningPid != pid)
                {
                    continue;
                }

                unsigned short port = ntohs((u_short)table->table[index].dwLocalPort);
                struct in_addr address;
                address.s_addr = table->table[index].dwLocalAddr;
                char ip[INET_ADDRSTRLEN] = { 0 };
                inet_ntop(AF_INET, &address, ip, sizeof(ip));

                char item[96] = { 0 };
                sprintf_s(item, "UDP %s:%u%s", ip, (unsigned)port, IsLanProbePort(port) ? "★" : "");
                AppendEndpointText(text, sizeof(text), item);
            }
        }
    }

    size = 0;

    if (GetExtendedTcpTable(nullptr, &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == ERROR_INSUFFICIENT_BUFFER && size > 0)
    {
        std::vector<BYTE> buffer(size);

        if (GetExtendedTcpTable(buffer.data(), &size, FALSE, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) == NO_ERROR)
        {
            MIB_TCPTABLE_OWNER_PID* table = reinterpret_cast<MIB_TCPTABLE_OWNER_PID*>(buffer.data());

            for (DWORD index = 0; index < table->dwNumEntries; ++index)
            {
                if (table->table[index].dwOwningPid != pid)
                {
                    continue;
                }

                unsigned short port = ntohs((u_short)table->table[index].dwLocalPort);
                struct in_addr address;
                address.s_addr = table->table[index].dwLocalAddr;
                char ip[INET_ADDRSTRLEN] = { 0 };
                inet_ntop(AF_INET, &address, ip, sizeof(ip));

                char item[112] = { 0 };
                sprintf_s(item, "TCP %s:%u(状态 %lu)%s", ip, (unsigned)port,
                    (unsigned long)table->table[index].dwState, IsLanProbePort(port) ? "★" : "");
                AppendEndpointText(text, sizeof(text), item);
            }
        }
    }

    if (text[0] == '\0')
    {
        strcpy_s(text, "（本进程没有已绑定的 UDP/TCP 端口）");
    }

    if (strcmp(text, g_lastEndpointText) == 0)
    {
        return;
    }

    strcpy_s(g_lastEndpointText, text);
    Log("本进程端口快照：%s", text);
}

// ---------------------------------------------------------------- 启动线程

static DWORD WINAPI StartupThread(LPVOID parameter)
{
    (void)parameter;

    OpenLog();

    Log("=== civ6hook-diag v2 已加载（只记录，不转发）===");
    Log("进程 PID：%lu", (unsigned long)GetCurrentProcessId());
    Log("DLL 路径：%s", g_dllPath);
    Log("日志文件：%s | %s", g_kernelLogPath, g_userLogPath);

    RefreshModuleTable();

    // 尽量早挂上：启动器自动注入时游戏刚起，这段窗口里发出的广播才不会漏。
    Sleep(300);
    HookAllLoadedModules();
    LogProcessEndpoints();
    LogOriginalPointers();

    Sleep(1200);
    HookAllLoadedModules();
    RefreshModuleTable();
    Log("首轮挂钩完成。");
    WriteSummary();   // 尽早先落一份摘要：哪怕宿主马上退出也能看到数据

    // 之后每 3 秒补挂一次（有些模块是延迟加载的）。
    for (int round = 0; round < 60; ++round)
    {
        Sleep(3000);
        HookAllLoadedModules();
        RefreshModuleTable();
        LogProcessEndpoints();

        // 每轮都刷新一次摘要文件：就算游戏被强杀，我们也能拿到一份接近实时的数据。
        WriteSummary();
    }

    Log("统计：sendto/WSASendTo/send=%ld，WSASend=%ld，recv 类=%ld（阻塞 %ld / 重叠 %ld），收到数据 %ld 次，广播发送 %ld 次",
        (long)g_sendToCount, (long)g_wsSendCount, (long)g_recvFromCount,
        (long)g_blockingRecvCount, (long)g_overlappedRecvCount,
        (long)g_dataRecvCount, (long)g_broadcastSendCount);

    WriteSummary();

    for (FILE* log : g_logs)
    {
        fclose(log);
    }

    g_logs.clear();
    return 0;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_selfModule = module;
        InitializeCriticalSection(&g_logLock);
        InitializeCriticalSection(&g_targetLock);
        InitializeCriticalSection(&g_moduleLock);
        InitializeCriticalSection(&g_socketLock);

        for (int index = 0; index < kMaxSockets; ++index)
        {
            g_sockets[index].broadcastOption = -1;
        }

        char flag[16] = { 0 };
        if (GetEnvironmentVariableA("KIRIYAMA_DIAG_QUIET", flag, sizeof(flag)) > 0 && flag[0] == '1')
        {
            InterlockedExchange(&g_quietMode, 1);
        }

        GetModuleFileNameA(module, g_dllPath, MAX_PATH);

        // 一进来就落盘：这样即使后面的线程/挂钩出问题，也能证明「DLL 被加载过」。
        OpenLog();
        Log("=== DllMain: DLL_PROCESS_ATTACH，进程 PID=%lu ===", (unsigned long)GetCurrentProcessId());
        Log("DLL：%s", g_dllPath);

        // 再放一个标记文件，方便一眼看出注入是否成功。
        char markerPath[MAX_PATH] = { 0 };
        strcpy_s(markerPath, g_dllPath);
        char* slash = strrchr(markerPath, '\\');
        if (slash != nullptr)
        {
            *(slash + 1) = '\0';
        }
        strcat_s(markerPath, "civ6hook-diag-loaded.txt");

        FILE* marker = _fsopen(markerPath, "a", _SH_DENYNO);
        if (marker != nullptr)
        {
            SYSTEMTIME time;
            GetLocalTime(&time);
            fprintf(marker, "[%04d-%02d-%02d %02d:%02d:%02d] PID=%lu 已加载 %s\n",
                time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond,
                (unsigned long)GetCurrentProcessId(), g_dllPath);
            fclose(marker);
        }

        HANDLE thread = CreateThread(nullptr, 0, StartupThread, nullptr, 0, nullptr);
        if (thread != nullptr)
        {
            CloseHandle(thread);
        }
    }

    return TRUE;
}
