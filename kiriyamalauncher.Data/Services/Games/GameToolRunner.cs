using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IGameToolRunner"/> 的默认实现：同步等待外部工具结束（带超时）。
/// </summary>
public class GameToolRunner : IGameToolRunner
{
    private static readonly TimeSpan DEFAULT_TIMEOUT = TimeSpan.FromSeconds(15);

    private readonly ILogger<GameToolRunner> _logger;

    public GameToolRunner(ILogger<GameToolRunner> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GameToolResult> RunAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new(executablePath)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogInformation("运行联机工具：{Tool}（工作目录 {Directory}）", executablePath, workingDirectory);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return GameToolResult.Fail($"无法启动 {System.IO.Path.GetFileName(executablePath)}。");
            }

            using CancellationTokenSource waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitSource.CancelAfter(timeout ?? DEFAULT_TIMEOUT);

            try
            {
                await process.WaitForExitAsync(waitSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 工具还在跑（多半是在等用户点确认框），不算失败。
                _logger.LogWarning("联机工具仍在运行：{Tool}", executablePath);
                return GameToolResult.Ok($"{System.IO.Path.GetFileName(executablePath)} 已启动，但还没有退出（可能有需要确认的对话框）。");
            }

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken);
            string message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();

            if (process.ExitCode == 0)
            {
                _logger.LogInformation("联机工具执行成功：{Tool}", executablePath);
                return GameToolResult.Ok(string.IsNullOrWhiteSpace(message) ? $"{System.IO.Path.GetFileName(executablePath)} 执行完成。" : message);
            }

            _logger.LogWarning("联机工具执行失败（退出码 {Code}）：{Message}", process.ExitCode, message);
            return GameToolResult.Fail(string.IsNullOrWhiteSpace(message)
                ? $"{System.IO.Path.GetFileName(executablePath)} 退出码 {process.ExitCode}。"
                : message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "运行联机工具失败：{Tool}", executablePath);
            return GameToolResult.Fail($"运行 {System.IO.Path.GetFileName(executablePath)} 失败：{ex.Message}");
        }
    }
}
