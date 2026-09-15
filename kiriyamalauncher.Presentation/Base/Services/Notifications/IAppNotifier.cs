using Avalonia.Controls.Notifications;
using System;

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

    /// <summary>
    /// 弹一个「进行中」的 loading toast（带转圈动画，不会自动消失）。
    /// 返回一个句柄，用完记得 <see cref="IDisposable.Dispose"/> 来关闭它——
    /// 通常配合 <c>using</c> 包住一段异步操作。
    /// </summary>
    IDisposable ShowLoading(string title, string? content = null);
}
