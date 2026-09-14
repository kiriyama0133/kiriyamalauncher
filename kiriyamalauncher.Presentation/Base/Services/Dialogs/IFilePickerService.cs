using System.Collections.Generic;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Dialogs;

/// <summary>
/// 选择文件的服务：ViewModel 不直接碰窗口，由这个接口把文件对话框能力抽出来。
/// </summary>
public interface IFilePickerService
{
    /// <summary>弹出「选择文件」对话框，返回本地文件路径；用户取消时返回 null。</summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="patterns">文件通配符（例如 *.exe）。传 null 表示不限制。</param>
    Task<string?> PickFileAsync(string title, IReadOnlyList<string>? patterns = null);
}
