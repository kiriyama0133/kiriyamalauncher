using kiriyamalauncher.Business.Modules.UserProfile.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;

/// <summary>
/// 界面偏好服务（字号、主题、ZeroTier 配置等）。
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>取偏好（没有就创建默认偏好）。</summary>
    Task<UserPreferencesDto> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>保存偏好。</summary>
    Task SaveAsync(UserPreferencesDto preferences, CancellationToken cancellationToken = default);
}
