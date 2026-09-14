<p align="center">
  <img src="package.png" alt="FeishuWss" width="128" height="128" />
</p>

<h1 align="center">FeishuWss</h1>

<p align="center">
  飞书开放平台<strong>长连接（WebSocket）</strong>SDK 的 C# 实现 —— 无需公网回调地址，进程内直接接收飞书事件。
</p>

<p align="center">
  <img alt="NuGet" src="https://img.shields.io/badge/nuget-v1.0.0-blue" />
  <img alt="net" src="https://img.shields.io/badge/.NET-8.0-512BD4" />
  <img alt="license" src="https://img.shields.io/badge/license-MIT-green" />
</p>

---

## 这是什么

飞书官方提供了 Go / Python / Java / Node.js 的长连接 SDK（`oapi-sdk-go` 等），但没有官方的 .NET 版本。

FeishuWss 参照 `oapi-sdk-go/v3/ws` 的实现，用 **.NET 8** 完整复刻了这套能力：

- 调用 `POST /callback/ws/endpoint` 拿到长连接接入地址与客户端配置
- 用 `ClientWebSocket` 建立持久连接
- 收发飞书私有的 protobuf 二进制帧（`pbbp2`）
- 定时 Ping / Pong 保活，断线自动重连（含抖动与次数上限）
- 超大事件自动拆包合包
- 事件分发到你的强类型 handler

**核心价值**：不需要备案域名、不需要公网 IP、不需要 ngrok 内网穿透，一个控制台 / 桌面 / 后台服务就能收飞书事件，本地开发和生产环境都能用。

## 安装

```bash
dotnet add package Maomi.FeishuWss
```

或者：

```xml
<PackageReference Include="Maomi.FeishuWss" Version="1.0.0" />
```



## 快速开始

### 前置配置

1. 在[飞书开放平台](https://open.feishu.cn/app)创建企业自建应用，拿到 `AppID` / `AppSecret`
2. 进入 **事件与回调 → 事件订阅**，订阅方式选择 **「使用长连接接收事件」**
3. 在 **权限管理** 中开通需要订阅的权限，例如 `im:message`（接收消息）
4. 在 **事件订阅** 中添加要订阅的事件，例如 `接收消息 im.message.receive_v1`

### 最小可运行代码

```csharp
using FeishuWss.Client;
using FeishuWss.Events;

// 1) 注册事件处理器
var dispatcher = new EventDispatcher()
    .OnP2MessageReceiveV1(async (ctx, e) =>
    {
        Console.WriteLine($"收到 {e.Sender.SenderId.OpenId} 在 {e.Message.ChatId} 的消息");
        Console.WriteLine($"类型={e.Message.MessageType}, 内容={e.Message.Content}");
        return null;   // 返回 null = 仅 ack；返回对象 = 作为响应写回
    })
    .OnCustomizedEvent("out_approval", (ctx, raw) =>
    {
        Console.WriteLine($"审批事件: {raw}");
        return Task.FromResult<object?>(null);
    });

// 2) 创建客户端
var client = WssClient.Build()
    .WithAppId(Environment.GetEnvironmentVariable("FEISHU_APP_ID")!)
    .WithAppSecret(Environment.GetEnvironmentVariable("FEISHU_APP_SECRET")!)
    .WithEventDispatcher(dispatcher)
    .OnReady(() => { Console.WriteLine("长连接已就绪"); return Task.CompletedTask; })
    .OnError(ex => { Console.Error.WriteLine($"连接异常: {ex.Message}"); return Task.CompletedTask; })
    .Build();

// 3) 启动（阻塞直到连接最终断开或 CancellationToken 取消）
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); client.Close(); };

await client.StartAsync(cts.Token);
```

运行完整示例：

```bash
export FEISHU_APP_ID=cli_xxxxxxxx
export FEISHU_APP_SECRET=xxxxxxxx
dotnet run --project samples/WssSample
```

## 核心能力

### 事件订阅的三种方式

```csharp
// 强类型：payload 自动反序列化为 T
dispatcher.On<MyEventPayload>("my_app.custom_event", async (ctx, e) => { ... });

// 透传：拿到 envelope 的原始 JSON 字符串
dispatcher.OnCustomizedEvent("my_app.custom_event", async (ctx, raw) => { ... });

// 兜底：所有未注册的 event_type
dispatcher.OnAny(async ctx => { ... });
```

### ClientAssertion 鉴权（不落地 AppSecret）

```csharp
class MyAssertionProvider : IClientAssertionProvider
{
    public async Task<ClientAssertionToken> RetrieveTokenAsync(string aud, CancellationToken ct)
    {
        // 从内部密钥服务换取短期 JWT
        var jwt = await FetchJwtFromInternalService(ct);
        return new ClientAssertionToken { Value = jwt };
    }
}

var client = WssClient.Build()
    .WithAppId("cli_xxx")
    .WithClientAssertionProvider(new MyAssertionProvider())
    .Build();
```

### 生命周期回调

| 回调 | 触发时机 |
|------|----------|
| `OnReady` | 首次建立连接成功 |
| `OnReconnecting` | 检测到断线，开始重连前 |
| `OnReconnected` | 重连成功（非首次） |
| `OnDisconnected` | 一条物理连接被关闭 |
| `OnError` | 重连过程中的可恢复错误、或终态错误 |

### 可调参数

```csharp
WssClient.Build()
    .WithAutoReconnect(true)                       // 是否自动重连，默认 true
    .WithReconnectPolicy(count: -1,               // 最大重连次数，-1 = 无限
                         intervalSec: 120,        // 重连间隔
                         nonceSec: 30)            // 首次重连随机抖动上限
    .WithPingInterval(120)                        // Ping 间隔（秒）
    .WithWriteTimeout(TimeSpan.FromSeconds(10))   // 单次写入超时
    .WithEndpointTimeout(TimeSpan.FromSeconds(10)) // HTTP 拉取 endpoint 超时
    .WithDomain("https://open.feishu.cn")         // 也可指向 Lark 国际版
    .WithHttpClient(myHttpClient)                 // 自定义 HttpClient（代理等）
    .WithLogger(myLogger)                         // 注入 ILogger
    .Build();
```

> 服务端会在 endpoint 响应和每次 Pong 里下发 `ClientConfig`（`PingInterval` / `ReconnectInterval` / `ReconnectCount` / `ReconnectNonce`），这些值会**动态覆盖**本地默认配置。

## 协议说明

飞书长连接帧是私有 protobuf 二进制格式，字段顺序与 wire-type 与 Go SDK 的 `gogoproto` 默认输出对齐：

| Tag | Field | Wire-Type | 说明 |
|-----|-------|-----------|------|
| 1 | `SeqID` | varint | |
| 2 | `LogID` | varint | |
| 3 | `service` | varint | service_id，Ping 帧使用 |
| 4 | `method` | varint | `0`=控制帧，`1`=数据帧 |
| 5 | `headers` | length-delimited | 多个 `Header{key,value}` |
| 6 | `payload_encoding` | length-delimited | |
| 7 | `payload_type` | length-delimited | |
| 8 | `payload` | length-delimited | bytes |
| 9 | `LogIDNew` | length-delimited | 字符串链路 ID |

`FrameSerializer` 完全手写 wire-format，**不引入** protobuf-net / Google.Protobuf，减少依赖体积。

事件 envelope 为 v2 schema：

```json
{
  "schema": "2.0",
  "header": {
    "event_id": "...",
    "event_type": "im.message.receive_v1",
    "create_time": "...",
    "app_id": "...",
    "tenant_key": "..."
  },
  "event": { }
}
```

## 与 oapi-sdk-go 的对应关系

| Go SDK (`v3/ws`) | FeishuWss |
|------------------|-----------|
| `pbbp2.pb.go` | `Frames/FrameSerializer.cs`（手写 wire-format） |
| `client.go` | `Client/WssClientOptions.cs` + `WssClient.Builder` |
| `client_lifecycle.go` | `WssClient.RunCoordinatorAsync` / `ReconnectAfterFailureAsync` |
| `client_session.go` | `WssClient.ReceiveLoopAsync` / `PingLoopAsync` / `FrameCombiner` |
| `client_transport.go` | `Transport/EndpointService.cs` |
| `client_message.go` | `WssClient.HandleDataFrameAsync` / `WriteEventResponseAsync` |
| `event/dispatcher` | `Events/EventDispatcher.cs` |
| `WithXxx(...) ClientOption` | `WssClient.Build().WithXxx()` |
| `context.Context` | `CancellationToken` |
| `gorilla/websocket` | `System.Net.WebSockets.ClientWebSocket` |

**差异**：Go SDK 通过代码生成产出了 200+ 个强类型 `OnP2XxxV1` 方法；FeishuWss 只内置 `OnP2MessageReceiveV1`，其余事件用通用的 `dispatcher.On<T>(eventType, handler)` 注册，避免庞大的生成代码。

## 项目结构

```
FeishuWss/
├── src/FeishuWss/               类库（打包目标）
│   ├── Client/                  WssClient 主体：lifecycle / session / transport
│   ├── Frames/                  protobuf 二进制帧编解码
│   ├── Transport/               /callback/ws/endpoint HTTP 拉取
│   ├── Events/                  事件分发器 + 强类型模型
│   └── Errors/                  异常类型
├── samples/WssSample/           控制台示例
├── tests/FeishuWss.Tests/       xUnit 单元测试
├── tools/package_icon_gen.py    生成 package.png 图标的脚本
├── package.png                  NuGet 包图标
└── README.md
```

## 构建与打包

```bash
dotnet build                                              # 构建
dotnet test                                               # 跑测试
dotnet pack src/FeishuWss/FeishuWss.csproj -c Release     # 产出 nupkg + snupkg
```

产物在 `src/FeishuWss/bin/Release/`：

```
FeishuWss.1.0.0.nupkg
FeishuWss.1.0.0.snupkg
```

重新生成包图标：

```bash
pip install Pillow
python tools/package_icon_gen.py     # 覆盖写入 package.png
```

本地验证包内容：

```bash
# 校验签名与结构
nuget verify -All src/FeishuWss/bin/Release/FeishuWss.1.0.0.nupkg

# 或直接当本地源安装
dotnet nuget add source ./src/FeishuWss/bin/Release -n local-feishuwss
dotnet add package FeishuWss --source local-feishuwss
```

## 已知边界

- 当前分发器只路由 `event` 帧；`card` 帧的入口已预留。卡片回调可用 `OnCustomizedEvent("card.action.trigger", ...)` 透传处理。
- 只内置 `im.message.receive_v1` 一个强类型事件，其余走 `On<T>`。
- Pong 帧中携带的 `ClientConfig` 按 JSON 解析，服务端字段变更时会退化到本地默认值。

## License

[MIT](LICENSE)

本项目协议实现参考自 [larksuite/oapi-sdk-go](https://github.com/larksuite/oapi-sdk-go)（MIT）。
