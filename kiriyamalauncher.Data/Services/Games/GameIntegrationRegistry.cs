using System;
using System.Collections.Generic;
using System.Linq;

namespace kiriyamalauncher.Data;

/// <summary>
/// 默认实现：从 DI 里收集所有 <see cref="IGameIntegration"/>。
/// </summary>
public class GameIntegrationRegistry : IGameIntegrationRegistry
{
    private readonly IReadOnlyList<IGameIntegration> _integrations;
    private readonly IReadOnlyDictionary<string, IGameIntegration> _byId;

    public GameIntegrationRegistry(IEnumerable<IGameIntegration> integrations)
    {
        _integrations = integrations.ToList();
        _byId = _integrations.ToDictionary(integration => integration.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<IGameIntegration> All => _integrations;

    /// <inheritdoc />
    public IGameIntegration? Find(string? integrationId)
    {
        if (string.IsNullOrWhiteSpace(integrationId))
        {
            return null;
        }

        return _byId.TryGetValue(integrationId.Trim(), out IGameIntegration? integration) ? integration : null;
    }
}
