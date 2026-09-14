#include "framework/logging.h"

#include <share.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <vector>

#include "framework/dll_context.h"

static CRITICAL_SECTION g_logLock;
static std::vector<FILE*> g_logs;

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

void Log(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    LogV(format, args);
    va_end(args);
}

void LogDebug(const char* format, ...)
{
    if (g_verbose == 0)
    {
        return;
    }

    va_list args;
    va_start(args, format);
    LogV(format, args);
    va_end(args);
}

void LogInitialize()
{
    InitializeCriticalSection(&g_logLock);
}

void LogOpen()
{
    if (!g_logs.empty())
    {
        return;
    }

    SYSTEMTIME time;
    GetLocalTime(&time);

    char directory[MAX_PATH] = { 0 };
    strcpy_s(directory, g_dllPath);
    char* slash = strrchr(directory, '\\');
    if (slash != nullptr)
    {
        *slash = '\0';
    }

    char path[MAX_PATH] = { 0 };
    sprintf_s(path, "%s\\civ6hook-%04d%02d%02d-%02d%02d%02d.log",
        directory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    FILE* file = _fsopen(path, "a", _SH_DENYNO);
    if (file != nullptr)
    {
        setvbuf(file, nullptr, _IONBF, 0);
        g_logs.push_back(file);
    }

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

    sprintf_s(path, "%s\\civ6hook-%04d%02d%02d-%02d%02d%02d.log",
        userDirectory, time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond);

    file = _fsopen(path, "a", _SH_DENYNO);
    if (file != nullptr)
    {
        setvbuf(file, nullptr, _IONBF, 0);
        g_logs.push_back(file);
    }
}
