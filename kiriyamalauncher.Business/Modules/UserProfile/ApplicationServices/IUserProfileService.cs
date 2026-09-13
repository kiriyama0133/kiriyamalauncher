using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 用户资料与偏好设置服务。
/// </summary>
public interface IUserProfileService
{
    /// <summary>读取当前用户资料；没有就创建一条默认记录。</summary>
    Task<UserProfileDto> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>保存用户资料与偏好设置。</summary>
    Task SaveAsync(UserProfileDto profile, CancellationToken cancellationToken = default);
}
