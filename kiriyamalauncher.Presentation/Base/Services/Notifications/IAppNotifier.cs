using Avalonia.Controls.Notifications;

namespace kiriyamalauncher.Presentation.Base.Services.Notifications;

/// <summary>
/// 应用内通知（右下角 toast）。
///
/// 统一在这里包装 SukiUI 的 toast，业务代码只需要关心「成功 / 提示 / 警告 / 错误」四种语义，
/// 图标与配色由 SukiUI 根据 <see cref="NotificationType"/> 自动处理。
/// </summary>
public interface IAppNotifier
{
    /// <summary>成功提示（勾选图标）。</summary>
    void Success(string title, string? content = null);

    /// <summary>普通信息提示。</summary>
    void Info(string title, string? content = null);

    /// <summary>警告提示。</summary>
    void Warning(string title, string? content = null);

    /// <summary>错误提示。</summary>
    void Error(string title, string? content = null);

    /// <summary>按指定类型弹提示。</summary>
    void Show(NotificationType type, string title, string? content = null);
}
