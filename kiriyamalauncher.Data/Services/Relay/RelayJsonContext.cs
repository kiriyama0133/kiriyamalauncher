using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace kiriyamalauncher.Data;

/// <summary>协议里的房间 JSON 结构（camelCase）。</summary>
internal sealed class RelayRoomDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Host { get; set; }
    public string? GameKey { get; set; }
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public bool HasPassword { get; set; }
}

/// <summary>协议里的游戏板块 JSON 结构（camelCase）。</summary>
internal sealed class RelayGameDto
{
    public string? Key { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>创建房间请求体（camelCase）。</summary>
internal sealed class CreateRoomRequestDto
{
    public string Name { get; set; } = string.Empty;
    public string HostName { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public string? Password { get; set; }
    public int MaxPlayers { get; set; }
}

/// <summary>加入房间请求体（camelCase）。</summary>
internal sealed class JoinRoomRequestDto
{
    public string Nickname { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string VirtualIp { get; set; } = string.Empty;
    public string? Password { get; set; }
}

/// <summary>离开房间请求体（camelCase）。</summary>
internal sealed class LeaveRoomRequestDto
{
    public string NodeId { get; set; } = string.Empty;
}

/// <summary>服务端 ProblemDetails 错误体（RFC 7807，用于提取可读的错误描述）。</summary>
internal sealed class ProblemDetailsDto
{
    public string? Title { get; set; }
    public int? Status { get; set; }
    public string? Detail { get; set; }
}

/// <summary>协议里的注册响应 JSON 结构（camelCase）。</summary>
internal sealed class RegisterDto
{
    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>注册请求体（camelCase）。</summary>
internal sealed class RegisterRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

/// <summary>协议里的登录响应 JSON 结构（camelCase）。</summary>
internal sealed class LoginDto
{
    public string? Code { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? DisplayName { get; set; }
}

/// <summary>登录请求体（camelCase）。</summary>
internal sealed class LoginRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
}

/// <summary>协议里的令牌响应 JSON 结构（camelCase）。</summary>
internal sealed class TokenDto
{
    public string? AccessToken { get; set; }
    public string? TokenType { get; set; }
    public int ExpiresIn { get; set; }
    public string? RefreshToken { get; set; }
}

/// <summary>协议里的房间成员列表响应 JSON 结构（camelCase）。</summary>
internal sealed class RelayRoomPlayersDto
{
    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    public List<RelayPlayerDto>? Players { get; set; }
}

/// <summary>协议里的单个玩家 JSON 结构（camelCase）。</summary>
internal sealed class RelayPlayerDto
{
    public Guid PlayerId { get; set; }
    public string? Nickname { get; set; }
    public string? NodeId { get; set; }
    public string? VirtualIp { get; set; }
}

/// <summary>
/// 中继协议的 JSON 源生成上下文。
/// NativeAOT 发布下反射序列化被禁用（IsReflectionEnabledByDefault=false），
/// 必须用编译期生成的元数据做序列化/反序列化。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<RelayRoomDto>))]
[JsonSerializable(typeof(List<RelayGameDto>))]
[JsonSerializable(typeof(RelayRoomDto))]
[JsonSerializable(typeof(RelayGameDto))]
[JsonSerializable(typeof(CreateRoomRequestDto))]
[JsonSerializable(typeof(JoinRoomRequestDto))]
[JsonSerializable(typeof(LeaveRoomRequestDto))]
[JsonSerializable(typeof(ProblemDetailsDto))]
[JsonSerializable(typeof(RegisterRequestDto))]
[JsonSerializable(typeof(RegisterDto))]
[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(LoginDto))]
[JsonSerializable(typeof(TokenDto))]
[JsonSerializable(typeof(RelayRoomPlayersDto))]
[JsonSerializable(typeof(List<RelayPlayerDto>))]
[JsonSerializable(typeof(RelayPlayerDto))]
internal sealed partial class RelayJsonContext : JsonSerializerContext
{
}
