using kiriyamalauncher.Data.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 界面偏好仓储：独立的 Preferences 表（整机一份），不依赖用户账号。
/// </summary>
public interface IUserPreferencesRepository
{
    /// <summary>取偏好；没有就创建一份默认偏好。</summary>
    Task<UserPreferences> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>写入偏好：有 Id 就更新，没有就插入。</summary>
    Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default);
}
