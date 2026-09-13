namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// 一个支持联机的游戏（游戏类别）。
/// </summary>
public record Game
{
    /// <summary>游戏名，卡片标题。</summary>
    public required string Name { get; init; }

    /// <summary>一句话介绍，卡片副标题。</summary>
    public required string Description { get; init; }

    /// <summary>封面图标识（例如 "sid.jpeg"），由表现层解析成实际资源路径。</summary>
    public string? CoverImage { get; init; }
}
