using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FeishuWss.Errors;
using FeishuWss.Events;
using FeishuWss.Frames;
using FeishuWss.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeishuWss.Client;

/// <summary>
/// 飞书长连接客户端主类。
/// 镜像 oapi-sdk-go/ws.Client 的行为：HTTP endpoint → WebSocket → 接收 / Ping / 重连。
///
/// 用法：
///   var client = WssClient.Build()
///       .WithAppId("cli_xxx")
///       .WithAppSecret("xxx")
///       .WithEventDispatcher(dispatcher)
///       .Build();
///   await client.StartAsync(CancellationToken.None);
/// </summary>
public sealed class WssClient : IAsyncDisposable
{
    private readonly WssClientOptions _opts;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly EndpointService _endpointService;

    /// <summary>服务端下发的 ClientConfig（PingInterval 等可被 Pong 覆盖）。</summary>
    private WssClientConfig _serverConfig = new();

    /// <summary>当前正在运行的 run（用于 Close 通知）。</summary>
    private WssClientRun? _activeRun;

    public WssClient(WssClientOptions opts)
    {
        _opts = opts;
        _logger = opts.Logger ?? NullLogger.Instance;

        _httpClient = opts.HttpClient ?? new HttpClient { Timeout = opts.EndpointTimeout };
        _endpointService = new EndpointService(
            _httpClient,
            opts.AppId,
            opts.AppSecret,
            opts.ClientAssertionProvider,
            opts.Domain,
            opts.ExtraHeaders,
            opts.UserAgent,
            _logger);
    }

    public EventDispatcher? EventDispatcher => _opts.EventDispatcher;

    /// <summary>开始一次运行。任务在连接最终断开（Close / 失败耗尽 / ctx 取消）时返回。</summary>
    public async Task StartAsync(CancellationToken callerToken)
    {
        var run = new WssClientRun(callerToken);
        _activeRun = run;
        try
        {
            await RunCoordinatorAsync(run).ConfigureAwait(false);
        }
        finally
        {
            _activeRun = null;
            run.Dispose();
        }
    }

    /// <summary>请求停止当前运行（非阻塞）。</summary>
    public void Close()
    {
        _activeRun?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        _httpClient.Dispose();
    }

    public static Builder Build() => new();

    // ============================================================
    // Lifecycle
    // ============================================================

    private async Task RunCoordinatorAsync(WssClientRun run)
    {
        WssClientConnection? conn;
        try
        {
            conn = await EstablishConnectionOnceAsync(run).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (WssClientError)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("websocket initial connection failed: {Message}", ex.Message);
            if (_opts.AutoReconnect && IsRetryable(ex))
            {
                await SafeInvokeOnErrorAsync(ex).ConfigureAwait(false);
            }
            conn = await ReconnectAfterFailureAsync(run, ex).ConfigureAwait(false);
            if (conn is null) return;
        }

        while (conn is not null)
        {
            var firstConnection = !run.EverConnected;
            run.PublishConnection(conn);
            run.EverConnected = true;

            var receiveTask = ReceiveLoopAsync(run, conn);
            var pingTask = firstConnection ? PingLoopAsync(run) : Task.CompletedTask;

            try
            {
                if (firstConnection)
                {
                    _logger.LogInformation("connected to {Endpoint}", conn.SafeEndpoint);
                    if (_opts.OnReady is { } onReady) await SafeInvokeAsync(() => onReady(), "ready").ConfigureAwait(false);
                }
                else
                {
                    _logger.LogInformation("reconnected to {Endpoint}", conn.SafeEndpoint);
                    if (_opts.OnReconnected is { } onRc) await SafeInvokeAsync(() => onRc(), "reconnected").ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ready callback threw");
            }

            Exception? exitErr;
            try
            {
                exitErr = await conn.ConnectionError.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                exitErr = null;
            }

            try { await receiveTask.ConfigureAwait(false); } catch { /* swallow on shutdown */ }
            try { if (!pingTask.IsCompleted) await pingTask.ConfigureAwait(false); } catch { /* swallow */ }

            await DeactivateConnectionAsync(run, conn).ConfigureAwait(false);

            if (run.CancellationToken.IsCancellationRequested) return;
            if (exitErr is null) return;

            _logger.LogWarning("websocket connection failed: {Message}", exitErr.Message);
            conn = await ReconnectAfterFailureAsync(run, exitErr).ConfigureAwait(false);
        }
    }

    private async Task<WssClientConnection?> ReconnectAfterFailureAsync(WssClientRun run, Exception failure)
    {
        if (!_opts.AutoReconnect || !IsRetryable(failure))
        {
            await SafeInvokeOnErrorAsync(failure).ConfigureAwait(false);
            return null;
        }

        if (_opts.OnReconnecting is { } onRc)
        {
            try { await onRc().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "reconnecting callback threw"); }
        }

        int attempts = 0;
        while (true)
        {
            var (maxCount, intervalSec, nonceSec, _) = GetEffectiveConfig();

            if (maxCount >= 0 && attempts >= maxCount)
            {
                var err = new WssReconnectExhaustedException(attempts);
                _logger.LogWarning("websocket reconnect attempts exhausted after {Attempts}", attempts);
                await SafeInvokeOnErrorAsync(err).ConfigureAwait(false);
                return null;
            }

            TimeSpan delay = attempts == 0
                ? TimeSpan.FromMilliseconds(Random.Shared.Next(0, Math.Max(1, nonceSec)) * 1000)
                : TimeSpan.FromSeconds(Math.Max(0, intervalSec));

            if (!await WaitRunDelayAsync(run, delay).ConfigureAwait(false)) return null;
            attempts++;

            if (maxCount >= 0)
                _logger.LogInformation("websocket reconnecting: attempt {Attempts}/{Max}", attempts, maxCount);
            else
                _logger.LogInformation("websocket reconnecting: attempt {Attempts}", attempts);

            try
            {
                return await EstablishConnectionOnceAsync(run).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("websocket reconnect attempt {Attempts} failed: {Message}", attempts, ex.Message);
                if (!_opts.AutoReconnect || !IsRetryable(ex))
                {
                    await SafeInvokeOnErrorAsync(ex).ConfigureAwait(false);
                    return null;
                }
                await SafeInvokeOnErrorAsync(ex).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitRunDelayAsync(WssClientRun run, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return !run.CancellationToken.IsCancellationRequested;
        try
        {
            await Task.Delay(delay, run.CancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ============================================================
    // Transport
    // ============================================================

    private async Task<WssClientConnection> EstablishConnectionOnceAsync(WssClientRun run)
    {
        var endpoint = await _endpointService.FetchAsync(run.CancellationToken).ConfigureAwait(false);
        ApplyServerConfig(endpoint.ClientConfig);

        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Scheme) || string.IsNullOrEmpty(u.Host))
            throw new WssProtocolException($"invalid endpoint url: {endpoint.Url}");

        var ws = (_opts.WebSocketFactory ?? DefaultWebSocketFactory)();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
            cts.CancelAfter(_opts.EndpointTimeout);
            await ws.ConnectAsync(u, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            throw;
        }

        var q = ParseQuery(u.Query);
        var connId = q.TryGetValue("device_id", out var did) ? did : string.Empty;
        var serviceId = q.TryGetValue("service_id", out var sid) && int.TryParse(sid, out var v) ? v : 0;

        return new WssClientConnection(ws, connId, serviceId, SafeEndpoint(endpoint.Url));
    }

    private static ClientWebSocket DefaultWebSocketFactory() => new()
    {
        Options = { KeepAliveInterval = TimeSpan.FromSeconds(30) }
    };

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return d;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) d[Uri.UnescapeDataString(part)] = string.Empty;
            else d[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return d;
    }

    private static string SafeEndpoint(string url)
    {
        var i = url.IndexOf('?');
        return i >= 0 ? url[..i] : url;
    }

    private void ApplyServerConfig(WssClientConfig conf) => _serverConfig = conf;

    private (int reconnectCount, int reconnectIntervalSec, int reconnectNonceSec, int pingIntervalSec) GetEffectiveConfig()
    {
        var sc = _serverConfig;
        return (
            sc.ReconnectCount > 0 ? sc.ReconnectCount : _opts.DefaultReconnectCount,
            sc.ReconnectIntervalSeconds > 0 ? sc.ReconnectIntervalSeconds : _opts.DefaultReconnectIntervalSeconds,
            sc.ReconnectNonce > 0 ? sc.ReconnectNonce : _opts.ReconnectNonceSeconds,
            sc.PingIntervalSeconds > 0 ? sc.PingIntervalSeconds : _opts.DefaultPingIntervalSeconds);
    }

    private static bool IsRetryable(Exception err) => err is not WssClientError;

    // ============================================================
    // Session
    // ============================================================

    private async Task DeactivateConnectionAsync(WssClientRun run, WssClientConnection conn)
    {
        run.TakeConnection();
        await conn.CloseAsync("disconnect").ConfigureAwait(false);
        _logger.LogInformation("disconnected from {Endpoint}", conn.SafeEndpoint);
        if (_opts.OnDisconnected is { } onDc)
        {
            try { await onDc().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "disconnected callback threw"); }
        }
    }

    private async Task PingLoopAsync(WssClientRun run)
    {
        while (!run.CancellationToken.IsCancellationRequested)
        {
            await SendPingAsync(run).ConfigureAwait(false);
            var (_, _, _, pingSec) = GetEffectiveConfig();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pingSec)), run.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SendPingAsync(WssClientRun run)
    {
        try
        {
            var conn = run.CurrentConnection;
            if (conn is null || !run.IsConnectionActive(conn)) return;
            var ping = Frame.CreatePing(conn.ServiceId);
            var data = FrameSerializer.Marshal(ping);
            await conn.WriteBinaryAsync(data, _opts.WriteTimeout, run.CancellationToken).ConfigureAwait(false);
            _logger.LogDebug("ping success, conn_id={ConnId}", conn.ConnId);
        }
        catch (OperationCanceledException) { /* normal stop */ }
        catch (Exception ex)
        {
            _logger.LogWarning("ping failed: {Message}", ex.Message);
            run.CurrentConnection?.ReportError(ex);
        }
    }

    private async Task ReceiveLoopAsync(WssClientRun run, WssClientConnection conn)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (!run.CancellationToken.IsCancellationRequested && conn.Socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(run.CancellationToken);
                    var (_, _, _, pingSec) = GetEffectiveConfig();
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, 2 * pingSec + 5)));
                    result = await conn.Socket.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("peer closed: {Desc}", result.CloseStatusDescription);
                        conn.ReportError(new WssServerError((int?)result.CloseStatus ?? -1, result.CloseStatusDescription ?? "peer closed"));
                        return;
                    }
                    ms.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    _logger.LogWarning("websocket received unsupported message type {Type}", result.MessageType);
                    continue;
                }

                _ = HandleMessageAsync(run, ms.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            conn.ReportError(new OperationCanceledException());
        }
        catch (Exception ex)
        {
            conn.ReportError(ex);
        }
    }

    private async Task HandleMessageAsync(WssClientRun run, byte[] msg)
    {
        Frame frame;
        try
        {
            frame = FrameSerializer.Unmarshal(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "frame decode failed");
            return;
        }

        try
        {
            if ((FrameType)frame.Method == FrameType.Control)
            {
                HandleControlFrame(frame);
                return;
            }
            await HandleDataFrameAsync(run, frame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "frame handler threw");
        }
    }

    private void HandleControlFrame(Frame frame)
    {
        if (frame.MessageType == WssMessageType.Pong)
        {
            _logger.LogDebug("receive pong");
            if (frame.Payload is { Length: > 0 })
            {
                try
                {
                    var conf = JsonSerializer.Deserialize(frame.Payload, BootstrapJsonContext.Default.WssClientConfig);
                    if (conf is not null) ApplyServerConfig(conf);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "client config in pong decode failed");
                }
            }
        }
    }

    private async Task HandleDataFrameAsync(WssClientRun run, Frame frame)
    {
        if (frame.MessageType != WssMessageType.Event)
        {
            // 当前只处理 event；卡片回调走另一条路
            return;
        }
        if (_opts.EventDispatcher is null)
        {
            _logger.LogDebug("received event but no dispatcher configured, drop");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] combined;
        if (frame.Sum > 1)
        {
            var merged = _msgCombine.Combine(frame.MessageId, frame.Sum, frame.Seq, frame.Payload);
            if (merged is null) return;
            combined = merged;
        }
        else
        {
            combined = frame.Payload;
        }

        var payloadJson = Encoding.UTF8.GetString(combined);
        _logger.LogDebug("receive event, message_id={Mid}, trace_id={Tid}, payload={P}",
            frame.MessageId, frame.TraceId, payloadJson);

        object? response = null;
        Exception? handlerErr = null;
        try
        {
            response = await _opts.EventDispatcher.DoAsync(payloadJson, run.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            handlerErr = ex;
            _logger.LogError(ex, "handle message failed, message_id={Mid}", frame.MessageId);
        }

        await WriteEventResponseAsync(run, frame, response, handlerErr, startedAt).ConfigureAwait(false);
    }

    private async Task WriteEventResponseAsync(WssClientRun run, Frame original, object? response, Exception? handlerErr, long startedAt)
    {
        var conn = run.CurrentConnection;
        if (conn is null || !run.IsConnectionActive(conn)) return;

        var endedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        original.Headers.Add(FrameHeader.BizRt, (endedAt - startedAt).ToString());

        var resp = new ResponseEnvelope { Code = handlerErr is not null ? 500 : 200 };
        if (handlerErr is null && response is not null)
        {
            try
            {
                resp.Data = JsonSerializer.SerializeToUtf8Bytes(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "response serialize failed");
                resp.Code = 500;
            }
        }

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(resp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "response envelope encode failed");
            return;
        }

        original.Payload = payload;
        original.PayloadType = string.Empty;
        original.PayloadEncoding = string.Empty;

        byte[] data;
        try
        {
            data = FrameSerializer.Marshal(original);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "response frame encode failed");
            return;
        }

        try
        {
            await conn.WriteBinaryAsync(data, _opts.WriteTimeout, run.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "response message write failed, message_id={Mid}", original.MessageId);
            conn.ReportError(ex);
        }
    }

    private async Task SafeInvokeAsync(Func<Task> fn, string tag)
    {
        try { await fn().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "{Tag} callback threw", tag); }
    }

    private async Task SafeInvokeOnErrorAsync(Exception err)
    {
        if (_opts.OnError is null) return;
        try { await _opts.OnError(err).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "error callback threw"); }
    }

    private readonly FrameCombiner _msgCombine = new(TimeSpan.FromSeconds(5));

    private sealed class ResponseEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")]
        public int Code { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("headers")]
        public Dictionary<string, string>? Headers { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public byte[]? Data { get; set; }
    }

    public sealed class Builder
    {
        private readonly WssClientOptions _opts = new();

        public Builder WithAppId(string appId) { _opts.AppId = appId; return this; }
        public Builder WithAppSecret(string appSecret) { _opts.AppSecret = appSecret; return this; }
        public Builder WithClientAssertionProvider(IClientAssertionProvider provider) { _opts.ClientAssertionProvider = provider; return this; }
        public Builder WithDomain(string domain) { _opts.Domain = domain; return this; }
        public Builder WithEventDispatcher(EventDispatcher d) { _opts.EventDispatcher = d; return this; }
        public Builder WithHttpClient(HttpClient http) { _opts.HttpClient = http; return this; }
        public Builder WithLogger(ILogger logger) { _opts.Logger = logger; return this; }
        public Builder WithAutoReconnect(bool enable) { _opts.AutoReconnect = enable; return this; }
        public Builder WithWriteTimeout(TimeSpan timeout) { _opts.WriteTimeout = timeout; return this; }
        public Builder WithEndpointTimeout(TimeSpan timeout) { _opts.EndpointTimeout = timeout; return this; }
        public Builder WithReconnectPolicy(int count, int intervalSec, int nonceSec)
        { _opts.DefaultReconnectCount = count; _opts.DefaultReconnectIntervalSeconds = intervalSec; _opts.ReconnectNonceSeconds = nonceSec; return this; }
        public Builder WithPingInterval(int seconds) { _opts.DefaultPingIntervalSeconds = seconds; return this; }
        public Builder WithExtraHeader(string key, string value)
        { (_opts.ExtraHeaders ??= new Dictionary<string, string>())[key] = value; return this; }
        public Builder OnReady(Func<Task> handler) { _opts.OnReady = handler; return this; }
        public Builder OnError(Func<Exception, Task> handler) { _opts.OnError = handler; return this; }
        public Builder OnReconnecting(Func<Task> handler) { _opts.OnReconnecting = handler; return this; }
        public Builder OnReconnected(Func<Task> handler) { _opts.OnReconnected = handler; return this; }
        public Builder OnDisconnected(Func<Task> handler) { _opts.OnDisconnected = handler; return this; }

        public WssClient Build()
        {
            if (string.IsNullOrEmpty(_opts.AppId))
                throw new ArgumentException("AppId is required");
            if (string.IsNullOrEmpty(_opts.AppSecret) && _opts.ClientAssertionProvider is null)
                throw new ArgumentException("AppSecret or ClientAssertionProvider is required");
            return new WssClient(_opts);
        }
    }
}

/// <summary>
/// 拆包 / 合包缓存。
/// 飞书对超大事件会拆成 sum 个分片（同 messageId），这里按 (messageId, sum) 聚合，
/// 全部到齐后按 seq 拼成完整 payload；超时未到齐的丢弃。
/// </summary>
internal sealed class FrameCombiner
{
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, CombinerState> _states = new(StringComparer.Ordinal);

    public FrameCombiner(TimeSpan ttl) { _ttl = ttl; }

    public byte[]? Combine(string messageId, int sum, int seq, byte[] payload)
    {
        var key = $"{messageId}|{sum}";
        EvictExpired();

        if (!_states.TryGetValue(key, out var state))
        {
            if (seq < 0 || seq >= sum) return null;
            state = new CombinerState(sum);
            state.Slots[seq] = payload;
            _states[key] = state;
            return null;
        }

        if (seq < 0 || seq >= state.Slots.Count) return null;
        state.Slots[seq] = payload;

        int capacity = 0;
        for (int i = 0; i < state.Slots.Count; i++)
        {
            if (state.Slots[i] is null) { state.LastAccess = DateTimeOffset.UtcNow; return null; }
            capacity += state.Slots[i]!.Length;
        }

        var combined = new byte[capacity];
        int pos = 0;
        for (int i = 0; i < state.Slots.Count; i++)
        {
            state.Slots[i]!.CopyTo(combined, pos);
            pos += state.Slots[i]!.Length;
        }
        _states.Remove(key);
        return combined;
    }

    private void EvictExpired()
    {
        if (_states.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        List<string>? expired = null;
        foreach (var kv in _states)
        {
            if (now - kv.Value.LastAccess > _ttl)
            {
                expired ??= new List<string>();
                expired.Add(kv.Key);
            }
        }
        if (expired is not null)
        {
            foreach (var k in expired) _states.Remove(k);
        }
    }

    private sealed class CombinerState
    {
        public List<byte[]?> Slots { get; }
        public DateTimeOffset LastAccess { get; set; } = DateTimeOffset.UtcNow;
        public CombinerState(int sum) { Slots = new List<byte[]?>(new byte[]?[sum]); }
    }
}
