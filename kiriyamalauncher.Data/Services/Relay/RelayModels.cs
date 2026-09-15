using System;
using System.Collections.Generic;

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

/// <summary>房间内的一名玩家（服务端返回，客户端做延迟探测）。</summary>
/// <param name="PlayerId">玩家连接标识。</param>
/// <param name="Nickname">玩家昵称（界面只显示这个）。</param>
/// <param name="NodeId">玩家节点 ID。</param>
/// <param name="VirtualIp">玩家虚拟 IP（延迟探测目标）。</param>
public sealed record RelayPlayer(
    Guid PlayerId,
    string Nickname,
    string NodeId,
    string VirtualIp);

/// <summary>房间详情（房间名 + 成员列表）。</summary>
/// <param name="RoomId">房间标识。</param>
/// <param name="RoomName">房间名。</param>
/// <param name="Players">房间内玩家。</param>
public sealed record RelayRoomPlayers(
    string RoomId,
    string RoomName,
    IReadOnlyList<RelayPlayer> Players);

/// <summary>中继服务器上的一个游戏板块（只读列表项）。</summary>
/// <param name="Key">游戏稳定标识（如 civ6）。</param>
/// <param name="DisplayName">游戏显示名（如「文明 6」）。</param>
public sealed record RelayGame(string Key, string DisplayName);

/// <summary>注册结果（服务器返回的账户信息）。</summary>
/// <param name="UserId">账户唯一标识。</param>
/// <param name="Email">注册邮箱。</param>
/// <param name="DisplayName">显示名。</param>
public sealed record RelayRegisteredAccount(string UserId, string Email, string DisplayName);

/// <summary>登录第一步返回的一次性 PKCE 授权码（附带显示名，登录后立即显示名称）。</summary>
/// <param name="Code">授权码（短时有效、一次性）。</param>
/// <param name="ExpiresAt">授权码过期时间。</param>
/// <param name="DisplayName">用户显示名。</param>
public sealed record RelayAuthorizationCode(string Code, DateTimeOffset ExpiresAt, string DisplayName);

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

