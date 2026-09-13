using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 用户资料服务：负责数据层实体与业务层 DTO 之间的转换。
/// </summary>
public class UserProfileService : IUserProfileService
{
    private readonly IUserRepository _userRepository;

    public UserProfileService(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<UserProfileDto> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        User user = await _userRepository.GetOrCreateCurrentAsync(cancellationToken);
        return ToDto(user);
    }

    public async Task SaveAsync(UserProfileDto profile, CancellationToken cancellationToken = default)
    {
        User user = new()
        {
            Id = profile.Id,
            Nickname = profile.Nickname,
            LoginName = profile.LoginName,
            Email = profile.Email,
            CreatedAt = profile.CreatedAt,
            ProfileUpdatedAt = DateTime.Now,
            FontSize = profile.FontSize,
            MoonServerIp = profile.MoonServerIp,
            ZeroTierNetworkId = profile.ZeroTierNetworkId,
            ZeroTierConnectionMode = profile.ZeroTierConnectionMode,
            BaseTheme = profile.BaseTheme,
            ColorTheme = profile.ColorTheme
        };

        await _userRepository.SaveAsync(user, cancellationToken);

        profile.Id = user.Id;
        profile.ProfileUpdatedAt = user.ProfileUpdatedAt;
    }

    private static UserProfileDto ToDto(User user) => new()
    {
        Id = user.Id,
        Nickname = user.Nickname,
        LoginName = user.LoginName,
        Email = user.Email,
        CreatedAt = user.CreatedAt,
        ProfileUpdatedAt = user.ProfileUpdatedAt,
        FontSize = user.FontSize,
        MoonServerIp = user.MoonServerIp,
        ZeroTierNetworkId = user.ZeroTierNetworkId,
        ZeroTierConnectionMode = user.ZeroTierConnectionMode,
        BaseTheme = user.BaseTheme,
        ColorTheme = user.ColorTheme
    };
}
