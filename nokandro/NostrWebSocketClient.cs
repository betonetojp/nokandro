using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace nokandro
{
    public class NostrWebSocketClient : IDisposable
    {
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
        // Fallback only: protocol keep-alives do not surface as ReceiveAsync messages.
        // Idle bunker can go long stretches with no kind 24133 traffic.
        private static readonly TimeSpan ReceiveIdleTimeout = TimeSpan.FromMinutes(10);

        private readonly string _url;
        private readonly string _tag;
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _isDisposed;

        public event Action<string>? OnMessageReceived;
        public event Action<string>? OnLog;
        public event Action<WebSocketState>? OnStateChanged;

        public WebSocketState State => _ws?.State ?? WebSocketState.None;

        public NostrWebSocketClient(string url, string tag = "NostrWebSocketClient")
        {
            _url = url;
            _tag = tag;
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectionLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
            _cts = null;
            OnStateChanged?.Invoke(WebSocketState.Closed);
        }

        private async Task ConnectionLoopAsync(CancellationToken ct)
        {
            Log("Starting connection loop...");
            var delayMs = 5000;
            const int maxDelayMs = 60000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    _ws.Options.KeepAliveInterval = KeepAliveInterval;
                    Log($"Connecting to {_url}...");
                    OnStateChanged?.Invoke(WebSocketState.Connecting);

                    await _ws.ConnectAsync(new Uri(_url), ct);

                    Log("Connected successfully");
                    OnStateChanged?.Invoke(WebSocketState.Open);

                    // Reset delay on successful connection
                    delayMs = 5000;

                    await ReceiveLoopAsync(_ws, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Connection error: {ex.Message}");
                    OnStateChanged?.Invoke(WebSocketState.Closed);
                }
                finally
                {
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;
                }

                if (ct.IsCancellationRequested) break;

                Log($"Reconnecting in {delayMs / 1000} seconds...");
                try
                {
                    await Task.Delay(delayMs, ct);
                    // Exponential backoff
                    delayMs = Math.Min(delayMs * 2, maxDelayMs);
                }
                catch
                {
                    break;
                }
            }
            Log("Connection loop ended");
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var pool = ArrayPool<byte>.Shared;
            var rawBuffer = pool.Rent(16 * 1024);
            var buffer = new ArraySegment<byte>(rawBuffer);
            var sb = new StringBuilder();
            var lastActivityUtc = DateTime.UtcNow;

            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult? result;
                    do
                    {
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idleCts.CancelAfter(ReceiveIdleTimeout);
                        try
                        {
                            result = await ws.ReceiveAsync(buffer, idleCts.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            var idleFor = DateTime.UtcNow - lastActivityUtc;
                            Log($"Receive idle timeout after {idleFor.TotalSeconds:F0}s — aborting for reconnect");
                            try { ws.Abort(); } catch { }
                            OnStateChanged?.Invoke(WebSocketState.Closed);
                            return;
                        }

                        lastActivityUtc = DateTime.UtcNow;

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Log("Websocket closed by server");
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                            OnStateChanged?.Invoke(WebSocketState.Closed);
                            break;
                        }

                        var chunk = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, result.Count);
                        sb.Append(chunk);
                    }
                    while (!result.EndOfMessage);

                    if (ws.State != WebSocketState.Open) break;

                    var message = sb.ToString();
                    if (!string.IsNullOrEmpty(message))
                    {
                        OnMessageReceived?.Invoke(message);
                    }
                }
            }
            finally
            {
                pool.Return(rawBuffer);
            }
        }

        public async Task<bool> SendTextAsync(string text, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open)
            {
                Log("SendTextAsync called but WebSocket is not open");
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            var seg = new ArraySegment<byte>(bytes);

            try
            {
                await _sendLock.WaitAsync(ct);
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.SendAsync(seg, WebSocketMessageType.Text, true, ct);
                        return true;
                    }
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex)
            {
                Log($"SendTextAsync failed: {ex.Message}");
            }
            return false;
        }

        private void Log(string msg)
        {
            OnLog?.Invoke($"[{_tag}] {msg}");
            AppLog.D(_tag, msg);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Stop();
            _sendLock.Dispose();
        }
    }
}
