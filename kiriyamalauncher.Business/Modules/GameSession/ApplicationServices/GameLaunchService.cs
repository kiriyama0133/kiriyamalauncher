using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// <see cref="IGameLaunchService"/> 的默认实现：
/// 记住启动路径 → 启动游戏 → 轮询进程 → 检测到进程就调用该游戏的注入组件。
/// </summary>
public class GameLaunchService : IGameLaunchService, IDisposable
{
    private readonly IGameIntegrationRegistry _integrationRegistry;
    private readonly IGameProcessMonitor _processMonitor;
    private readonly IGameLaunchPathRepository _launchPathRepository;
    private readonly ILogger<GameLaunchService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, GameIntegrationStatusDto> _statuses = new(StringComparer.OrdinalIgnoreCase);

    private IGameIntegration? _activeIntegration;
    private bool _autoInjectEnabled = true;
    private bool _autoInjectAlways;

    public GameLaunchService(
        IGameIntegrationRegistry integrationRegistry,
        IGameProcessMonitor processMonitor,
        IGameLaunchPathRepository launchPathRepository,
        ILogger<GameLaunchService> logger)
    {
        _integrationRegistry = integrationRegistry;
        _processMonitor = processMonitor;
        _launchPathRepository = launchPathRepository;
        _logger = logger;

        _processMonitor.ProcessStarted += OnProcessStarted;
        _processMonitor.ProcessStopped += OnProcessStopped;
    }

    /// <inheritdoc />
    public event EventHandler<GameIntegrationStatusDto>? StatusChanged;

    /// <inheritdoc />
    public bool IsAutoInjectEnabled => _autoInjectEnabled;

    /// <inheritdoc />
    public async Task<string?> GetExecutablePathAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        GameLaunchPath? saved = await _launchPathRepository.FindAsync(integrationId, cancellationToken);
        return saved?.ExecutablePath;
    }

    /// <inheritdoc />
    public async Task SetExecutablePathAsync(string integrationId, string executablePath, CancellationToken cancellationToken = default)
    {
        GameLaunchPath? saved = await _launchPathRepository.FindAsync(integrationId, cancellationToken);
        GameLaunchPath path = saved ?? new GameLaunchPath { GameId = integrationId, CreatedAt = DateTime.Now };

        path.ExecutablePath = executablePath.Trim();

        await _launchPathRepository.SaveAsync(path, cancellationToken);

        _logger.LogInformation("已保存 {GameId} 的启动路径：{Path}", integrationId, path.ExecutablePath);
    }

    /// <inheritdoc />
    public void SetAutoInjectEnabled(bool enabled)
    {
        _autoInjectEnabled = enabled;
        _logger.LogInformation("自动注入已{State}。", enabled ? "开启" : "关闭");

        IGameIntegration? integration;
        lock (_sync)
        {
            integration = _activeIntegration;
        }

        if (integration is not null)
        {
            UpdateStatus(integration, _processMonitor.CurrentProcesses.FirstOrDefault(), null);
        }
    }

    /// <inheritdoc />
    public void BeginMonitoring(string? integrationId)
    {
        IGameIntegration? integration = _integrationRegistry.Find(integrationId);

        lock (_sync)
        {
            _activeIntegration = integration;
        }

        if (integration is null)
        {
            _processMonitor.StopWatching();
            return;
        }

        _logger.LogInformation(
            "开始监视 {Game} 的进程：{ProcessNames}",
            integration.DisplayName,
            string.Join(", ", integration.ProcessNames));

        _processMonitor.Watch(integration.ProcessNames);
        UpdateStatus(integration, _processMonitor.CurrentProcesses.FirstOrDefault(), null);
    }

    /// <inheritdoc />
    public void StopMonitoring()
    {
        lock (_sync)
        {
            _activeIntegration = null;
        }

        if (!_autoInjectAlways)
        {
            _processMonitor.StopWatching();
        }
    }

    /// <inheritdoc />
    public void StartAutoInject()
    {
        _autoInjectAlways = true;

        List<string> processNames = [];

        foreach (IGameIntegration integration in _integrationRegistry.All)
        {
            foreach (string name in integration.ProcessNames)
            {
                if (!processNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    processNames.Add(name);
                }
            }
        }

        if (processNames.Count == 0)
        {
            _logger.LogWarning("没有可监视的游戏集成，常驻自动注入未生效。");
            return;
        }

        _logger.LogInformation(
            "已开启常驻自动注入（检测到游戏进程就注入），监视：{Names}",
            string.Join(", ", processNames));

        _processMonitor.Watch(processNames);
    }

    /// <inheritdoc />
    public void StopAutoInject()
    {
        _autoInjectAlways = false;

        IGameIntegration? active;
        lock (_sync)
        {
            active = _activeIntegration;
        }

        if (active is null)
        {
            _processMonitor.StopWatching();
        }

        _logger.LogInformation("已停止常驻自动注入。");
    }

    /// <inheritdoc />
    public GameIntegrationStatusDto GetStatus(string integrationId)
    {
        lock (_sync)
        {
            return _statuses.TryGetValue(integrationId, out GameIntegrationStatusDto? status)
                ? status
                : new GameIntegrationStatusDto { IntegrationId = integrationId, IsAutoInjectEnabled = _autoInjectEnabled, StatusText = "还没有开始监视。" };
        }
    }

    /// <inheritdoc />
    public GameIntegrationInfoDto? FindIntegration(string? integrationId)
    {
        IGameIntegration? integration = _integrationRegistry.Find(integrationId);

        return integration is null
            ? null
            : new GameIntegrationInfoDto
            {
                IntegrationId = integration.Id,
                DisplayName = integration.DisplayName,
                ExecutableFileNameHint = integration.ExecutableFileNameHint,
                ProcessNames = integration.ProcessNames
            };
    }

    /// <inheritdoc />
    public async Task<GameOperationResultDto> LaunchAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        IGameIntegration? integration = _integrationRegistry.Find(integrationId);

        if (integration is null)
        {
            return GameOperationResultDto.Fail("这个游戏还没有配置联机集成。");
        }

        if (!OperatingSystem.IsWindows())
        {
            return GameOperationResultDto.Fail("联机组件目前只支持 Windows。");
        }

        string? executablePath = await GetExecutablePathAsync(integrationId, cancellationToken);

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return GameOperationResultDto.Fail("请先选择游戏的启动程序（.exe）。");
        }

        try
        {
            ProcessStartInfo startInfo = new(executablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            };

            Process.Start(startInfo);
            _logger.LogInformation("已启动游戏：{Path}", executablePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动游戏失败：{Path}", executablePath);
            return GameOperationResultDto.Fail($"启动游戏失败：{ex.Message}");
        }

        return GameOperationResultDto.Ok($"已启动 {Path.GetFileName(executablePath)}，检测到进程后会自动注入组件。");
    }

    /// <inheritdoc />
    public async Task<GameOperationResultDto> InjectNowAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        IGameIntegration? integration = _integrationRegistry.Find(integrationId);

        if (integration is null)
        {
            return GameOperationResultDto.Fail("这个游戏还没有配置联机集成。");
        }

        GameProcessInfo? process = _processMonitor.Scan(integration.ProcessNames).FirstOrDefault();

        if (process is null)
        {
            return GameOperationResultDto.Fail("还没有检测到正在运行的游戏进程，请先启动游戏。");
        }

        return await InjectAsync(integration, process, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GameOperationResultDto> RemoveInjectionAsync(string integrationId, CancellationToken cancellationToken = default)
    {
        IGameIntegration? integration = _integrationRegistry.Find(integrationId);

        if (integration is null)
        {
            return GameOperationResultDto.Fail("这个游戏还没有配置联机集成。");
        }

        GameProcessInfo? process = _processMonitor.Scan(integration.ProcessNames).FirstOrDefault();

        if (process is null)
        {
            return GameOperationResultDto.Fail("没有检测到正在运行的游戏进程。");
        }

        GameToolResult result = await integration.RemoveInjectionAsync(process, cancellationToken);
        UpdateStatus(integration, process, result.IsSuccess ? false : null);

        return result.IsSuccess
            ? GameOperationResultDto.Ok(result.Message)
            : GameOperationResultDto.Fail(result.Message);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _processMonitor.ProcessStarted -= OnProcessStarted;
        _processMonitor.ProcessStopped -= OnProcessStopped;
    }

    private async Task<GameOperationResultDto> InjectAsync(IGameIntegration integration, GameProcessInfo process, CancellationToken cancellationToken)
    {
        GameToolResult result = await integration.InjectAsync(process, cancellationToken);

        UpdateStatus(integration, process, result.IsSuccess);

        return result.IsSuccess
            ? GameOperationResultDto.Ok(result.Message)
            : GameOperationResultDto.Fail(result.Message);
    }

    private void OnProcessStarted(object? sender, GameProcessInfo process)
    {
        IGameIntegration? integration;

        lock (_sync)
        {
            integration = _activeIntegration;
        }

        // 没停留在游戏板块时，用进程名反查是哪个游戏的集成（常驻自动注入）。
        integration ??= _autoInjectAlways ? FindIntegrationByProcessName(process.Name) : null;

        if (integration is null)
        {
            return;
        }

        UpdateStatus(integration, process, null);

        if (_autoInjectEnabled)
        {
            // 注入是个外部进程调用，放后台跑，失败也只记日志 + 更新状态。
            _ = Task.Run(async () =>
            {
                try
                {
                    await InjectAsync(integration, process, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "自动注入失败：{Process}", process.DisplayName);
                }
            });
        }
    }

    /// <summary>按进程名找对应的游戏集成。</summary>
    private IGameIntegration? FindIntegrationByProcessName(string processName)
    {
        foreach (IGameIntegration integration in _integrationRegistry.All)
        {
            foreach (string name in integration.ProcessNames)
            {
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                {
                    return integration;
                }
            }
        }

        return null;
    }

    private void OnProcessStopped(object? sender, GameProcessInfo process)
    {
        IGameIntegration? integration;
        lock (_sync)
        {
            integration = _activeIntegration;
        }

        if (integration is null)
        {
            return;
        }

        // 游戏退出了，注入的模块也随之消失。
        UpdateStatus(integration, null, false);
    }

    private void UpdateStatus(IGameIntegration integration, GameProcessInfo? process, bool? isInjected)
    {
        GameIntegrationStatusDto status;

        lock (_sync)
        {
            _statuses.TryGetValue(integration.Id, out GameIntegrationStatusDto? previous);

            bool isRunning = process is not null;
            bool injected = isInjected ?? (isRunning ? previous?.IsInjected ?? false : false);

            status = new GameIntegrationStatusDto
            {
                IntegrationId = integration.Id,
                IsGameRunning = isRunning,
                ProcessId = process?.ProcessId,
                IsInjected = injected,
                IsAutoInjectEnabled = _autoInjectEnabled,
                StatusText = BuildStatusText(integration, isRunning, injected)
            };

            _statuses[integration.Id] = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private static string BuildStatusText(IGameIntegration integration, bool isRunning, bool isInjected)
    {
        if (!isRunning)
        {
            return $"正在监视游戏进程：{string.Join(" / ", integration.ProcessNames)}";
        }

        return isInjected
            ? "已检测到游戏进程，联机组件已注入。"
            : "已检测到游戏进程，正在注入联机组件……";
    }
}
