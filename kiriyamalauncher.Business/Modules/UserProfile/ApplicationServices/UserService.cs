using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using kiriyamalauncher.Data;
using kiriyamalauncher.Data.Entities;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 用户账号服务：负责数据层实体与业务层 DTO 之间的转换。
/// </summary>
public class UserService : IUserService
{
    private readonly IUserRepository _userRepository;

    public UserService(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    /// <inheritdoc />
    public async Task<UserDto> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default)
    {
        User user = await _userRepository.GetOrCreateCurrentAsync(cancellationToken);
        return ToDto(user);
    }

    /// <inheritdoc />
    public async Task<UserDto?> FindByLoginAsync(string loginNameOrEmail, CancellationToken cancellationToken = default)
    {
        User? user = await _userRepository.FindByLoginAsync(loginNameOrEmail, cancellationToken);
        return user is null ? null : ToDto(user);
    }

    /// <inheritdoc />
    public async Task SaveAsync(UserDto user, CancellationToken cancellationToken = default)
    {
        User entity = new()
        {
            Id = user.Id,
            Nickname = user.Nickname,
            LoginName = user.LoginName,
            Email = user.Email,
            CreatedAt = user.CreatedAt,
            ProfileUpdatedAt = DateTime.Now,
            AccessToken = user.AccessToken,
            RefreshToken = user.RefreshToken,
            AccessTokenExpiresAt = user.AccessTokenExpiresAt
        };

        await _userRepository.SaveAsync(entity, cancellationToken);

        user.Id = entity.Id;
        user.ProfileUpdatedAt = entity.ProfileUpdatedAt;
    }

    private static UserDto ToDto(User user) => new()
    {
        Id = user.Id,
        Nickname = user.Nickname,
        LoginName = user.LoginName,
        Email = user.Email,
        CreatedAt = user.CreatedAt,
        ProfileUpdatedAt = user.ProfileUpdatedAt,
        AccessToken = user.AccessToken,
        RefreshToken = user.RefreshToken,
        AccessTokenExpiresAt = user.AccessTokenExpiresAt
    };
}
