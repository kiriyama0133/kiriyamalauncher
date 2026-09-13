namespace kiriyamalauncher.Business.Modules.GameSession.DTOs;

/// <summary>
/// 一个支持联机的游戏（游戏类别）。
/// </summary>
public class GameDto
{
    /// <summary>游戏名，卡片标题。</summary>
    public required string Name { get; init; }

    /// <summary>一句话介绍，卡片副标题。</summary>
    public required string Description { get; init; }

    /// <summary>
    /// 封面图标识（例如 "sid.jpeg"）。表现层负责把它解析成实际资源路径；
    /// 若本身已是完整地址（avares://、http(s):// 或绝对路径）表现层会直接使用。
    /// </summary>
    public string? CoverImage { get; init; }
}
