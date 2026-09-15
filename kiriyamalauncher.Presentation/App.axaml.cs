using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Business;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services;
using kiriyamalauncher.Presentation.Base.Services.Dialogs;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using kiriyamalauncher.Presentation.Base.Services.Preferences;
using kiriyamalauncher.Presentation.ViewModels;
using kiriyamalauncher.Presentation.Views;
using SukiUI.Toasts;
using SukiUI.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Modules.ComponentModel;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.MemorySink;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation;

public partial class App : Application
{
    private static readonly object LOGGING_LOCK = new();

    private static ILogSource<LogEvent>? _logSource;

    public override void Initialize()
    {
        // 日志要尽早配好：崩溃处理里要用它写文件。
        ConfigureLogging();

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup += OnDesktopStartup;
            desktop.Exit += OnDesktopExit;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopStartup(object? sender, ControlledApplicationLifetimeStartupEventArgs e)
    {
        // Services need to be gathered after app initialization but before
        // the MainWindow is created as that starts the chain of dependency injections.

        IServiceCollection services = BuildServiceCollection();
        IServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        // 主窗口建好之后再恢复账号与偏好：字号要作用到窗口上，主题要作用到已加载的控件上。
        _ = Ioc.Default.GetRequiredService<IAppSnapshot>().LoadAsync();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // 退出时清理 ZeroTier：客户端引擎会离开全部已加入的网络（虚拟网卡随 leave 移除）
        // 并停止系统服务，避免「启动器关了，ZeroTier 服务和一堆虚拟网卡还常驻后台」。
        // libzt 引擎是进程内实现，随进程退出，其清理为空操作。整体限时，绝不卡死退出。
        try
        {
            Task cleanup = Task.WhenAll(
                Ioc.Default.GetServices<IZeroTierBackend>().Select(backend => backend.CleanupOnExitAsync()));

            cleanup.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "退出时清理 ZeroTier 失败（忽略）。");
        }

        // 退出前把还在防抖里的字号等改动落库，避免「刚改完就关掉 → 没存上」。
        try
        {
            Ioc.Default.GetRequiredService<IAppSnapshot>().FlushAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "退出前写入偏好失败。");
        }

        Serilog.Log.CloseAndFlush();

        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup -= OnDesktopStartup;
        }
    }

    /// <summary>
    /// 配置日志：内存（界面用）+ 文件（排查崩溃用，%LOCALAPPDATA%\kiriyamalauncher\logs）。
    /// 只配置一次，重复调用返回同一个日志源。
    /// </summary>
    private static ILogSource<LogEvent> ConfigureLogging()
    {
        lock (LOGGING_LOCK)
        {
            if (_logSource is not null)
            {
                return _logSource;
            }

            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "kiriyamalauncher",
                "logs");

            Directory.CreateDirectory(logDirectory);

            Serilog.Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(logDirectory, "app-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true)
                .WriteTo.MemorySink(out ILogSource<LogEvent> logSource, options =>
                {
                    // 内存日志只保留最近 5000 条，旧日志被挤出（文件里仍有全量）。
                    options.MaxLogsCount = 5000;
                })
                .CreateLogger();

            _logSource = logSource;
            Serilog.Log.Information("日志已启动，目录：{Directory}", logDirectory);

            return logSource;
        }
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => LogCrash("AppDomain 未处理异常", e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("未观察到的任务异常", e.Exception);
        e.SetObserved();
    }

    private static void OnUiThreadUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
        => LogCrash("UI 线程未处理异常", e.Exception);

    /// <summary>把崩溃信息立刻写进日志文件（内存日志在进程崩溃后取不到）。</summary>
    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            Serilog.Log.Fatal(exception, "{Source}", source);
            Serilog.Log.CloseAndFlush();
        }
        catch (Exception)
        {
            // 记日志本身失败就不再抛了，避免二次崩溃。
        }
    }

    private static IServiceCollection BuildServiceCollection()
    {
        ILogSource<LogEvent> logSource = ConfigureLogging();

        IServiceCollection services = new ServiceCollection();

        // Application level infrastructure.
        services.AddLogging(configure => configure.AddSerilog(Serilog.Log.Logger))
            .AddSingleton((sp) => { return sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(App)); });

        // Presentation services.
        services.AddScoped(typeof(IAgnosticDispatcher), typeof(AvaloniaDispatcher))
            .AddSingleton(logSource)
            .AddSingleton<ISukiToastManager, SukiToastManager>()
            .AddSingleton<ISukiDialogManager, SukiDialogManager>()
            .AddSingleton<IAppNotifier, SukiAppNotifier>()
            .AddSingleton<IFilePickerService, AvaloniaFilePickerService>()
            .AddSingleton<IGameIntegrationViewModelFactory, GameIntegrationViewModelFactory>()
            .AddSingleton<LanDevicesViewModel>()
            .AddSingleton<RoomsViewModel>()
            .AddSingleton<IAppSnapshot, AppSnapshot>();

        // ZeroTier 传输后端工厂：根据偏好选择 Sockets（libzt 内嵌）还是 Client（客户端，尚未接入）。
        // 工厂本身充当 IZeroTierService 的代理，消费者不用感知后端切换。
        services.AddSingleton<IZeroTierService>(sp =>
            new ZeroTierBackendFactory(
                sp.GetServices<IZeroTierBackend>(),
                () => sp.GetRequiredService<IAppSnapshot>().Preferences.ZeroTierTransportBackend,
                sp.GetRequiredService<ILogger<ZeroTierBackendFactory>>()));

        // View models.
        foreach (Type assemblyType in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (typeof(BaseViewModel).IsAssignableFrom(assemblyType) && !assemblyType.IsAbstract)
            {
                services.AddScoped(assemblyType);
            }
        }

        // Business domain services.
        services.AddBusinessServices();

        return services;
    }
}
