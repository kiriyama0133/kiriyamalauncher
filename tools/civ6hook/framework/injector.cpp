// ============================================================================
//  挂载器：让游戏"调用到我们这里"。
//
//  为什么要三种方式都上：
//    · 普通导入表（IAT） —— 最常见，游戏启动时静态导入的 WS2_32 函数都在这；
//    · 延迟导入          —— 有些模块是"第一次用到才解析"，IAT 里还是桩；
//    · 序号导入          —— 有些模块按序号而不是名字导入，得先反查名字才能比对；
//    · GetProcAddress    —— 运行时动态解析的，直接在我们的替身上返回自己的实现。
//
//  额外还做了一件关键的事：**反复挂**（StartupThread 里循环调用）。
//  游戏会在运行过程中陆续加载新的 DLL（比如 Steam 叠加层、音频后端），
//  它们启动时不在快照里，只有隔一会儿重扫才能覆盖到。
// ============================================================================

#include "framework/injector.h"

#include <tlhelp32.h>
#include <string.h>

#include "framework/dll_context.h"
#include "framework/hook_functions.h"
#include "framework/logging.h"
#include "framework/winsock_api.h"

struct HookTarget
{
    const char* functionName;
    void* hookFunction;
    void** originalFunction;
};

static const HookTarget g_targets[] =
{
    { "socket",       (void*)Hook_socket,       (void**)&original_socket },
    { "WSASocketW",   (void*)Hook_WSASocketW,   (void**)&original_WSASocketW },
    { "bind",         (void*)Hook_bind,         (void**)&original_bind },
    { "connect",      (void*)Hook_connect,      (void**)&original_connect },
    { "getsockname",  (void*)Hook_getsockname,  (void**)&original_getsockname },
    { "closesocket",  (void*)Hook_closesocket,  (void**)&original_closesocket },
    { "sendto",       (void*)Hook_sendto,       (void**)&original_sendto },
    { "WSASendTo",    (void*)Hook_WSASendTo,    (void**)&original_WSASendTo },
    { "send",         (void*)Hook_send,         (void**)&original_send },
    { "WSASend",      (void*)Hook_WSASend,      (void**)&original_WSASend },
    { "recvfrom",     (void*)Hook_recvfrom,     (void**)&original_recvfrom },
    { "WSARecvFrom",  (void*)Hook_WSARecvFrom,  (void**)&original_WSARecvFrom },
    { "recv",         (void*)Hook_recv,         (void**)&original_recv },
    { "WSARecv",      (void*)Hook_WSARecv,      (void**)&original_WSARecv },
    { "select",       (void*)Hook_select,       (void**)&original_select },
    { "WSAPoll",      (void*)Hook_WSAPoll,      (void**)&original_WSAPoll },
    { "GetProcAddress", (void*)Hook_GetProcAddress, (void**)&original_GetProcAddress }
};

static const int kTargetCount = (int)(sizeof(g_targets) / sizeof(g_targets[0]));

static bool IsWs2Module(const char* moduleName)
{
    return _stricmp(moduleName, "WS2_32.dll") == 0;
}

static bool IsKernelModule(const char* moduleName)
{
    return _stricmp(moduleName, "KERNEL32.dll") == 0
        || _stricmp(moduleName, "KERNELBASE.dll") == 0;
}

/// <summary>按序号反查导出名（有些模块按序号导入）。</summary>
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

/// <summary>
/// 直接从模块自己的导出表里取"真正的那个函数"。
/// 用来避免一种死循环：IAT 里已经被我们改过，再去读就拿到自己的 Hook 了。
/// </summary>
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
        LogDebug("已挂钩 %s!%s（模块 %s）", importedModule, functionName, ownerModuleName);
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

        if (!IsWs2Module(importedModule) && !IsKernelModule(importedModule))
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

            if (TryHookSlot(reinterpret_cast<void**>(&addressThunk->u1.Function), functionName,
                importedModule, moduleName, true))
            {
                ++patched;
            }
        }
    }

    return patched;
}

void InstallHooksIntoAllModules()
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
            if (reinterpret_cast<HMODULE>(entry.hModule) == g_selfModule)
            {
                continue;
            }

            char shortName[MAX_PATH] = { 0 };
            WideCharToMultiByte(CP_UTF8, 0, entry.szModule, -1, shortName, MAX_PATH, nullptr, nullptr);

            if (_strnicmp(shortName, "kernel32", 8) == 0
                || _strnicmp(shortName, "kernelbase", 10) == 0
                || _strnicmp(shortName, "ntdll", 5) == 0
                || _strnicmp(shortName, "ws2_32", 6) == 0)
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

// GetProcAddress 拦截：凡是运行时解析我们要看的函数，就把我们自己的实现返回给它。
FARPROC WINAPI Hook_GetProcAddress(HMODULE module, LPCSTR name)
{
    static __declspec(thread) int inHook = 0;

    if (original_GetProcAddress == nullptr)
    {
        return nullptr;
    }

    if (inHook != 0)
    {
        return original_GetProcAddress(module, name);
    }

    FARPROC real = original_GetProcAddress(module, name);

    if (real == nullptr || name == nullptr || HIWORD((DWORD_PTR)name) == 0 || module == nullptr)
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

        inHook = 1;
        LogDebug("动态解析 %s → 交给 Hook。", name);
        inHook = 0;
        return (FARPROC)target.hookFunction;
    }

    return real;
}
