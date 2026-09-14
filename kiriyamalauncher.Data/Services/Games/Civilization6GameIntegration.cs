using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 文明 6 的联机集成。
///
/// 组件都在 Scripts/civ6 下随应用一起发行：
/// hookdll.dll / hookdll64.dll 是注入用的 DLL（同一份 x64 DLL 的两种命名，工具按名字挑），
/// injciv6.exe 是注入工具（自己去找 CivilizationVI* 进程，不需要参数），
/// civ6remove.exe 是卸载工具（把已注入的模块从进程里卸掉）。
/// </summary>
public class Civilization6GameIntegration : IGameIntegration
{
    /// <summary>集成标识。</summary>
    public const string INTEGRATION_ID = "civ6";

    private readonly IGameToolRunner _toolRunner;
    private readonly ILogger<Civilization6GameIntegration> _logger;
    private readonly string _resourceDirectory;

    public Civilization6GameIntegration(
        IGameToolRunner toolRunner,
        GameResourceLocator resourceLocator,
        ILogger<Civilization6GameIntegration> logger)
    {
        _toolRunner = toolRunner;
        _logger = logger;
        _resourceDirectory = resourceLocator.GetGameResourceDirectory(INTEGRATION_ID);
    }

    /// <inheritdoc />
    public string Id => INTEGRATION_ID;

    /// <inheritdoc />
    public string DisplayName => "文明 6";

    /// <inheritdoc />
    public IReadOnlyList<string> ProcessNames { get; } =
    [
        "CivilizationVI",
        "CivilizationVI_DX11",
        "CivilizationVI_DX12"
    ];

    /// <inheritdoc />
    public string ExecutableFileNameHint => "CivilizationVI_DX12.exe";

    /// <inheritdoc />
    public IReadOnlyList<string> ResourceFiles { get; } =
    [
        "hookdll.dll",
        "hookdll64.dll",
        "injciv6.exe",
        "civ6remove.exe"
    ];

    /// <inheritdoc />
    public Task<GameToolResult> InjectAsync(GameProcessInfo process, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("准备注入文明 6 联机组件：{Process}", process.DisplayName);
        return RunToolAsync("injciv6.exe", "注入", cancellationToken);
    }

    /// <inheritdoc />
    public Task<GameToolResult> RemoveInjectionAsync(GameProcessInfo process, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("准备卸载文明 6 联机组件：{Process}", process.DisplayName);
        return RunToolAsync("civ6remove.exe", "卸载", cancellationToken);
    }

    /// <summary>两个工具都是「自己找进程」，所以不需要命令行参数，只要工作目录指对。</summary>
    private Task<GameToolResult> RunToolAsync(string toolFileName, string actionName, CancellationToken cancellationToken)
    {
        string toolPath = Path.Combine(_resourceDirectory, toolFileName);

        if (!File.Exists(toolPath))
        {
            _logger.LogError("找不到{Action}工具：{Path}", actionName, toolPath);
            return Task.FromResult(GameToolResult.Fail($"找不到{actionName}工具：{toolPath}"));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(GameToolResult.Fail("联机组件目前只支持 Windows。"));
        }

        return _toolRunner.RunAsync(toolPath, _resourceDirectory, [], null, cancellationToken);
    }
}
