using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace kiriyamalauncher.Data;

/// <summary>
/// <see cref="IRelayServerClient"/> 的 HTTP 实现（用 <see cref="IHttpClientFactory"/> 的默认客户端）。
/// JSON 走 <see cref="RelayJsonContext"/> 源生成：NativeAOT 发布下反射序列化被禁用，不能再用反射序列化器。
/// </summary>
public class HttpRelayServerClient : IRelayServerClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpRelayServerClient> _logger;

    public HttpRelayServerClient(IHttpClientFactory httpClientFactory, ILogger<HttpRelayServerClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelayGame>> ListGamesAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync($"{baseUrl}/api/games", cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        try
        {
            List<RelayGameDto>? dtos = JsonSerializer.Deserialize(json, RelayJsonContext.Default.ListRelayGameDto);
            return dtos is null ? [] : dtos.ConvertAll(ToGame);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "解析游戏列表失败：{Json}", json);
            throw new RelayServerException("服务器返回了无法识别的游戏数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelayRoom>> ListRoomsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync($"{baseUrl}/api/rooms", cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        try
        {
            List<RelayRoomDto>? dtos = JsonSerializer.Deserialize(json, RelayJsonContext.Default.ListRelayRoomDto);
            return dtos is null ? [] : dtos.ConvertAll(ToRoom);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "解析房间列表失败：{Json}", json);
            throw new RelayServerException("服务器返回了无法识别的房间数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelayRoom>> ListRoomsAsync(string baseUrl, string gameKey, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        string url = $"{baseUrl}/api/rooms?game={Uri.EscapeDataString(gameKey)}";
        using HttpResponseMessage response = await client
            .GetAsync(url, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        try
        {
            List<RelayRoomDto>? dtos = JsonSerializer.Deserialize(json, RelayJsonContext.Default.ListRelayRoomDto);
            return dtos is null ? [] : dtos.ConvertAll(ToRoom);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "解析房间列表失败：{Json}", json);
            throw new RelayServerException("服务器返回了无法识别的房间数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<RelayRoom> CreateRoomAsync(string baseUrl, string name, string hostName, string gameKey, string? password, int maxPlayers = 0, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using StringContent content = BuildJson(
            new CreateRoomRequestDto { Name = name, HostName = hostName, Game = gameKey, Password = password, MaxPlayers = maxPlayers },
            RelayJsonContext.Default.CreateRoomRequestDto);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/rooms", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, json);

        try
        {
            RelayRoomDto? dto = JsonSerializer.Deserialize(json, RelayJsonContext.Default.RelayRoomDto);
            return dto is null
                ? throw new RelayServerException("服务器没有返回创建结果。")
                : ToRoom(dto);
        }
        catch (JsonException ex)
        {
            throw new RelayServerException("服务器返回了无法识别的房间数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task JoinRoomAsync(string baseUrl, string roomId, string nickname, string nodeId, string virtualIp, string? password, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using StringContent content = BuildJson(
            new JoinRoomRequestDto { Nickname = nickname, NodeId = nodeId, VirtualIp = virtualIp, Password = password },
            RelayJsonContext.Default.JoinRoomRequestDto);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/rooms/{Uri.EscapeDataString(roomId)}/join", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 401:
                throw new RelayServerException("房间密码错误。");
            case 404:
                throw new RelayServerException("房间不存在或已关闭。");
            case 409:
                throw new RelayServerException("房间已满，无法加入。");
        }

        EnsureSuccess(response, json);
    }

    /// <inheritdoc />
    public async Task LeaveRoomAsync(string baseUrl, string roomId, string nodeId, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using StringContent content = BuildJson(
            new LeaveRoomRequestDto { NodeId = nodeId },
            RelayJsonContext.Default.LeaveRoomRequestDto);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/rooms/{Uri.EscapeDataString(roomId)}/leave", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 404:
                throw new RelayServerException("房间不存在或已关闭。");
        }

        EnsureSuccess(response, json);
    }

    /// <inheritdoc />
    public async Task<RelayRoomPlayers> ListPlayersAsync(string baseUrl, string roomId, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using HttpResponseMessage response = await client
            .GetAsync($"{baseUrl}/api/rooms/{Uri.EscapeDataString(roomId)}/players", cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 404:
                throw new RelayServerException("房间不存在或已关闭。");
        }

        EnsureSuccess(response, json);

        try
        {
            RelayRoomPlayersDto? dto = JsonSerializer.Deserialize(json, RelayJsonContext.Default.RelayRoomPlayersDto);
            if (dto is null)
            {
                throw new RelayServerException("服务器没有返回房间成员数据。");
            }

            IReadOnlyList<RelayPlayer> players = (dto.Players ?? [])
                .Select(p => new RelayPlayer(
                    p.PlayerId,
                    p.Nickname ?? string.Empty,
                    p.NodeId ?? string.Empty,
                    p.VirtualIp ?? string.Empty))
                .ToList();

            return new RelayRoomPlayers(dto.RoomId ?? string.Empty, dto.RoomName ?? string.Empty, players);
        }
        catch (JsonException ex)
        {
            throw new RelayServerException("服务器返回了无法识别的房间成员数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<RelayRegisteredAccount> RegisterAsync(string baseUrl, string email, string password, string? displayName, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using StringContent content = BuildJson(
            new RegisterRequestDto { Email = email, Password = password, DisplayName = displayName },
            RelayJsonContext.Default.RegisterRequestDto);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/auth/register", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        // 邮箱已被注册（409）：给用户能看懂的提示，而不是原始 JSON。
        if ((int)response.StatusCode == 409)
        {
            throw new RelayServerException("该邮箱已被注册，请直接登录或换一个邮箱。");
        }

        EnsureSuccess(response, json);

        try
        {
            RegisterDto? dto = JsonSerializer.Deserialize(json, RelayJsonContext.Default.RegisterDto);
            return dto is null
                ? throw new RelayServerException("服务器没有返回注册结果。")
                : new RelayRegisteredAccount(
                    dto.UserId ?? string.Empty,
                    dto.Email ?? string.Empty,
                    dto.DisplayName ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new RelayServerException("服务器返回了无法识别的注册数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<RelayAuthorizationCode> LoginAsync(string baseUrl, string email, string password, string codeChallenge, CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateClient();
        using StringContent content = BuildJson(
            new LoginRequestDto { Email = email, Password = password, CodeChallenge = codeChallenge },
            RelayJsonContext.Default.LoginRequestDto);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/auth/login", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 401:
                throw new RelayServerException("邮箱或密码错误。");
        }

        EnsureSuccess(response, json);

        try
        {
            LoginDto? dto = JsonSerializer.Deserialize(json, RelayJsonContext.Default.LoginDto);
            return dto is null || string.IsNullOrEmpty(dto.Code)
                ? throw new RelayServerException("服务器没有返回授权码。")
                : new RelayAuthorizationCode(dto.Code, dto.ExpiresAt, dto.DisplayName ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new RelayServerException("服务器返回了无法识别的授权码数据。", ex);
        }
    }

    /// <inheritdoc />
    public async Task<RelayTokenSet> ExchangeCodeAsync(string baseUrl, string code, string codeVerifier, string clientId, CancellationToken cancellationToken = default)
        => await TokenAsync(baseUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = clientId,
        }, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<RelayTokenSet> RefreshAsync(string baseUrl, string refreshToken, string clientId, CancellationToken cancellationToken = default)
        => await TokenAsync(baseUrl, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, cancellationToken).ConfigureAwait(false);

    private async Task<RelayTokenSet> TokenAsync(string baseUrl, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateClient();
        using var content = new FormUrlEncodedContent(form);

        using HttpResponseMessage response = await client
            .PostAsync($"{baseUrl}/api/auth/token", content, cancellationToken)
            .ConfigureAwait(false);

        string json = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 400:
                throw new RelayServerException("令牌兑换失败：授权码无效或已过期。");
        }

        EnsureSuccess(response, json);

        try
        {
            TokenDto? dto = JsonSerializer.Deserialize(json, RelayJsonContext.Default.TokenDto);
            return dto is null || string.IsNullOrEmpty(dto.AccessToken)
                ? throw new RelayServerException("服务器没有返回访问令牌。")
                : new RelayTokenSet(
                    dto.AccessToken,
                    dto.TokenType ?? "Bearer",
                    dto.ExpiresIn,
                    dto.RefreshToken ?? string.Empty);
        }
        catch (JsonException ex)
        {
            throw new RelayServerException("服务器返回了无法识别的令牌数据。", ex);
        }
    }

    private HttpClient CreateClient()
    {
        HttpClient client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private static StringContent BuildJson<T>(T payload, JsonTypeInfo<T> typeInfo)
        => new(JsonSerializer.Serialize(payload, typeInfo), Encoding.UTF8, "application/json");

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>非成功状态码统一转成 <see cref="RelayServerException"/>（带可读原因）。</summary>
    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message = (int)response.StatusCode switch
        {
            400 => "请求参数有误。",
            404 => "接口不存在（请检查服务器版本）。",
            >= 500 => "中继服务器内部错误。",
            _ => $"服务器返回错误（HTTP {(int)response.StatusCode}）。"
        };

        // 服务端错误体通常是 ProblemDetails（RFC 7807）：提取 detail 作为可读原因，
        // 避免把整段原始 JSON 拼进提示里。
        string detail = ExtractProblemDetail(body);

        if (detail.Length > 0)
        {
            message += $" {detail}";
        }

        throw new RelayServerException(message);
    }

    /// <summary>从错误响应体里提取 ProblemDetails 的 detail 字段；不是 JSON 或没有 detail 时返回空串。</summary>
    private static string ExtractProblemDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        string trimmed = body.Trim();

        if (!trimmed.StartsWith('{'))
        {
            return trimmed.Length < 200 ? trimmed : string.Empty;
        }

        try
        {
            ProblemDetailsDto? problem = JsonSerializer.Deserialize(trimmed, RelayJsonContext.Default.ProblemDetailsDto);
            return problem?.Detail ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static RelayRoom ToRoom(RelayRoomDto dto) => new(
        dto.Id ?? string.Empty,
        dto.Name ?? string.Empty,
        dto.Host ?? string.Empty,
        dto.GameKey ?? string.Empty,
        dto.PlayerCount,
        dto.MaxPlayers,
        dto.HasPassword);

    private static RelayGame ToGame(RelayGameDto dto) => new(
        dto.Key ?? string.Empty,
        dto.DisplayName ?? string.Empty);
}
