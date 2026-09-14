using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 一个游戏的联机集成：需要监视哪些进程、随应用发行哪些组件、怎么注入 / 卸载。
///
/// 加新游戏时只需要新写一个实现并在 DI 里注册（游戏集成注册表会自动收集），
/// 进程监视、工具运行、界面控件都不用改。
/// </summary>
public interface IGameIntegration
{
    /// <summary>集成标识，与游戏数据里的 IntegrationId 对应（例如 civ6）。</summary>
    string Id { get; }

    /// <summary>给界面显示的名字。</summary>
    string DisplayName { get; }

    /// <summary>用于自动检测的进程名（不带 .exe，例如 CivilizationVI_DX12）。</summary>
    IReadOnlyList<string> ProcessNames { get; }

    /// <summary>选择启动路径时的默认文件名提示（例如 CivilizationVI_DX12.exe）。</summary>
    string ExecutableFileNameHint { get; }

    /// <summary>随应用一起发行的组件文件名（相对游戏组件目录）。</summary>
    IReadOnlyList<string> ResourceFiles { get; }

    /// <summary>把组件注入到游戏进程里。</summary>
    Task<GameToolResult> InjectAsync(GameProcessInfo process, CancellationToken cancellationToken = default);

    /// <summary>把组件从游戏进程里卸载。</summary>
    Task<GameToolResult> RemoveInjectionAsync(GameProcessInfo process, CancellationToken cancellationToken = default);
}
