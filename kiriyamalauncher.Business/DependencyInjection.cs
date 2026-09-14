using kiriyamalauncher.Business.Modules.Sample.ApplicationServices;
using kiriyamalauncher.Business.Modules.Sample.DomainServices;
using kiriyamalauncher.Business.Modules.GameSession.ApplicationServices;
using kiriyamalauncher.Business.Modules.UserProfile.ApplicationServices;
using kiriyamalauncher.Data;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RunnethOverStudio.AppToolkit.Modules.Messaging;
using System.Reflection;

namespace kiriyamalauncher.Business;

public static class DependencyInjection
{
    /// <summary>
    /// Adds business-tier services.
    /// Dependent on <see cref="ILogger"/>.
    /// </summary>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddBusinessServices(this IServiceCollection services)
    {
        // Infrastructure.
        services.AddSingleton<IEventSystem, EventSystem>()
            .AddDataAccessServices();

        // Internal business domain.
        services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly())
            .AddScoped<FlatUIColorPicker, FlatUIColorPicker>()
            .AddScoped<LineSorter, LineSorter>()
            .AddScoped<UUIDGenerator, UUIDGenerator>();

        // Orchestrated public-facing (application) services.
        services.AddScoped<ISampleToolsService, SampleToolsService>();

        // Game session（游戏联机）.
        services.AddScoped<IGameService, GameService>()
            .AddSingleton<IGameLaunchService, GameLaunchService>()
            .AddSingleton<ILanTunnelService, LanTunnelService>();

        // User profile（账号与界面偏好互相独立）.
        services.AddSingleton<IUserService, UserService>()
            .AddSingleton<IUserPreferencesService, UserPreferencesService>();

        return services;
    }
}
