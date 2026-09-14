using System.Text.Json;
using System.Text.Json.Serialization;
using FeishuWss.Events.Models;

namespace FeishuWss.Events;

/// <summary>
/// 事件相关类型的 System.Text.Json 源生成上下文。
/// 同时定义枚举默认序列化配置（驼峰命名已对齐飞书）。
/// </summary>
[JsonSerializable(typeof(EventEnvelope))]
[JsonSerializable(typeof(P2MessageReceiveV1))]
public partial class EventJsonContext : JsonSerializerContext
{
    private static JsonSerializerOptions? _serializerOptions;

    public static JsonSerializerOptions SerializerOptions => _serializerOptions ??= new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
