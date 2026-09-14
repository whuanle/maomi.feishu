using System.Text.Json;
using FeishuWss.Client;
using FeishuWss.Events;
using FeishuWss.Events.Models;
using Microsoft.Extensions.Logging;

namespace WssSample;

/// <summary>
/// 飞书长连接最小可运行示例。
///
/// 启动前准备：
///   1. 在飞书开放平台创建企业自建应用，拿到 AppID / AppSecret
///   2. 「事件订阅 → 订阅方式」选择「使用长连接接收事件」
///   3. 在「权限管理」里勾选 im:message 等要订阅的能力
///   4. 设置环境变量 FEISHU_APP_ID / FEISHU_APP_SECRET
///
/// 运行：
///   dotnet run --project samples/WssSample
/// </summary>
internal static class Program
{
    private static async Task Main(string[] args)
    {
        var appId = Environment.GetEnvironmentVariable("FEISHU_APP_ID");
        var appSecret = Environment.GetEnvironmentVariable("FEISHU_APP_SECRET");
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
        {
            Console.Error.WriteLine("请先设置环境变量 FEISHU_APP_ID 和 FEISHU_APP_SECRET");
            return;
        }

        using var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
            b.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<WssClient>();

        var dispatcher = new EventDispatcher(logger)
            // 强类型订阅：收到消息事件
            .OnP2MessageReceiveV1(async (ctx, e) =>
            {
                var env = ctx.TryParseEnvelope();
                logger.LogInformation("[event] {EventType} from {UserId}: {Type} -> {ChatId}",
                    env?.Header.EventType,
                    e.Sender.SenderId.OpenId,
                    e.Message.MessageType,
                    e.Message.ChatId);
                // 这里处理消息：例如解析 e.Message.Content (JSON 字符串) 做关键字回复
                return null; // 返回 null 表示只 ack
            })
            // 自定义事件（透传 payload）
            .OnCustomizedEvent("out_approval", (ctx, raw) =>
            {
                logger.LogInformation("[custom] out_approval: {Raw}", raw);
                return Task.FromResult<object?>(null);
            });

        var client = WssClient.Build()
            .WithAppId(appId)
            .WithAppSecret(appSecret)
            .WithEventDispatcher(dispatcher)
            .WithLogger(logger)
            .WithAutoReconnect(true)
            .OnReady(() => { logger.LogInformation("[lifecycle] ready"); return Task.CompletedTask; })
            .OnReconnecting(() => { logger.LogInformation("[lifecycle] reconnecting..."); return Task.CompletedTask; })
            .OnReconnected(() => { logger.LogInformation("[lifecycle] reconnected"); return Task.CompletedTask; })
            .OnDisconnected(() => { logger.LogInformation("[lifecycle] disconnected"); return Task.CompletedTask; })
            .OnError(ex => { logger.LogError(ex, "[lifecycle] error: {Message}", ex.Message); return Task.CompletedTask; })
            .Build();

        // Ctrl-C 优雅退出
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            logger.LogInformation("Ctrl-C pressed, closing...");
            cts.Cancel();
            client.Close();
        };

        try
        {
            await client.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "client terminated with error: {Message}", ex.Message);
        }
    }
}
