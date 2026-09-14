using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;
using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Presentation.Base.Services.Dialogs;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 某个游戏的联机面板：选择启动路径、启动游戏、注入 / 卸载组件、显示进程检测状态。
///
/// 这里只依赖 <see cref="IGameLaunchService"/> 这个与游戏无关的抽象，
/// 具体是哪个游戏（进程名、组件、注入方式）由 Business / Data 层决定。
/// </summary>
public partial class GameIntegrationViewModel : ObservableObject
{
    private readonly IGameLaunchService _launchService;
    private readonly IFilePickerService _filePickerService;
    private readonly IAppNotifier _notifier;
    private readonly ILogger<GameIntegrationViewModel> _logger;

    /// <summary>集成标识（例如 civ6）。</summary>
    public string IntegrationId { get; }

    /// <summary>游戏显示名。</summary>
    public string DisplayName { get; }

    /// <summary>启动程序的默认文件名提示。</summary>
    public string ExecutableFileNameHint { get; }

    /// <summary>被监视的进程名列表（用于界面提示）。</summary>
    public string ProcessNamesText { get; }

    /// <summary>当前应用是否以管理员身份运行（注入游戏需要与游戏权限对等）。</summary>
    public bool IsElevated { get; }

    /// <summary>权限不足时的提示；已提权时为空。</summary>
    public string ElevationHint => IsElevated
        ? string.Empty
        : "⚠ 当前应用不是以管理员身份运行：如果游戏是管理员启动的，注入会失败（需要时请用管理员身份重启本应用）。";

    /// <summary>游戏启动程序路径。</summary>
    [ObservableProperty]
    private string _executablePath = string.Empty;

    /// <summary>进程检测 / 注入状态文字。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>是否检测到游戏进程。</summary>
    [ObservableProperty]
    private bool _isGameRunning;

    /// <summary>联机组件是否已注入。</summary>
    [ObservableProperty]
    private bool _isInjected;

    /// <summary>检测到进程后是否自动注入。</summary>
    [ObservableProperty]
    private bool _isAutoInjectEnabled = true;

    /// <summary>是否有操作正在进行。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>是否已经选好启动路径。</summary>
    public bool HasExecutablePath => !string.IsNullOrWhiteSpace(ExecutablePath);

    /// <summary>「启动游戏」是否可用。</summary>
    public bool CanLaunch => HasExecutablePath && !IsBusy;

    public GameIntegrationViewModel(
        IGameLaunchService launchService,
        IFilePickerService filePickerService,
        IAppNotifier notifier,
        ILogger<GameIntegrationViewModel> logger,
        GameIntegrationInfoDto integration)
    {
        _launchService = launchService;
        _filePickerService = filePickerService;
        _notifier = notifier;
        _logger = logger;

        IntegrationId = integration.IntegrationId;
        DisplayName = integration.DisplayName;
        ExecutableFileNameHint = integration.ExecutableFileNameHint;
        ProcessNamesText = string.Join(" / ", integration.ProcessNames);
        IsAutoInjectEnabled = launchService.IsAutoInjectEnabled;
        IsElevated = ProcessElevation.IsElevated();
    }

    /// <summary>进入游戏板块时调用：读取已保存的路径与当前状态。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            string? path = await _launchService.GetExecutablePathAsync(IntegrationId);
            if (!string.IsNullOrWhiteSpace(path))
            {
                ExecutablePath = path;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取游戏启动路径失败。");
        }

        ApplyStatus(_launchService.GetStatus(IntegrationId));
    }

    /// <summary>把服务端状态同步到界面（由页面在 UI 线程上调用）。</summary>
    public void ApplyStatus(GameIntegrationStatusDto status)
    {
        if (!string.Equals(status.IntegrationId, IntegrationId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IsGameRunning = status.IsGameRunning;
        IsInjected = status.IsInjected;
        IsAutoInjectEnabled = status.IsAutoInjectEnabled;
        StatusText = status.StatusText;
    }

    partial void OnExecutablePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasExecutablePath));
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    partial void OnIsAutoInjectEnabledChanged(bool value)
    {
        if (_launchService.IsAutoInjectEnabled != value)
        {
            _launchService.SetAutoInjectEnabled(value);
        }
    }

    /// <summary>选择游戏启动程序（.exe），选完立刻保存。</summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? path = await _filePickerService.PickFileAsync($"选择 {DisplayName} 的启动程序", ["*.exe"]);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ExecutablePath = path;

        try
        {
            await _launchService.SetExecutablePathAsync(IntegrationId, path);
            _notifier.Info("已保存启动路径", path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存游戏启动路径失败。");
            _notifier.Error("保存失败", ex.Message);
        }
    }

    /// <summary>用保存的路径启动游戏，之后进程出现时自动注入。</summary>
    [RelayCommand]
    private Task LaunchAsync() => RunOperationAsync(
        () => _launchService.LaunchAsync(IntegrationId),
        "启动游戏");

    /// <summary>对当前正在运行的游戏进程立即注入一次。</summary>
    [RelayCommand]
    private Task InjectNowAsync() => RunOperationAsync(
        () => _launchService.InjectNowAsync(IntegrationId),
        "注入联机组件");

    /// <summary>把组件从游戏进程里卸载。</summary>
    [RelayCommand]
    private Task RemoveInjectionAsync() => RunOperationAsync(
        () => _launchService.RemoveInjectionAsync(IntegrationId),
        "卸载联机组件");

    private async Task RunOperationAsync(Func<Task<GameOperationResultDto>> operation, string title)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;

        try
        {
            GameOperationResultDto result = await operation();

            if (result.IsSuccess)
            {
                _notifier.Success(title, result.Message);
            }
            else
            {
                _notifier.Warning(title, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Title} 失败。", title);
            _notifier.Error(title, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
