using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.DependencyInjection;
using kiriyamalauncher.Business;
using kiriyamalauncher.Presentation.Base.Services;
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
using System.Reflection;

namespace kiriyamalauncher.Presentation;

public partial class App : Application
{
    public override void Initialize()
    {
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
        // 从 SQLite 恢复用户偏好（字号、主题）。
        _ = Ioc.Default.GetRequiredService<IAppSnapshot>().LoadAsync();

        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Serilog.Log.CloseAndFlush();

        if (sender is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Startup -= OnDesktopStartup;
        }
    }

    private static IServiceCollection BuildServiceCollection()
    {
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.MemorySink(out ILogSource<LogEvent> logSource)
            .CreateLogger();

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
            .AddSingleton<IAppSnapshot, AppSnapshot>();

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
