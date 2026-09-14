using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace kiriyamalauncher.Presentation.Base.Services.Dialogs;

/// <summary>
/// 基于 Avalonia StorageProvider 的文件选择实现（挂在主窗口上）。
/// </summary>
public class AvaloniaFilePickerService : IFilePickerService
{
    /// <inheritdoc />
    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string>? patterns = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return null;
        }

        FilePickerOpenOptions options = new()
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = patterns is null
                ? null
                : [new FilePickerFileType("可执行文件") { Patterns = [.. patterns] }]
        };

        IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(options);

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
