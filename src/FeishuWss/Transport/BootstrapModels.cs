using System.Text.Json.Serialization;

namespace FeishuWss.Transport;

/// <summary>
/// HTTP /callback/ws/endpoint 请求体。
/// </summary>
public sealed class BootstrapRequest
{
    [JsonPropertyName("AppID")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("AppSecret")]
    public string AppSecret { get; set; } = string.Empty;

    [JsonPropertyName("ClientAssertion")]
    public string ClientAssertion { get; set; } = string.Empty;
}

/// <summary>
/// HTTP /callback/ws/endpoint 响应体。
/// </summary>
public sealed class BootstrapResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string Msg { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public BootstrapData? Data { get; set; }
}

public sealed class BootstrapData
{
    [JsonPropertyName("URL")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("ClientConfig")]
    public WssClientConfig? ClientConfig { get; set; }
}

/// <summary>
/// 服务端下发的客户端配置。重连次数 / 间隔 / 抖动 / ping 间隔都在这里。
/// </summary>
public sealed class WssClientConfig
{
    [JsonPropertyName("ReconnectCount")]
    public int ReconnectCount { get; set; } = -1;

    [JsonPropertyName("ReconnectInterval")]
    public int ReconnectIntervalSeconds { get; set; } = 120;

    [JsonPropertyName("ReconnectNonce")]
    public int ReconnectNonce { get; set; } = 30;

    [JsonPropertyName("PingInterval")]
    public int PingIntervalSeconds { get; set; } = 120;
}

/// <summary>
/// client_assertion 提供方，产出 JWT 形式的 token 串。
/// 与 oapi-sdk-go 里 ClientAssertionProvider 一致。
/// </summary>
public interface IClientAssertionProvider
{
    Task<ClientAssertionToken> RetrieveTokenAsync(string aud, CancellationToken ct);
}

public sealed class ClientAssertionToken
{
    public string Value { get; set; } = string.Empty;
    public ClientAssertionTarget? TargetInfo { get; set; }
}

public sealed class ClientAssertionTarget
{
    public string TargetService { get; set; } = string.Empty;
    public string TargetPrefix { get; set; } = string.Empty;
}
