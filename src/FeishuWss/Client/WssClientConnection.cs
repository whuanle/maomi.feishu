using System.Net.WebSockets;
using FeishuWss.Errors;

namespace FeishuWss.Client;

/// <summary>
/// 一次运行的物理连接。承载 ClientWebSocket + 当前 service_id + 写入锁。
/// 重连时整体替换，不在原地变更。
/// </summary>
public sealed class WssClientConnection : IDisposable
{
    public ClientWebSocket Socket { get; }
    public string ConnId { get; }
    public int ServiceId { get; }
    public string SafeEndpoint { get; }

    /// <summary>
    /// 物理连接出错时通知 coordinator；只放第一条错误。
    /// </summary>
    public TaskCompletionSource<Exception> ConnectionError { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public WssClientConnection(ClientWebSocket socket, string connId, int serviceId, string safeEndpoint)
    {
        Socket = socket;
        ConnId = connId;
        ServiceId = serviceId;
        SafeEndpoint = safeEndpoint;
    }

    public async Task WriteBinaryAsync(byte[] data, TimeSpan timeout, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await Socket.SendAsync(data, WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void ReportError(Exception err)
    {
        ConnectionError.TrySetResult(err);
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 关闭异常忽略
        }
    }

    public void Dispose()
    {
        try { Socket.Dispose(); } catch { }
        try { _writeLock.Dispose(); } catch { }
    }
}

/// <summary>
/// 一次 Start 的运行状态。协调 lifecycle：cancel / error / conn / done。
/// </summary>
public sealed class WssClientRun : IDisposable
{
    private readonly object _gate = new();
    private int _stopReason; // 0=Running 1=Ctx 2=Close 3=Failed

    public CancellationTokenSource Cts { get; }
    public CancellationToken CancellationToken { get; }

    public WssClientConnection? CurrentConnection { get; private set; }
    public Exception? StopError { get; private set; }
    public bool EverConnected { get; set; }

    public WssClientRun(CancellationToken callerToken)
    {
        Cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        CancellationToken = Cts.Token;
    }

    public bool IsStopping
    {
        get
        {
            lock (_gate) return _stopReason != 0;
        }
    }

    public void SetStopping(int reason, Exception? err)
    {
        lock (_gate)
        {
            if (_stopReason != 0) return;
            _stopReason = reason;
            StopError = err;
        }
    }

    public void PublishConnection(WssClientConnection conn)
    {
        lock (_gate)
        {
            CurrentConnection = conn;
            EverConnected = true;
        }
    }

    public WssClientConnection? TakeConnection()
    {
        lock (_gate)
        {
            var c = CurrentConnection;
            CurrentConnection = null;
            return c;
        }
    }

    public void Cancel()
    {
        try { Cts.Cancel(); } catch { /* ignore */ }
    }

    public bool IsConnectionActive(WssClientConnection conn)
    {
        if (CancellationToken.IsCancellationRequested) return false;
        lock (_gate)
        {
            return _stopReason == 0 && ReferenceEquals(CurrentConnection, conn);
        }
    }

    public void dispose() => Dispose();

    public void Dispose()
    {
        try { Cts.Dispose(); } catch { }
    }
}
