namespace FeishuWss.Errors;

/// <summary>
/// 飞书长连接错误基类。
/// </summary>
public class WssException : Exception
{
    public int Code { get; }

    public WssException(int code, string message) : base(message)
    {
        Code = code;
    }

    public WssException(int code, string message, Exception inner) : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>
/// 客户端错误（如 appSecret 缺失、token 空、auth 失败超过连接数）。不可重连。
/// </summary>
public sealed class WssClientError : WssException
{
    public WssClientError(int code, string message) : base(code, message) { }
}

/// <summary>
/// 服务端错误（如 endpoint 5xx、握手 514 等）。可重连。
/// </summary>
public sealed class WssServerError : WssException
{
    public WssServerError(int code, string message) : base(code, message) { }
}

/// <summary>
/// 重连耗尽。
/// </summary>
public sealed class WssReconnectExhaustedException : WssException
{
    public int Attempts { get; }
    public WssReconnectExhaustedException(int attempts)
        : base(-1, $"unable to connect to server after {attempts} retries")
    {
        Attempts = attempts;
    }
}

/// <summary>
/// 解析 / 编解码错误。
/// </summary>
public sealed class WssProtocolException : WssException
{
    public WssProtocolException(string message) : base(-2, message) { }
    public WssProtocolException(string message, Exception inner) : base(-2, message, inner) { }
}
