using System.Text.Json.Serialization;

namespace FeishuWss.Events.Models;

/// <summary>
/// v2 schema 事件 envelope。飞书 server 推送上来的 payload 最外层结构。
/// </summary>
public sealed class EventEnvelope
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = string.Empty;

    [JsonPropertyName("header")]
    public EventHeader Header { get; set; } = new();

    [JsonPropertyName("event")]
    public System.Text.Json.JsonElement Event { get; set; }
}

public sealed class EventHeader
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("create_time")]
    public string CreateTime { get; set; } = string.Empty;

    [JsonPropertyName("app_id")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("tenant_key")]
    public string TenantKey { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}
