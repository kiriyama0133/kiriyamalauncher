using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 用户账号服务（登录 / 注册 / 昵称等），不涉及界面偏好。
/// </summary>
public interface IUserService
{
    /// <summary>取当前账号（没有就创建一条空账号）。</summary>
    Task<UserDto> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>按登录名或邮箱查账号。</summary>
    Task<UserDto?> FindByLoginAsync(string loginNameOrEmail, CancellationToken cancellationToken = default);

    /// <summary>保存账号信息。</summary>
    Task SaveAsync(UserDto user, CancellationToken cancellationToken = default);
}
