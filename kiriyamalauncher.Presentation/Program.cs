using Avalonia;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace kiriyamalauncher.Presentation;

internal sealed class Program
{
    /// <summary>单实例守卫用的互斥体名（Local\ = 只在当前登录会话里唯一）。</summary>
    private const string InstanceGuardName = @"Local\kiriyamalauncher.single-instance";

    private const int SW_RESTORE = 9;

    // 必须一直持有到进程结束：一旦被 GC 回收，句柄关闭就等于"没人在占"，守卫就失效了。
    private static Mutex? _instanceGuard;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 同一个会话里只允许一个实例在跑。
        //
        // 为什么必须这么做：「游戏隧道」是一条命名管道，而且只有 1 个服务端实例
        // （游戏里的 Hook 当客户端，启动器当服务端）。同时开两个启动器时：
        //   1) 后开的那个建不出管道（日志里会看到「所有的管道范例都在使用中」）；
        //   2) 游戏里的 Hook 会连上**先开的那个**实例；
        //   3) 先开的那个如果已经离开过联机页，它的设备扫描是停的、对端列表是空的，
        //      于是游戏发出去的广播全被丢弃 —— 表现就是「怎么刷新都看不到房间」。
        if (!TryAcquireInstanceGuard())
        {
            ActivateExistingInstance();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static bool TryAcquireInstanceGuard()
    {
        try
        {
            Mutex guard = new(initiallyOwned: true, InstanceGuardName, out bool createdNew);

            if (createdNew)
            {
                _instanceGuard = guard;
                return true;
            }

            guard.Dispose();
            return false;
        }
        catch (Exception)
        {
            // 互斥体本身出问题（极少见）时不要因此拦住用户，照常启动。
            return true;
        }
    }

    /// <summary>
    /// 已经有实例在跑：把它的窗口叫到前面来。
    /// 否则用户双击之后「什么都没发生」，会以为程序坏了。
    /// </summary>
    private static void ActivateExistingInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Process[] candidates;

        try
        {
            using Process current = Process.GetCurrentProcess();
            candidates = Process.GetProcessesByName(current.ProcessName);
        }
        catch (Exception)
        {
            return;
        }

        try
        {
            foreach (Process candidate in candidates)
            {
                try
                {
                    IntPtr window = candidate.MainWindowHandle;

                    if (window == IntPtr.Zero)
                    {
                        continue;
                    }

                    ShowWindowAsync(window, SW_RESTORE);
                    SetForegroundWindow(window);
                    return;
                }
                catch (Exception)
                {
                    // 单个进程读不到就算了，继续试下一个。
                }
            }
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
