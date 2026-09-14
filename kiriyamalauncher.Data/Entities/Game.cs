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

    /// <summary>对应的联机集成标识（例如 civ6）；没有联机支持的游戏留空。</summary>
    public string? IntegrationId { get; init; }
}
