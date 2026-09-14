using System.Text.Json.Serialization;

namespace FeishuWss.Events.Models;

/// <summary>
/// im.message.receive_v1 事件载荷。
/// 完整字段参考飞书开放平台文档：https://open.feishu.cn/document/uAjLw4CM/ukTMukTMukTM/reference/im-v1/message/events/receive
/// 这里只列常用字段；通过 JsonExtensionData 保留原始 JSON，便于访问未列出字段。
/// </summary>
public sealed class P2MessageReceiveV1
{
    [JsonPropertyName("sender")]
    public MessageSender Sender { get; set; } = new();

    [JsonPropertyName("message")]
    public MessageBody Message { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}

public sealed class MessageSender
{
    [JsonPropertyName("sender_id")]
    public SenderId SenderId { get; set; } = new();

    [JsonPropertyName("sender_type")]
    public string SenderType { get; set; } = string.Empty;

    [JsonPropertyName("tenant_key")]
    public string TenantKey { get; set; } = string.Empty;
}

public sealed class SenderId
{
    [JsonPropertyName("union_id")]
    public string UnionId { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("open_id")]
    public string OpenId { get; set; } = string.Empty;
}

public sealed class MessageBody
{
    [JsonPropertyName("message_id")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("root_id")]
    public string RootId { get; set; } = string.Empty;

    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = string.Empty;

    [JsonPropertyName("chat_id")]
    public string ChatId { get; set; } = string.Empty;

    [JsonPropertyName("chat_type")]
    public string ChatType { get; set; } = string.Empty;

    [JsonPropertyName("message_type")]
    public string MessageType { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("create_time")]
    public string CreateTime { get; set; } = string.Empty;
}
