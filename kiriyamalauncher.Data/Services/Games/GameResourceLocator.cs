using System;
using System.IO;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏联机组件的位置解析：组件随应用一起发行，放在发布目录下的 Scripts/&lt;gameId&gt; 里。
/// </summary>
public class GameResourceLocator
{
    /// <summary>组件根目录名。</summary>
    public const string SCRIPTS_FOLDER = "Scripts";

    private readonly string _baseDirectory;

    public GameResourceLocator()
        : this(AppContext.BaseDirectory)
    {
    }

    public GameResourceLocator(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    /// <summary>某个游戏的组件目录（不保证存在）。</summary>
    public string GetGameResourceDirectory(string gameId)
        => Path.Combine(_baseDirectory, SCRIPTS_FOLDER, gameId);

    /// <summary>取某个游戏组件文件的完整路径；不存在时返回 null。</summary>
    public string? FindGameResourceFile(string gameId, string fileName)
    {
        string path = Path.Combine(GetGameResourceDirectory(gameId), fileName);
        return File.Exists(path) ? path : null;
    }
}
