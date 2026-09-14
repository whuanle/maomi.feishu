using System.Text;
using FeishuWss.Events;
using FeishuWss.Events.Models;
using Xunit;

namespace FeishuWss.Tests;

/// <summary>
/// EventDispatcher 路由测试。
/// 验证 event_type 解析 + 强类型反序列化 + 自定义事件 + 兜底。
/// </summary>
public class EventDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_TypedHandler_DeserializesBody()
    {
        var dispatcher = new EventDispatcher()
            .On<P2MessageReceiveV1>(EventDispatcher.P2MessageReceiveV1Type, (ctx, e) =>
            {
                Assert.Equal("om_test", e.Message.MessageId);
                Assert.Equal("user_open_id", e.Sender.SenderId.OpenId);
                return Task.FromResult<object?>(null);
            });

        var payload = """
        {
          "schema": "2.0",
          "header": {
            "event_id": "evt_1",
            "event_type": "im.message.receive_v1",
            "create_time": "1700000000",
            "app_id": "cli_xxx",
            "tenant_key": "t1"
          },
          "event": {
            "sender": { "sender_id": { "open_id": "user_open_id" }, "sender_type": "user" },
            "message": { "message_id": "om_test", "chat_id": "oc_chat", "chat_type": "p2p", "message_type": "text", "content": "{}" }
          }
        }
        """;

        var resp = await dispatcher.DoAsync(payload, CancellationToken.None);
        Assert.Null(resp);
    }

    [Fact]
    public async Task DispatchAsync_CustomizedHandler_RawPayload()
    {
        var dispatcher = new EventDispatcher()
            .OnCustomizedEvent("out_approval", (ctx, raw) =>
            {
                Assert.Contains("out_approval", raw);
                return Task.FromResult<object?>(null);
            });

        var payload = """
        {
          "schema": "2.0",
          "header": { "event_type": "out_approval" },
          "event": { "approval_code": "ABC" }
        }
        """;
        await dispatcher.DoAsync(payload, CancellationToken.None);
    }

    [Fact]
    public async Task DispatchAsync_NoHandler_LogsAndReturnsNull()
    {
        var dispatcher = new EventDispatcher();
        var payload = """
        {
          "schema": "2.0",
          "header": { "event_type": "unknown_event" },
          "event": {}
        }
        """;
        var resp = await dispatcher.DoAsync(payload, CancellationToken.None);
        Assert.Null(resp);
    }

    [Fact]
    public async Task DispatchAsync_OnAny_CatchAll()
    {
        var dispatcher = new EventDispatcher()
            .OnAny(ctx =>
            {
                Assert.Equal("any_event", ctx.EventType);
                return Task.FromResult<object?>(null);
            });

        var payload = """
        {
          "schema": "2.0",
          "header": { "event_type": "any_event" },
          "event": {}
        }
        """;
        await dispatcher.DoAsync(payload, CancellationToken.None);
    }

    [Fact]
    public async Task DispatchAsync_HandlerReturnsObject_Available()
    {
        var dispatcher = new EventDispatcher()
            .OnCustomizedEvent("evt", (ctx, raw) =>
                Task.FromResult<object?>(new { code = 0, msg = "ok" }));

        var payload = """
        {
          "schema": "2.0",
          "header": { "event_type": "evt" },
          "event": {}
        }
        """;
        var resp = await dispatcher.DoAsync(payload, CancellationToken.None);
        Assert.NotNull(resp);
    }
}
