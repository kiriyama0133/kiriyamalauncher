using kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;
using kiriyamalauncher.Business.Modules.GameSession.DTOs;
using kiriyamalauncher.Presentation.Base.Services.Dialogs;
using kiriyamalauncher.Presentation.Base.Services.Notifications;
using Microsoft.Extensions.Logging;

namespace kiriyamalauncher.Presentation.ViewModels;

/// <summary>
/// 创建 <see cref="GameIntegrationViewModel"/> 的工厂：它需要运行时参数（哪个游戏），所以用工厂而不是直接解析。
/// </summary>
public interface IGameIntegrationViewModelFactory
{
    /// <summary>按游戏集成信息创建一个联机面板 ViewModel。</summary>
    GameIntegrationViewModel Create(GameIntegrationInfoDto integration);
}

/// <inheritdoc />
public class GameIntegrationViewModelFactory : IGameIntegrationViewModelFactory
{
    private readonly IGameLaunchService _launchService;
    private readonly IFilePickerService _filePickerService;
    private readonly IAppNotifier _notifier;
    private readonly ILogger<GameIntegrationViewModel> _logger;

    public GameIntegrationViewModelFactory(
        IGameLaunchService launchService,
        IFilePickerService filePickerService,
        IAppNotifier notifier,
        ILogger<GameIntegrationViewModel> logger)
    {
        _launchService = launchService;
        _filePickerService = filePickerService;
        _notifier = notifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public GameIntegrationViewModel Create(GameIntegrationInfoDto integration)
        => new(_launchService, _filePickerService, _notifier, _logger, integration);
}
