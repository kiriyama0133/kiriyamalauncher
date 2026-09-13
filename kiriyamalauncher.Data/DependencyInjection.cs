using Microsoft.Extensions.DependencyInjection;
using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;

namespace kiriyamalauncher.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Adds application file-system, database, and web data access services to the <see cref="IServiceCollection"/>. 
    /// Services include <see cref="IFileSystemAccess"/>, <see cref="ISQLDataAccess"/>, and <see cref="IHttpRequester"/>.
    /// Also adds the <see cref="IHttpClientFactory"/> and configures a named <see cref="HttpClient"/> used for compression.
    /// </summary>
    /// <returns>A reference to this instance after the operation has completed.</returns>
    public static IServiceCollection AddDataAccessServices(this IServiceCollection services)
    {
        services.AddScoped<IFileSystemAccess, FileSystemAccess>()
            .AddDatabaseAccess()
            .AddWebAccess()
            .AddScoped<IGameDataAccess, GameDataAccess>()
            .AddSingleton<IUserRepository, UserRepository>()
            .AddSingleton<IZeroTierService, ZeroTierService>()
            .AddSingleton<IFirewallService>(CreateFirewallService);

        return services;
    }

    /// <summary>
    /// 防火墙实现按平台选择：Windows 用系统自带的 netsh，其它平台只给出手动放行提示。
    /// </summary>
    private static IFirewallService CreateFirewallService(IServiceProvider serviceProvider)
        => OperatingSystem.IsWindows()
            ? new WindowsFirewallService(serviceProvider.GetRequiredService<ILogger<WindowsFirewallService>>())
            : new UnsupportedFirewallService();

    private static IServiceCollection AddDatabaseAccess(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>()
            .AddSingleton<ISQLDataAccess, DapperSQLiteDataAccess>();

        return services;
    }

    private static IServiceCollection AddWebAccess(this IServiceCollection services)
    {
        services.AddHttpClient(HttpRequester.COMPRESSION_CLIENT_NAME, c => { c.DefaultRequestHeaders.Add("Accept-Encoding", "deflate, gzip"); })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
            });

        services.AddScoped<IHttpRequester, HttpRequester>();

        return services;
    }
}
