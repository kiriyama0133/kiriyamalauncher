using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;

/// <summary>
/// 游戏启动与联机组件注入的服务。
///
/// 与具体游戏无关：游戏相关的知识（进程名、随应用的组件、注入方式）都在
/// <c>IGameIntegration</c> 实现里，这里只负责「保存路径 → 启动 → 监视进程 → 注入」这套流程。
/// </summary>
public interface IGameLaunchService
{
    /// <summary>状态变化（进程检测、注入结果等）。可能在后台线程触发，界面层需要自己切回 UI 线程。</summary>
    event EventHandler<GameIntegrationStatusDto>? StatusChanged;

    /// <summary>是否开启「检测到游戏进程就自动注入」。</summary>
    bool IsAutoInjectEnabled { get; }

    /// <summary>取已保存的游戏启动路径。</summary>
    Task<string?> GetExecutablePathAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>保存游戏启动路径。</summary>
    Task SetExecutablePathAsync(string integrationId, string executablePath, CancellationToken cancellationToken = default);

    /// <summary>开关自动注入。</summary>
    void SetAutoInjectEnabled(bool enabled);

    /// <summary>进入某个游戏板块时调用：开始监视这个游戏的进程。</summary>
    void BeginMonitoring(string? integrationId);

    /// <summary>离开板块时调用：停止监视（不会卸载已经注入的组件）。</summary>
    void StopMonitoring();

    /// <summary>
    /// 开启常驻自动注入：不再要求停留在游戏板块，
    /// 只要被监视的游戏进程出现（且自动注入是开的）就注入。
    /// 一般在本机接入虚拟局域网之后调用。
    /// </summary>
    void StartAutoInject();

    /// <summary>停止常驻自动注入（已经注入的组件不受影响）。</summary>
    void StopAutoInject();

    /// <summary>读取当前状态（界面初始化时用）。</summary>
    GameIntegrationStatusDto GetStatus(string integrationId);

    /// <summary>取某个游戏的联机集成信息；这个游戏没有集成时返回 null。</summary>
    GameIntegrationInfoDto? FindIntegration(string? integrationId);

    /// <summary>按保存的路径启动游戏，之后进程出现时会自动注入。</summary>
    Task<GameOperationResultDto> LaunchAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>对当前正在运行的游戏进程立即注入一次。</summary>
    Task<GameOperationResultDto> InjectNowAsync(string integrationId, CancellationToken cancellationToken = default);

    /// <summary>把组件从正在运行的游戏进程里卸载。</summary>
    Task<GameOperationResultDto> RemoveInjectionAsync(string integrationId, CancellationToken cancellationToken = default);
}
