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
/// <param name="IsHost">该玩家是不是当前房主（界面据此显示房主标识）。</param>
public sealed record RelayPlayer(
    Guid PlayerId,
    string Nickname,
    string NodeId,
    string VirtualIp,
    bool IsHost);

/// <summary>房间详情（房间名 + 成员列表）。</summary>
/// <param name="RoomId">房间标识。</param>
/// <param name="RoomName">房间名。</param>
/// <param name="HostNodeId">房主节点 ID（客户端据此判断自己是不是房主）。</param>
/// <param name="Players">房间内玩家。</param>
public sealed record RelayRoomPlayers(
    string RoomId,
    string RoomName,
    string HostNodeId,
    IReadOnlyList<RelayPlayer> Players);

/// <summary>
/// 一条房间实时事件（服务端经 SSE 推送）。
/// 房间销毁、房主变更这类「别人触发的状态变化」靠它即时送达，无需等下一次轮询。
/// </summary>
/// <param name="Type">事件类型，取值见 <see cref="RelayRoomEventTypes"/>。</param>
/// <param name="Reason">发生原因（房间解散时给用户看的说明）。</param>
/// <param name="HostName">新房主昵称（房主变更时有效）。</param>
/// <param name="HostNodeId">新房主节点 ID（房主变更时有效）。</param>
public sealed record RelayRoomEvent(
    string Type,
    string? Reason,
    string? HostName,
    string? HostNodeId);

/// <summary>房间实时事件的类型名（与服务端 RoomEventTypes 约定一致）。</summary>
public static class RelayRoomEventTypes
{
    /// <summary>房间已被销毁（房主退出或房间空了）：客户端应立即退出房间页面。</summary>
    public const string RoomClosed = "room_closed";

    /// <summary>房主已变更：刷新房主标识与转让入口的可见性。</summary>
    public const string HostChanged = "host_changed";
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

