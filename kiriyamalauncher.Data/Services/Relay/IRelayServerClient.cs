using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// 中继服务器（服务端联机）客户端：房间大厅与账号鉴权的 HTTP REST 协议封装。
///
/// 协议约定（服务器端按此实现即可）：
///   - GET  {base}/api/games             → 游戏板块列表 JSON 数组
///   - GET  {base}/api/rooms             → 全部房间列表 JSON 数组
///   - GET  {base}/api/rooms?game={key}  → 按游戏板块过滤的房间列表
///   - POST {base}/api/rooms             → 创建房间（body: name / game / password）
///   - POST {base}/api/rooms/{id}/join   → 加入房间（body: password）
///
/// 鉴权（PKCE + OAuth）：
///   - POST {base}/api/auth/register     → 注册（body: email / password / displayName）
///   - POST {base}/api/auth/login        → 登录，返回一次性授权码（body: email / password / codeChallenge）
///   - POST {base}/api/auth/token        → 换令牌（form: grant_type / code / code_verifier / refresh_token / client_id）
///
/// 房间 JSON 字段（camelCase）：
///   id, name, host, gameKey, playerCount, maxPlayers, hasPassword
/// 游戏 JSON 字段（camelCase）：
///   key, displayName
/// </summary>
public interface IRelayServerClient
{
    /// <summary>列出服务器上的全部游戏板块（只读列表）。</summary>
    Task<IReadOnlyList<RelayGame>> ListGamesAsync(string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>列出服务器上的全部房间。</summary>
    Task<IReadOnlyList<RelayRoom>> ListRoomsAsync(string baseUrl, CancellationToken cancellationToken = default);

    /// <summary>按游戏板块列出房间（gameKey 如 civ6）。</summary>
    Task<IReadOnlyList<RelayRoom>> ListRoomsAsync(string baseUrl, string gameKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建一个房间（gameKey 指定游戏板块，password 为空表示无密码，maxPlayers 为 0 表示不限）。
    /// hostNodeId 是房主的 ZeroTier 节点 ID：服务端据此认定房主，房主退出时解散房间。
    /// </summary>
    Task<RelayRoom> CreateRoomAsync(string baseUrl, string name, string hostName, string? hostNodeId, string gameKey, string? password, int maxPlayers = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// 加入房间；服务端会给节点打房间 Tag。密码错误（401）/ 房间不存在（404）/ 已满（409）
    /// 会抛 <see cref="RelayServerException"/>。nodeId/virtualIp 取自本机 ZeroTier 状态。
    /// </summary>
    Task JoinRoomAsync(string baseUrl, string roomId, string nickname, string nodeId, string virtualIp, string? password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 离开房间；服务端清除该节点的房间 Tag。
    /// 房主离开会让整个房间解散（房内其余成员会被服务端请出）。
    /// 房间不存在（404）会抛 <see cref="RelayServerException"/>。
    /// </summary>
    Task LeaveRoomAsync(string baseUrl, string roomId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 转让房主：仅当前房主可发起（requesterNodeId 必须确实是房主），目标必须已在房内。
    /// 无权转让（403）/ 目标非法（400）/ 房间不存在（404）会抛 <see cref="RelayServerException"/>。
    /// </summary>
    Task TransferHostAsync(string baseUrl, string roomId, string requesterNodeId, string targetNodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出房间内的玩家（供房间页面展示成员并做延迟探测）。房间不存在（404）会抛 <see cref="RelayServerException"/>。
    /// </summary>
    Task<RelayRoomPlayers> ListPlayersAsync(string baseUrl, string roomId, CancellationToken cancellationToken = default);

    /// <summary>注册新账户；邮箱已被占用会抛 <see cref="RelayServerException"/>。</summary>
    Task<RelayRegisteredAccount> RegisterAsync(string baseUrl, string email, string password, string? displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登录第一步：校验密码，返回一次性授权码（PKCE）。
    /// 密码错误 / 账户不存在会抛 <see cref="RelayServerException"/>。
    /// </summary>
    Task<RelayAuthorizationCode> LoginAsync(string baseUrl, string email, string password, string codeChallenge, CancellationToken cancellationToken = default);

    /// <summary>
    /// 用授权码换令牌（authorization_code grant，PKCE）。
    /// </summary>
    Task<RelayTokenSet> ExchangeCodeAsync(string baseUrl, string code, string codeVerifier, string clientId, CancellationToken cancellationToken = default);

    /// <summary>用刷新令牌换新令牌（refresh_token grant，会轮换刷新令牌）。</summary>
    Task<RelayTokenSet> RefreshAsync(string baseUrl, string refreshToken, string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅房间事件流（SSE）。服务端推送的事件逐个交给 <paramref name="onEvent"/>，
    /// 直到 <paramref name="cancellationToken"/> 取消（正常结束）或连接中断（抛 <see cref="RelayServerException"/>）。
    ///
    /// 房主退出导致房间解散时服务端会推 room_closed，客户端据此自动退出房间页面。
    /// </summary>
    Task SubscribeRoomEventsAsync(string baseUrl, string roomId, Func<RelayRoomEvent, Task> onEvent, CancellationToken cancellationToken = default);
}
