namespace kiriyamalauncher.Data;

/// <summary>调用外部联机组件的执行结果。</summary>
/// <param name="IsSuccess">是否成功。</param>
/// <param name="Message">结果说明，可直接显示到界面 / 日志。</param>
public record GameToolResult(bool IsSuccess, string Message)
{
    /// <summary>成功结果。</summary>
    public static GameToolResult Ok(string message) => new(true, message);

    /// <summary>失败结果。</summary>
    public static GameToolResult Fail(string message) => new(false, message);
}
