#include "framework/dll_context.h"

volatile LONG g_enabled = 1;
volatile LONG g_dryRun = 0;
volatile LONG g_verbose = 0;

char g_dllPath[MAX_PATH] = { 0 };
HMODULE g_selfModule = nullptr;

void DllContextInit(HMODULE module)
{
    g_selfModule = module;
    GetModuleFileNameA(module, g_dllPath, MAX_PATH);
}
