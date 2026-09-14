namespace kiriyamalauncher.Business.Modules.GameSession.DTOs;

/// <summary>
/// 一次游戏操作（启动 / 注入 / 卸载）的结果。
/// </summary>
public class GameOperationResultDto
{
    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; init; }

    /// <summary>结果说明。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>成功结果。</summary>
    public static GameOperationResultDto Ok(string message) => new() { IsSuccess = true, Message = message };

    /// <summary>失败结果。</summary>
    public static GameOperationResultDto Fail(string message) => new() { IsSuccess = false, Message = message };
}
