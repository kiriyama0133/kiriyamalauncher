using Microsoft.Extensions.DependencyInjection;
using RunnethOverStudio.AppToolkit.Modules.DataAccess;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;

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
            // 存储层：账号表与偏好表各自独立（偏好不依赖账号）。
            .AddSingleton<SqliteDatabase>()
            .AddSingleton<IUserRepository, UserRepository>()
            .AddSingleton<IUserPreferencesRepository, UserPreferencesRepository>()
            .AddSingleton<IGameLaunchPathRepository, GameLaunchPathRepository>()
            // 游戏联机集成：加新游戏时只要再注册一个 IGameIntegration，其它层不用改。
            .AddSingleton<GameResourceLocator>()
            .AddSingleton<IGameToolRunner, GameToolRunner>()
            .AddSingleton<IGameProcessMonitor, GameProcessMonitor>()
            .AddSingleton<IGameIntegration, Civilization6GameIntegration>()
            .AddSingleton<IGameIntegrationRegistry, GameIntegrationRegistry>()
            // 虚拟局域网设备发现（轮询虚拟网段）
            .AddSingleton<IVirtualLanDiscoveryService, VirtualLanDiscoveryService>()
            // 中继服务器（服务端联机）客户端：房间大厅
            .AddSingleton<IRelayServerClient, HttpRelayServerClient>()
            // 游戏隧道：Hook ↔ libzt
            .AddSingleton<IVirtualLanTunnel, VirtualLanTunnel>()
            // ZeroTier 传输后端：Sockets（libzt 内嵌节点）+ Client（官方客户端，走系统虚拟网卡）。
            // 工厂按用户偏好的传输引擎挑一个。
            .AddSingleton<IZeroTierBackend, ZeroTierService>()
            .AddSingleton<IZeroTierBackend, ZeroTierClientBackend>()
            .AddSingleton<IFirewallService>(CreateFirewallService)
            .AddSingleton<IRouteMetricService>(CreateRouteMetricService);

        // 中继服务器的 HTTP 客户端：连接池里的连接最长活 30 秒——服务器重启或
        // ZeroTier 通路重建后复用旧连接，会被「远程主机强迫关闭」（10054）。
        // 给连接设限期，让失效连接及时作废，而不是被下一次请求复用。
        services.AddHttpClient(HttpRelayServerClient.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromSeconds(30),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15)
            });

        // SSE 长连接专用客户端：不做连接回收。
        // 上面那个客户端的 30 秒连接限期是治 10054 的，但事件流是持续连接，
        // 限期会在传输途中把它掐断，所以事件流必须用这个不设限期的客户端。
        services.AddHttpClient(HttpRelayServerClient.SseClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan
            });

        return services;
    }

    /// <summary>
    /// 防火墙实现按平台选择：Windows 用系统自带的 netsh，其它平台只给出手动放行提示。
    /// </summary>
    private static IFirewallService CreateFirewallService(IServiceProvider serviceProvider)
        => OperatingSystem.IsWindows()
            ? new WindowsFirewallService(serviceProvider.GetRequiredService<ILogger<WindowsFirewallService>>())
            : new UnsupportedFirewallService();

    /// <summary>
    /// 网卡优先级（metric）实现按平台选择：Windows 用 PowerShell/netsh（按 ifIndex 下发并读回校验），
    /// Linux 用路由 metric（ip route，Linux 没有接口级 metric），macOS 用 ifconfig，
    /// 其它平台返回手动提示。三个实现都会在设好自己之后消除「并列最高优先级」（详见 RouteMetricPolicy）。
    /// </summary>
    private static IRouteMetricService CreateRouteMetricService(IServiceProvider serviceProvider)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRouteMetricService(serviceProvider.GetRequiredService<ILogger<WindowsRouteMetricService>>());
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxRouteMetricService(serviceProvider.GetRequiredService<ILogger<LinuxRouteMetricService>>());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacRouteMetricService(serviceProvider.GetRequiredService<ILogger<MacRouteMetricService>>());
        }

        return new UnsupportedRouteMetricService();
    }

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
