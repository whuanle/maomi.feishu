using System.Text.Json;
using FeishuWss.Events.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeishuWss.Events;

/// <summary>
/// 长连接事件分发器。
/// 镜像 oapi-sdk-go/event/dispatcher：典型用法
///   var dispatcher = new EventDispatcher()
///       .OnP2MessageReceiveV1(async (ctx, e) => { ... })
///       .OnCustomizedEvent("out_approval", async (ctx, raw) => { ... });
/// 内部按 event_type 路由；找不到时记日志并返回 null（不影响后续消息处理）。
/// </summary>
public sealed class EventDispatcher
{
    private readonly Dictionary<string, Func<EventContext, Task<object?>>> _typedHandlers = new(StringComparer.Ordinal);
    private Func<EventContext, Task<object?>>? _catchAllHandler;
    private readonly ILogger _logger;

    public EventDispatcher(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// 注册强类型事件处理器。同一 eventType 多次注册会被覆盖（保留最后一次）。
    /// </summary>
    public EventDispatcher On<T>(string eventType, Func<EventContext, T, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        ArgumentNullException.ThrowIfNull(handler);
        _typedHandlers[eventType] = async ctx =>
        {
            var envelope = ParseEnvelope(ctx.Payload);
            var body = envelope.Event.Deserialize<T>(EventJsonContext.SerializerOptions);
            if (body is null)
            {
                _logger.LogWarning("event body deserialization failed, eventType={EventType}", eventType);
                return null;
            }
            return await handler(ctx, body).ConfigureAwait(false);
        };
        return this;
    }

    /// <summary>
    /// 注册强类型事件处理器（无返回值）。
    /// </summary>
    public EventDispatcher On<T>(string eventType, Func<EventContext, T, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return On<T>(eventType, async (ctx, body) =>
        {
            await handler(ctx, body).ConfigureAwait(false);
            return null;
        });
    }

    /// <summary>
    /// 注册自定义（透传）事件。handler 拿到的是整个 envelope 的原始 JSON 字符串，
    /// 适合不确定 schema 的第三方事件或存量代码兼容。
    /// </summary>
    public EventDispatcher OnCustomizedEvent(string eventType, Func<EventContext, string, Task<object?>> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        ArgumentNullException.ThrowIfNull(handler);
        _typedHandlers[eventType] = ctx => handler(ctx, ctx.Payload);
        return this;
    }

    /// <summary>
    /// 注册兜底处理器。任意未注册的 event_type 都会走这里。返回 null 表示不响应。
    /// </summary>
    public EventDispatcher OnAny(Func<EventContext, Task<object?>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _catchAllHandler = handler;
        return this;
    }

    // 常用强类型快捷方法，命名风格与 Go SDK 一致（OnP2XxxV1）

    /// <summary>接收消息 v1 事件。</summary>
    public EventDispatcher OnP2MessageReceiveV1(Func<EventContext, P2MessageReceiveV1, Task<object?>> handler)
        => On(P2MessageReceiveV1Type, handler);

    /// <summary>接收消息 v1 事件（无返回值）。</summary>
    public EventDispatcher OnP2MessageReceiveV1(Func<EventContext, P2MessageReceiveV1, Task> handler)
        => On(P2MessageReceiveV1Type, handler);

    /// <summary>card.action.trigger 回传（卡片回调）。</summary>
    public EventDispatcher OnCardActionTrigger(Func<EventContext, System.Text.Json.JsonElement, Task<object?>> handler)
        => On<System.Text.Json.JsonElement>("card.action.trigger", (ctx, el) => handler(ctx, el));

    public const string P2MessageReceiveV1Type = "im.message.receive_v1";

    /// <summary>
    /// Dispatcher 入口：长连接收到一个事件 payload 后调用。
    /// 返回 handler 给的响应体（如果有），长连接会作为 ack 写回服务端。
    /// </summary>
    public async Task<object?> DoAsync(string payload, CancellationToken ct)
    {
        string eventType;
        try
        {
            var envelope = ParseEnvelope(payload);
            eventType = envelope.Header.EventType;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "event envelope parse failed");
            throw;
        }

        var ctx = new EventContext(payload, eventType, ct);

        if (_typedHandlers.TryGetValue(eventType, out var handler))
        {
            return await handler(ctx).ConfigureAwait(false);
        }

        if (_catchAllHandler is not null)
        {
            return await _catchAllHandler(ctx).ConfigureAwait(false);
        }

        _logger.LogWarning("no handler for event_type={EventType}", eventType);
        return null;
    }

    private static EventEnvelope ParseEnvelope(string payload)
    {
        var envelope = JsonSerializer.Deserialize(payload, EventJsonContext.Default.EventEnvelope);
        if (envelope is null)
            throw new InvalidDataException("event envelope is null");
        return envelope;
    }
}

/// <summary>
/// 事件上下文：payload 原文 + event_type + CancellationToken。
/// handler 内部可用 ct 取消业务处理。
/// </summary>
public sealed record EventContext(string Payload, string EventType, CancellationToken CancellationToken)
{
    /// <summary>
    /// 解析 envelope 的强类型 view，方便 handler 内部访问 header/tenant 等元数据。
    /// </summary>
    public EventEnvelope? TryParseEnvelope()
    {
        try
        {
            return JsonSerializer.Deserialize(Payload, EventJsonContext.Default.EventEnvelope);
        }
        catch
        {
            return null;
        }
    }
}
