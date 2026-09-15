using System;

namespace kiriyamalauncher.Data;

/// <summary>中继服务器上的一个游戏房间。</summary>
/// <param name="Id">房间唯一标识。</param>
/// <param name="Name">房间名。</param>
/// <param name="HostName">房主昵称。</param>
/// <param name="GameKey">房间归属的游戏板块标识（如 civ6）。</param>
/// <param name="PlayerCount">当前玩家数。</param>
/// <param name="MaxPlayers">最大玩家数。</param>
/// <param name="HasPassword">是否设有进入密码。</param>
public sealed record RelayRoom(
    string Id,
    string Name,
    string HostName,
    string GameKey,
    int PlayerCount,
    int MaxPlayers,
    bool HasPassword)
{
    /// <summary>给界面显示的玩家数（如「2 / 4」）。</summary>
    public string PlayerCountText => MaxPlayers > 0 ? $"{PlayerCount} / {MaxPlayers}" : PlayerCount.ToString();
}

/// <summary>中继服务器上的一个游戏板块（只读列表项）。</summary>
/// <param name="Key">游戏稳定标识（如 civ6）。</param>
/// <param name="DisplayName">游戏显示名（如「文明 6」）。</param>
public sealed record RelayGame(string Key, string DisplayName);

/// <summary>注册结果（服务器返回的账户信息）。</summary>
/// <param name="UserId">账户唯一标识。</param>
/// <param name="Email">注册邮箱。</param>
/// <param name="DisplayName">显示名。</param>
public sealed record RelayRegisteredAccount(string UserId, string Email, string DisplayName);

/// <summary>登录第一步返回的一次性 PKCE 授权码。</summary>
/// <param name="Code">授权码（短时有效、一次性）。</param>
/// <param name="ExpiresAt">授权码过期时间。</param>
public sealed record RelayAuthorizationCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>OAuth token 端点返回的令牌集。</summary>
/// <param name="AccessToken">访问令牌（JWT）。</param>
/// <param name="TokenType">令牌类型（恒为 Bearer）。</param>
/// <param name="ExpiresIn">访问令牌有效期（秒）。</param>
/// <param name="RefreshToken">刷新令牌（明文，仅此一次）。</param>
public sealed record RelayTokenSet(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken);

/// <summary>中继服务器通信失败（连接失败 / 超时 / 服务器返回错误），携带给用户看的原因。</summary>
public sealed class RelayServerException : Exception
{
    public RelayServerException(string message) : base(message)
    {
    }

    public RelayServerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

