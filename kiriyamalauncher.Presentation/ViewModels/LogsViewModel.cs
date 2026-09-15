using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kiriyamalauncher.Presentation.Base;
using Material.Icons;
using Serilog.Events;
using Serilog.Sinks.MemorySink;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 日志页：实时展示应用运行日志。
///
/// 数据来自 Serilog 的内存日志源（<see cref="ILogSource{T}"/>）：
///   - 页面打开时先 <see cref="ILogSource{T}.GetLogs"/> 拉取已积累的历史日志；
///   - 之后订阅 <see cref="ILogSource{T}.LogEmitted"/> 实时追加新日志。
/// 日志事件在后台线程触发，统一转回 UI 线程再改集合。
/// </summary>
public partial class LogsViewModel : PageViewModel
{
    /// <summary>内存日志最多保留的条数（旧日志被挤出后不再显示，但文件里仍有全量）。</summary>
    private const int MAX_DISPLAYED_LOGS = 2000;

    private readonly ILogSource<LogEvent> _logSource;

    public override string DisplayName => "日志";

    public override MaterialIconKind Icon => MaterialIconKind.TextBoxOutline;

    /// <summary>界面展示的日志条目（最新的在顶部）。</summary>
    public ObservableCollection<LogEntryViewModel> Logs { get; } = [];

    /// <summary>当前显示的日志条数。</summary>
    [ObservableProperty]
    private int _logCount;

    /// <summary>是否有日志可清空。</summary>
    public bool HasLogs => LogCount > 0;

    /// <summary>是否已停止实时追加（离开页面后为 true，避免后台继续堆积）。</summary>
    private bool _detached;

    public LogsViewModel(ILogSource<LogEvent> logSource)
    {
        _logSource = logSource;
    }

    /// <summary>页面加载：拉取历史日志，并开始实时追加。</summary>
    public async Task LoadAsync()
    {
        _detached = false;
        _logSource.LogEmitted -= OnLogEmitted;
        _logSource.LogEmitted += OnLogEmitted;

        var history = (await _logSource.GetLogs(0, MAX_DISPLAYED_LOGS)).ToArray();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_detached)
            {
                return;
            }

            Logs.Clear();

            // 历史日志按时间正序存着，界面要最新的在上，倒序插入。
            foreach (LogEvent logEvent in history.Reverse())
            {
                Logs.Add(LogEntryViewModel.From(logEvent));
            }

            LogCount = Logs.Count;
        });
    }

    /// <summary>清空内存日志与界面。</summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        await _logSource.ClearLogs();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Logs.Clear();
            LogCount = 0;
        });
    }

    /// <summary>离开页面（页面对象被导航切换走）时解除订阅，避免后台空转。</summary>
    public void Detach()
    {
        _detached = true;
        _logSource.LogEmitted -= OnLogEmitted;
    }

    private void OnLogEmitted(object? sender, LogEvent logEvent)
    {
        if (_detached)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_detached)
            {
                return;
            }

            Logs.Insert(0, LogEntryViewModel.From(logEvent));

            // 只保留最近 MAX_DISPLAYED_LOGS 条，防止界面无限增长。
            while (Logs.Count > MAX_DISPLAYED_LOGS)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }

            LogCount = Logs.Count;
        });
    }
}

/// <summary>单条日志的展示模型。</summary>
public partial class LogEntryViewModel : ObservableObject
{
    /// <summary>格式化后的时间（HH:mm:ss.fff）。</summary>
    public string TimeText { get; }

    /// <summary>日志级别（如 Information / Warning / Error）。</summary>
    public string LevelText { get; }

    /// <summary>渲染后的消息正文。</summary>
    public string Message { get; }

    /// <summary>异常详情（没有则为空）。</summary>
    public string? ExceptionText { get; }

    /// <summary>来源上下文（例如某个类的名字）。</summary>
    public string SourceContext { get; }

    /// <summary>级别对应的界面颜色类名（用于着色）。</summary>
    public string LevelClass { get; }

    /// <summary>是否包含异常详情。</summary>
    public bool HasException => !string.IsNullOrEmpty(ExceptionText);

    private LogEntryViewModel(LogEvent logEvent)
    {
        TimeText = logEvent.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        LevelText = logEvent.Level.ToString();
        Message = logEvent.RenderMessage();

        ExceptionText = logEvent.Exception?.ToString();

        logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext);
        SourceContext = sourceContext is ScalarValue scalar && scalar.Value is string text
            ? text
            : string.Empty;

        LevelClass = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug => "levelDebug",
            LogEventLevel.Information => "levelInfo",
            LogEventLevel.Warning => "levelWarning",
            LogEventLevel.Error or LogEventLevel.Fatal => "levelError",
            _ => "levelInfo"
        };
    }

    public static LogEntryViewModel From(LogEvent logEvent) => new(logEvent);
}
