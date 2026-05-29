using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Parsed nostrconnect:// URI (NIP-46 client-initiated).
    /// </summary>
    public sealed class NostrConnectUri
    {
        public string ClientPubkey { get; }
        public string[] Relays { get; }
        public string Secret { get; }
        public string? Metadata { get; }
        public string? Name { get; }
        public string? Url { get; }
        public string? Image { get; }
        public string? Perms { get; }
        public string RawUri { get; }

        private NostrConnectUri(string clientPubkey, string[] relays, string secret, string? metadata, string? name, string? url, string? image, string? perms, string rawUri)
        {
            ClientPubkey = clientPubkey;
            Relays = relays;
            Secret = secret;
            Metadata = metadata;
            Name = name;
            Url = url;
            Image = image;
            Perms = perms;
            RawUri = rawUri;
        }

        public static bool TryParse(string uri, out NostrConnectUri? result)
        {
            result = null;
            if (string.IsNullOrEmpty(uri)) return false;

            uri = uri.Trim();
            if (!uri.StartsWith("nostrconnect://", StringComparison.OrdinalIgnoreCase)) return false;

            try
            {
                var withoutScheme = uri["nostrconnect://".Length..];
                var qIndex = withoutScheme.IndexOf('?');
                var clientPubkey = qIndex >= 0 ? withoutScheme[..qIndex] : withoutScheme;

                if (clientPubkey.Length != 64) return false;
                foreach (var c in clientPubkey)
                {
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        return false;
                }
                clientPubkey = clientPubkey.ToLowerInvariant();

                var relays = new List<string>();
                string? secret = null;
                string? metadata = null;
                string? name = null;
                string? url = null;
                string? image = null;
                string? perms = null;

                if (qIndex >= 0)
                {
                    var qs = withoutScheme[(qIndex + 1)..];
                    foreach (var param in qs.Split('&'))
                    {
                        var eqIdx = param.IndexOf('=');
                        if (eqIdx < 0) continue;
                        var key = param[..eqIdx];
                        var val = Uri.UnescapeDataString(param[(eqIdx + 1)..]);
                        switch (key)
                        {
                            case "relay":
                                if (!string.IsNullOrEmpty(val)) relays.Add(val);
                                break;
                            case "secret":
                                secret = val;
                                break;
                            case "metadata":
                                metadata = val;
                                break;
                            case "name":
                                if (!string.IsNullOrEmpty(val)) name = val;
                                break;
                            case "url":
                                if (!string.IsNullOrEmpty(val)) url = val;
                                break;
                            case "image":
                                if (!string.IsNullOrEmpty(val)) image = val;
                                break;
                            case "perms":
                                if (!string.IsNullOrEmpty(val)) perms = val;
                                break;
                        }
                    }
                }

                if (relays.Count == 0 || string.IsNullOrEmpty(secret))
                    return false;

                result = new NostrConnectUri(clientPubkey, [.. relays], secret, metadata, name, url, image, perms, uri);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string? GetClientName()
        {
            if (!string.IsNullOrEmpty(Metadata))
            {
                try
                {
                    using var doc = JsonDocument.Parse(Metadata);
                    if (doc.RootElement.TryGetProperty("name", out var n))
                    {
                        var s = n.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
            }
            return Name;
        }
    }

    /// <summary>
    /// Manages a nostrconnect:// session (NIP-46 client-initiated).
    /// </summary>
    public sealed class NostrConnectSession
    {
        private const string TAG = "NostrConnectSession";
        private readonly byte[] _privKey;
        private readonly string _signerPubkeyHex;
        private readonly NostrConnectUri _connectUri;
        private readonly Nip46Handler _handler;
        private CancellationTokenSource? _cts;
        private readonly List<ClientWebSocket> _sockets = [];
        private readonly SemaphoreSlim _wsLock = new(1, 1);
        private bool _clientAuthenticated;
        private bool _connectAckSent;

        public bool IsRunning { get; private set; }
        public string ClientPubkey => _connectUri.ClientPubkey;
        public string[] Relays => _connectUri.Relays;
        public string RawUri => _connectUri.RawUri;

        public event Action<string>? OnLog;
        /// <summary>pubkey, perms string</summary>
        public event Action<string, string?>? OnClientPaired;

        public NostrConnectSession(byte[] privKey, NostrConnectUri connectUri, bool preAuthenticated = false, Nip46Permissions? initialPerms = null)
        {
            _privKey = privKey;
            _connectUri = connectUri;
            _clientAuthenticated = preAuthenticated;
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _signerPubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();
            _handler = new Nip46Handler(_privKey, _signerPubkeyHex, _connectUri.Relays);
            var perms = initialPerms ?? Nip46Permissions.Parse(_connectUri.Perms);
            if (preAuthenticated)
            {
                _handler.MarkConnected(_connectUri.ClientPubkey);
                if (perms != null)
                    _handler.SetPermissions(_connectUri.ClientPubkey, perms);
            }
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            foreach (var relay in _connectUri.Relays)
            {
                var r = relay;
                Task.Run(() => RunRelayAsync(r, _cts.Token));
            }
        }

        public void Stop()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            lock (_sockets)
            {
                foreach (var ws in _sockets)
                {
                    try { ws.Abort(); ws.Dispose(); } catch { }
                }
                _sockets.Clear();
            }
            _handler.DisconnectAll();
            _clientAuthenticated = false;
            _connectAckSent = false;
        }

        private async Task RunRelayAsync(string relay, CancellationToken ct)
        {
            Log($"Connecting to {relay}...");

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    lock (_sockets) { _sockets.Add(ws); }

                    await ws.ConnectAsync(new Uri(relay), ct);
                    Log($"Connected to {relay}");

                    var subId = "nc_" + relay.GetHashCode().ToString("x");
                    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                    var reqJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[24133],\"authors\":[\"{_connectUri.ClientPubkey}\"],\"#p\":[\"{_signerPubkeyHex}\"],\"since\":{since}}}]";
                    await SendTextAsync(ws, reqJson, ct);

                    if (!_connectAckSent)
                    {
                        _connectAckSent = true;
                        await SendConnectResponseAsync(ws, requestId: Guid.NewGuid().ToString("N")[..16], ct);
                    }

                    var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
                    var sb = new StringBuilder();

                    while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                    {
                        sb.Clear();
                        WebSocketReceiveResult? result;
                        do
                        {
                            result = await ws.ReceiveAsync(buffer, ct);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                                break;
                            }
                            sb.Append(Encoding.UTF8.GetString(buffer.Array!, 0, result.Count));
                        } while (!result.EndOfMessage);

                        if (ws.State != WebSocketState.Open) break;

                        var msg = sb.ToString();
                        if (!string.IsNullOrEmpty(msg))
                        {
                            try { await HandleRawMessage(ws, msg, ct); }
                            catch (Exception ex) { Log("HandleRawMessage error: " + ex.Message); }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log($"Connection error ({relay}): {ex.Message}"); }
                finally
                {
                    if (ws != null)
                    {
                        lock (_sockets) { _sockets.Remove(ws); }
                        try { ws.Dispose(); } catch { }
                    }
                }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(5000, ct); } catch { break; }
            }

            Log($"Relay loop ended: {relay}");
        }

        /// <summary>NIP-46: connect response result MUST be the URI secret.</summary>
        private async Task SendConnectResponseAsync(ClientWebSocket ws, string requestId, CancellationToken ct)
        {
            try
            {
                var signedEvent = _handler.BuildConnectResponseForClient(requestId, _connectUri.Secret, _connectUri.ClientPubkey);
                await SendTextAsync(ws, $"[\"EVENT\",{signedEvent}]", ct);
                _clientAuthenticated = true;
                _handler.MarkConnected(_connectUri.ClientPubkey);
                var perms = Nip46Permissions.Parse(_connectUri.Perms);
                if (perms != null)
                    _handler.SetPermissions(_connectUri.ClientPubkey, perms);
                OnClientPaired?.Invoke(_connectUri.ClientPubkey, _connectUri.Perms);
                Log("Sent connect response (secret)");
            }
            catch (Exception ex)
            {
                Log("Failed to send connect response: " + ex.Message);
            }
        }

        private async Task HandleRawMessage(ClientWebSocket ws, string data, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            var type = root[0].GetString();
            if (type is "OK" or "NOTICE") return;
            if (type != "EVENT" || root.GetArrayLength() < 3) return;

            var ev = root[2];
            if (!ev.TryGetProperty("kind", out var kEl) || kEl.GetInt32() != 24133) return;

            if (!ev.TryGetProperty("pubkey", out var pkEl)) return;
            var senderPubkey = pkEl.GetString();
            if (string.IsNullOrEmpty(senderPubkey)) return;
            if (!string.Equals(senderPubkey, _connectUri.ClientPubkey, StringComparison.OrdinalIgnoreCase)) return;

            if (!ev.TryGetProperty("content", out var contentEl)) return;
            var encryptedContent = contentEl.GetString();
            if (string.IsNullOrEmpty(encryptedContent)) return;

            try
            {
                var (sent, method, ok, err) = await _handler.ProcessRequestAsync(
                    senderPubkey,
                    encryptedContent,
                    (text, token) => SendTextAsync(ws, text, token),
                    ct,
                    HandleConnect);

                Log($"{method} → {(ok ? "OK" : "ERR: " + err)}");
                if (!sent) Log($"Failed to send {method} response");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log($"Rejected: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"NIP-46 request failed: {ex.Message}");
            }
        }

        private string HandleConnect(string senderPubkey, JsonElement paramsArr)
        {
            if (!string.IsNullOrEmpty(_connectUri.Secret))
            {
                var found = false;
                if (paramsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in paramsArr.EnumerateArray())
                    {
                        if (el.ValueKind == JsonValueKind.String && el.GetString() == _connectUri.Secret)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (!found)
                    throw new UnauthorizedAccessException("invalid secret");
            }

            _clientAuthenticated = true;
            _handler.MarkConnected(senderPubkey);
            var connectPerms = Nip46Permissions.FromConnectParams(paramsArr);
            var perms = connectPerms ?? Nip46Permissions.Parse(_connectUri.Perms);
            if (perms != null)
                _handler.SetPermissions(senderPubkey, perms);
            OnClientPaired?.Invoke(senderPubkey, Nip46Permissions.ExtractPermsString(paramsArr) ?? _connectUri.Perms);
            Log($"Client connect accepted: {senderPubkey[..12]}...");
            return _connectUri.Secret;
        }

        private async Task<bool> SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
        {
            if (ws.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                await _wsLock.WaitAsync(ct);
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                        return true;
                    }
                }
                finally { _wsLock.Release(); }
            }
            catch (Exception ex) { AppLog.W(TAG, "SendTextAsync: " + ex.Message); }
            return false;
        }

        private void Log(string msg)
        {
            AppLog.D(TAG, msg);
            OnLog?.Invoke(msg);
        }
    }
}
