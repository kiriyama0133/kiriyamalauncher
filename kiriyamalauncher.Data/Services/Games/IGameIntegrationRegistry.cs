using System.Collections.Generic;

namespace kiriyamalauncher.Data;

/// <summary>
/// 游戏集成注册表：按 IntegrationId 找对应的 <see cref="IGameIntegration"/>。
/// </summary>
public interface IGameIntegrationRegistry
{
    /// <summary>所有已注册的游戏集成。</summary>
    IReadOnlyList<IGameIntegration> All { get; }

    /// <summary>按标识查找；找不到返回 null（游戏数据里可以不配集成）。</summary>
    IGameIntegration? Find(string? integrationId);
}
