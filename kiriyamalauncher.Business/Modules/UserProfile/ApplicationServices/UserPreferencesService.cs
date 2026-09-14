using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 偏好服务：负责数据层实体与业务层 DTO 之间的转换。
/// </summary>
public class UserPreferencesService : IUserPreferencesService
{
    private readonly IUserPreferencesRepository _preferencesRepository;

    public UserPreferencesService(IUserPreferencesRepository preferencesRepository)
    {
        _preferencesRepository = preferencesRepository;
    }

    /// <inheritdoc />
    public async Task<UserPreferencesDto> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        UserPreferences preferences = await _preferencesRepository.GetOrCreateAsync(cancellationToken);
        return ToDto(preferences);
    }

    /// <inheritdoc />
    public async Task SaveAsync(UserPreferencesDto preferences, CancellationToken cancellationToken = default)
    {
        UserPreferences entity = new()
        {
            Id = preferences.Id,
            FontSize = preferences.FontSize,
            MoonServerIp = preferences.MoonServerIp,
            ZeroTierNetworkId = preferences.ZeroTierNetworkId,
            ZeroTierConnectionMode = preferences.ZeroTierConnectionMode,
            BaseTheme = preferences.BaseTheme,
            ColorTheme = preferences.ColorTheme,
            UpdatedAt = preferences.UpdatedAt
        };

        await _preferencesRepository.SaveAsync(entity, cancellationToken);

        preferences.Id = entity.Id;
        preferences.UpdatedAt = entity.UpdatedAt;
    }

    private static UserPreferencesDto ToDto(UserPreferences preferences) => new()
    {
        Id = preferences.Id,
        FontSize = preferences.FontSize,
        MoonServerIp = preferences.MoonServerIp,
        ZeroTierNetworkId = preferences.ZeroTierNetworkId,
        ZeroTierConnectionMode = preferences.ZeroTierConnectionMode,
        BaseTheme = preferences.BaseTheme,
        ColorTheme = preferences.ColorTheme,
        UpdatedAt = preferences.UpdatedAt
    };
}
