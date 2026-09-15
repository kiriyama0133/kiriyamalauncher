using Avalonia.Controls.Notifications;
using SukiUI.Toasts;
using System;

namespace kiriyamalauncher.Presentation.Base.Services.Notifications;

/// <summary>
/// 基于 SukiUI Toast 的通知实现：右下角弹出，4 秒后自动消失，也可以点一下立刻关掉。
/// </summary>
public class SukiAppNotifier : IAppNotifier
{
    /// <summary>toast 自动消失的时间。</summary>
    private static readonly TimeSpan AUTO_DISMISS_AFTER = TimeSpan.FromSeconds(4);

    private readonly ISukiToastManager _toastManager;

    public SukiAppNotifier(ISukiToastManager toastManager)
    {
        _toastManager = toastManager;
    }

    public void Success(string title, string? content = null) => Show(NotificationType.Success, title, content);

    public void Info(string title, string? content = null) => Show(NotificationType.Information, title, content);

    public void Warning(string title, string? content = null) => Show(NotificationType.Warning, title, content);

    public void Error(string title, string? content = null) => Show(NotificationType.Error, title, content);

    public void Show(NotificationType type, string title, string? content = null)
    {
        _toastManager.CreateToast()
            .WithTitle(title)
            .WithContent(content ?? string.Empty)
            .OfType(type)
            .Dismiss().After(AUTO_DISMISS_AFTER, true)
            .Dismiss().ByClicking()
            .Queue();
    }

    public IDisposable ShowLoading(string title, string? content = null)
    {
        ISukiToast toast = _toastManager.CreateToast()
            .WithTitle(title)
            .WithContent(content ?? string.Empty)
            .WithLoadingState(true)
            .Dismiss().ByClicking()
            .Queue();

        return new LoadingToastHandle(_toastManager, toast);
    }

    /// <summary>loading toast 的句柄：<see cref="Dispose"/> 时关闭对应的 toast。</summary>
    private sealed class LoadingToastHandle : IDisposable
    {
        private readonly ISukiToastManager _manager;
        private readonly ISukiToast _toast;

        public LoadingToastHandle(ISukiToastManager manager, ISukiToast toast)
        {
            _manager = manager;
            _toast = toast;
        }

        public void Dispose()
        {
            if (!_manager.IsDismissed(_toast))
            {
                _manager.Dismiss(_toast);
            }
        }
    }
}
