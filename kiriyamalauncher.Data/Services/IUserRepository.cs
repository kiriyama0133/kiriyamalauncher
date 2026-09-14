using kiriyamalauncher.Data.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 用户账号仓储：只负责 Users 表，与界面偏好完全无关（偏好见 <see cref="IUserPreferencesRepository"/>）。
/// </summary>
public interface IUserRepository
{
    /// <summary>取当前账号；一条都没有时创建一条空账号（未登录状态）。</summary>
    Task<User> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>按登录名或邮箱查找账号（登录时用）。</summary>
    Task<User?> FindByLoginAsync(string loginNameOrEmail, CancellationToken cancellationToken = default);

    /// <summary>写入账号：有 Id 就更新，没有就插入。</summary>
    Task SaveAsync(User user, CancellationToken cancellationToken = default);
}
