using System;
using System.Runtime.InteropServices;

namespace kiriyamalauncher.Data;

/// <summary>
/// 判断当前进程是否以管理员身份运行。
///
/// 注入游戏进程需要与游戏权限对等：如果游戏是管理员启动的、而本程序不是，
/// OpenProcess / WriteProcessMemory 会直接失败，界面上要提前提示用户。
/// </summary>
public static class ProcessElevation
{
    private const int TOKEN_QUERY = 0x0008;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation</summary>
    private const int TOKEN_ELEVATION = 20;

    /// <summary>当前进程是否是提升（管理员）权限。</summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
            {
                return false;
            }

            int size = sizeof(int);
            buffer = Marshal.AllocHGlobal(size);

            if (!GetTokenInformation(token, TOKEN_ELEVATION, buffer, size, out _))
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
