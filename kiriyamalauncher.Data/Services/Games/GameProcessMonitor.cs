using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IGameProcessMonitor"/> 的默认实现：每 2 秒轮询一次系统进程列表。
/// </summary>
public class GameProcessMonitor : IGameProcessMonitor, IDisposable
{
    private static readonly TimeSpan POLL_INTERVAL = TimeSpan.FromSeconds(2);

    private readonly ILogger<GameProcessMonitor> _logger;
    private readonly object _sync = new();
    private readonly HashSet<string> _watchedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, GameProcessInfo> _running = [];

    private CancellationTokenSource? _loopSource;
    private Task? _loop;

    public GameProcessMonitor(ILogger<GameProcessMonitor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler<GameProcessInfo>? ProcessStarted;

    /// <inheritdoc />
    public event EventHandler<GameProcessInfo>? ProcessStopped;

    /// <inheritdoc />
    public IReadOnlyList<string> WatchedNames
    {
        get
        {
            lock (_sync)
            {
                return _watchedNames.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GameProcessInfo> CurrentProcesses
    {
        get
        {
            lock (_sync)
            {
                return _running.Values.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public void Watch(IEnumerable<string> processNames)
    {
        lock (_sync)
        {
            _watchedNames.Clear();
            foreach (string name in processNames)
            {
                string normalized = Normalize(name);
                if (normalized.Length > 0)
                {
                    _watchedNames.Add(normalized);
                }
            }
        }

        EnsureLoop();

        // 立刻扫一次，不用等第一个轮询间隔。
        _ = PollAsync();
    }

    /// <inheritdoc />
    public void StopWatching()
    {
        lock (_sync)
        {
            _watchedNames.Clear();
            _running.Clear();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GameProcessInfo> Scan(IEnumerable<string> processNames)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in processNames)
        {
            string normalized = Normalize(name);
            if (normalized.Length > 0)
            {
                names.Add(normalized);
            }
        }

        return names.Count == 0 ? [] : EnumerateMatchingProcesses(names);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource? source;

        lock (_sync)
        {
            source = _loopSource;
            _loopSource = null;
            _loop = null;
        }

        if (source is not null)
        {
            source.Cancel();
            source.Dispose();
        }
    }

    private void EnsureLoop()
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }

            _loopSource = new CancellationTokenSource();
            CancellationToken token = _loopSource.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(POLL_INTERVAL);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await PollAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "监视游戏进程时出错。");
        }
    }

    private Task PollAsync()
    {
        HashSet<string> names;
        lock (_sync)
        {
            names = new HashSet<string>(_watchedNames, StringComparer.OrdinalIgnoreCase);
        }

        if (names.Count == 0)
        {
            lock (_sync)
            {
                _running.Clear();
            }

            return Task.CompletedTask;
        }

        return Task.Run(() => Poll(names));
    }

    private void Poll(HashSet<string> names)
    {
        List<GameProcessInfo> current = EnumerateMatchingProcesses(names);
        Dictionary<int, GameProcessInfo> previous;

        lock (_sync)
        {
            previous = new Dictionary<int, GameProcessInfo>(_running);
            _running.Clear();

            foreach (GameProcessInfo info in current)
            {
                _running[info.ProcessId] = info;
            }
        }

        foreach (GameProcessInfo info in current)
        {
            if (previous.ContainsKey(info.ProcessId))
            {
                continue;
            }

            _logger.LogInformation("检测到游戏进程启动：{Process}", info.DisplayName);
            ProcessStarted?.Invoke(this, info);
        }

        foreach ((int processId, GameProcessInfo info) in previous)
        {
            if (current.Any(candidate => candidate.ProcessId == processId))
            {
                continue;
            }

            _logger.LogInformation("检测到游戏进程退出：{Process}", info.DisplayName);
            ProcessStopped?.Invoke(this, info);
        }
    }

    private static List<GameProcessInfo> EnumerateMatchingProcesses(HashSet<string> names)
    {
        List<GameProcessInfo> result = [];

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (!names.Contains(name))
                {
                    continue;
                }

                string executablePath = string.Empty;

                try
                {
                    executablePath = process.MainModule?.FileName ?? string.Empty;
                }
                catch (Exception)
                {
                    // 系统进程 / 权限不足时读不到路径，忽略。
                }

                result.Add(new GameProcessInfo(process.Id, name, executablePath));
            }
            catch (Exception)
            {
                // 进程刚好退出时读属性会抛异常，跳过即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    /// <summary>把进程名统一成「不带目录、不带 .exe」的形式。</summary>
    private static string Normalize(string processName)
    {
        string name = processName.Trim();

        if (name.Length == 0)
        {
            return string.Empty;
        }

        name = Path.GetFileName(name);

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
