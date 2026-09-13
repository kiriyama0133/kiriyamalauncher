using System.Globalization;

namespace kiriyamalauncher.Data.Entities;

/// <summary>
/// ZeroTier 相关的常量与默认值。
/// </summary>
public static class ZeroTierSettings
{
    /// <summary>连接模式：使用 my.zerotier.com 官方控制器。</summary>
    public const string OfficialController = "Official";

    /// <summary>连接模式：使用自建控制器（自己跑 zerotier-one 的 controller）。</summary>
    public const string SelfHostedController = "SelfHosted";

    /// <summary>默认 ZeroTier 网络 ID（my.zerotier.com 上创建的网络）。</summary>
    public const string DefaultNetworkId = "633e31d8a2724274";

    /// <summary>没有配置 moon 时使用的默认 moon 节点 ID。</summary>
    public const string DefaultMoonId = "1947244ee4";

    /// <summary>网络 ID 必须是 16 位十六进制。</summary>
    public static bool IsValidNetworkId(string? networkId)
        => networkId is { Length: 16 }
            && ulong.TryParse(networkId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
}
