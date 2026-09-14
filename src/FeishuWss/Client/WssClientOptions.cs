using System.Net.WebSockets;
using FeishuWss.Events;
using FeishuWss.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeishuWss.Client;

/// <summary>
/// 长连接客户端所有可配置项。链式 API 镜像 Go SDK 的 ClientOption 模式。
/// </summary>
public sealed class WssClientOptions
{
    /// <summary>飞书应用 AppID。</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>飞书应用 AppSecret。与 ClientAssertionProvider 二选一。</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>可选：client_assertion 提供方（与 appSecret 互斥）。</summary>
    public IClientAssertionProvider? ClientAssertionProvider { get; set; }

    /// <summary>飞书域名。默认国内版。</summary>
    public string Domain { get; set; } = "https://open.feishu.cn";

    /// <summary>事件分发器。</summary>
    public EventDispatcher? EventDispatcher { get; set; }

    /// <summary>HTTP client 注入点。默认 10s timeout。</summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>WebSocket 底层 client（通过 ClientWebSocket 创建）。</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; set; }

    /// <summary>每次写入超时。</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>HTTP /endpoint 请求超时。</summary>
    public TimeSpan EndpointTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>是否自动重连（默认 true）。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>首次重连抖动（秒）。</summary>
    public int ReconnectNonceSeconds { get; set; } = 30;

    /// <summary>默认重连间隔（秒），服务端 ClientConfig 可覆盖。</summary>
    public int DefaultReconnectIntervalSeconds { get; set; } = 120;

    /// <summary>默认重连次数，-1 无限。</summary>
    public int DefaultReconnectCount { get; set; } = -1;

    /// <summary>默认 ping 间隔（秒），服务端 ClientConfig 可覆盖。</summary>
    public int DefaultPingIntervalSeconds { get; set; } = 120;

    /// <summary>User-Agent 来源标识。</summary>
    public string Source { get; set; } = "feishu-wss-csharp";

    /// <summary>logger 注入点。</summary>
    public ILogger? Logger { get; set; }

    /// <summary>额外请求头（透传到 /endpoint）。</summary>
    public IDictionary<string, string>? ExtraHeaders { get; set; }

    // ---- 生命周期回调 ----

    public Func<Task>? OnReady { get; set; }
    public Func<Exception, Task>? OnError { get; set; }
    public Func<Task>? OnReconnecting { get; set; }
    public Func<Task>? OnReconnected { get; set; }
    public Func<Task>? OnDisconnected { get; set; }

    public string UserAgent => $"oapi-wss-sdk-csharp/{Source}";
}
