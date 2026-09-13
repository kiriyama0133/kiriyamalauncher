using kiriyamalauncher.Data.Entities;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 用户仓储：读写用户资料与偏好设置。
/// </summary>
public interface IUserRepository
{
    /// <summary>读取当前用户；没有就创建一条默认记录。</summary>
    Task<User> GetOrCreateCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>保存用户资料与偏好设置。</summary>
    Task SaveAsync(User user, CancellationToken cancellationToken = default);
}
