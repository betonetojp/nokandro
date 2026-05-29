using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// NIP-46 bunker:// remote signer with Amber-style persistent client pairing.
    /// </summary>
    public sealed class NostrBunker
    {
        private const string TAG = "NostrBunker";
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly byte[] _privKey;
        private readonly string _pubkeyHex;
        private readonly string[] _relays;
        private string _secret;
        private readonly SemaphoreSlim _wsLock = new(1, 1);
        private readonly Nip46Handler _handler;
        private readonly HashSet<string> _authorizedClients;
        private readonly BunkerOptions? _options;
        private readonly HashSet<string> _processedEventIds = new();

        public bool IsRunning { get; private set; }
        private int _connectedCount;
        public int ConnectedClientCount => _connectedCount;
        public string BunkerUri => Nip46Json.BuildBunkerUri(_pubkeyHex, _relays, _secret);

        public event Action<string>? OnLog;
        /// <summary>Newly paired client (first connect).</summary>
        public event Action<string>? OnClientAuthorized;
        /// <summary>Known client reconnected.</summary>
        public event Action<string>? OnClientReconnected;
        /// <summary>Connect blocked until user approves in UI.</summary>
        public event Action<string>? OnClientPendingApproval;

        public void RemoveAuthorizedClient(string pubkey)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            _authorizedClients.Remove(pubkey);
            if (_handler.DisconnectClient(pubkey))
                _connectedCount = Math.Max(0, _connectedCount - 1);
            Log($"Authorized client removed: {pubkey[..Math.Min(12, pubkey.Length)]}...");
        }

        /// <summary>Registers a client as authorized in memory (e.g. after manual approval).</summary>
        public void AddAuthorizedClient(string pubkey)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            _authorizedClients.Add(pubkey);
            var perms = _options?.GetPermissions?.Invoke(pubkey);
            if (perms != null)
                _handler.SetPermissions(pubkey, perms);
            Log($"Client authorized in memory: {pubkey[..Math.Min(12, pubkey.Length)]}...");
        }

        public NostrBunker(byte[] privKey, string relay, string? secret = null, IEnumerable<string>? authorizedClients = null, BunkerOptions? options = null)
            : this(privKey, [relay], secret, authorizedClients, options)
        {
        }

        public NostrBunker(byte[] privKey, string[] relays, string? secret = null, IEnumerable<string>? authorizedClients = null, BunkerOptions? options = null)
        {
            _privKey = privKey;
            _options = options;
            _relays = relays.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
            if (_relays.Length == 0)
                _relays = ["wss://ephemeral.snowflare.cc/"];

            _authorizedClients = authorizedClients != null
                ? new HashSet<string>(authorizedClients, StringComparer.OrdinalIgnoreCase)
                : [];

            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _pubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();

            _secret = !string.IsNullOrEmpty(secret) ? secret : GenerateSecret();
            _handler = new Nip46Handler(_privKey, _pubkeyHex, _relays);

            foreach (var pk in _authorizedClients)
            {
                var perms = _options?.GetPermissions?.Invoke(pk);
                if (perms != null)
                    _handler.SetPermissions(pk, perms);
            }
        }

        private static string GenerateSecret()
        {
            var secretBytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secretBytes);
            return BitConverter.ToString(secretBytes).Replace("-", "").ToLowerInvariant();
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
            _handler.DisconnectAll();
            _connectedCount = 0;
        }

        private async Task RunAsync(CancellationToken ct)
        {
            Log("Bunker starting...");
            var relay = _relays[0];

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(relay), ct);
                    Log("Connected to relay");

                    var subId = "bunker";
                    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                    var reqJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[24133],\"#p\":[\"{_pubkeyHex}\"],\"since\":{since}}}]";
                    await SendTextAsync(reqJson, ct);
                    Log("Subscribed (kind:24133)");

                    var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
                    var sb = new StringBuilder();

                    while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                    {
                        sb.Clear();
                        WebSocketReceiveResult? result;
                        do
                        {
                            result = await _ws.ReceiveAsync(buffer, ct);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                                break;
                            }
                            sb.Append(Encoding.UTF8.GetString(buffer.Array!, 0, result.Count));
                        } while (!result.EndOfMessage);

                        if (_ws.State != WebSocketState.Open) break;

                        var msg = sb.ToString();
                        if (!string.IsNullOrEmpty(msg))
                        {
                            try { await HandleRawMessage(msg, ct); }
                            catch (Exception ex) { Log("HandleRawMessage error: " + ex.Message); }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log("Connection error: " + ex.Message); }
                finally
                {
                    try { _ws?.Dispose(); } catch { }
                    _ws = null;
                }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(5000, ct); } catch { break; }
            }

            IsRunning = false;
            Log("Bunker stopped");
        }

        private async Task HandleRawMessage(string data, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            var type = root[0].GetString();
            if (type == "OK")
            {
                try
                {
                    if (root.GetArrayLength() >= 3 && !root[2].GetBoolean())
                        Log($"Relay rejected: {(root.GetArrayLength() >= 4 ? root[3].GetString() : "")}");
                }
                catch { }
                return;
            }

            if (type == "NOTICE")
            {
                try { Log($"Notice: {(root.GetArrayLength() >= 2 ? root[1].GetString() : "")}"); } catch { }
                return;
            }

            if (type != "EVENT" || root.GetArrayLength() < 3) return;

            var ev = root[2];
            if (!ev.TryGetProperty("kind", out var kEl) || kEl.GetInt32() != 24133) return;

            if (ev.TryGetProperty("id", out var evIdEl))
            {
                var evId = evIdEl.GetString();
                if (!string.IsNullOrEmpty(evId))
                {
                    if (!_processedEventIds.Add(evId)) return;
                    if (_processedEventIds.Count > 1000) _processedEventIds.Clear();
                }
            }

            if (!ev.TryGetProperty("pubkey", out var pkEl)) return;
            var senderPubkey = pkEl.GetString();
            if (string.IsNullOrEmpty(senderPubkey)) return;

            if (!ev.TryGetProperty("content", out var contentEl)) return;
            var encryptedContent = contentEl.GetString();
            if (string.IsNullOrEmpty(encryptedContent)) return;

            try
            {
                var (sent, method, ok, err) = await _handler.ProcessRequestAsync(
                    senderPubkey,
                    encryptedContent,
                    SendTextAsync,
                    ct,
                    HandleConnect);

                Log($"{method} → {(ok ? "OK" : "ERR: " + err)}");
                if (!sent) Log($"Failed to send {method} response (WebSocket not open)");
            }
            catch (Exception ex)
            {
                Log($"NIP-46 request failed: {ex.Message}");
            }
        }

        private string HandleConnect(string senderPubkey, JsonElement paramsArr)
        {
            var isKnown = IsKnownClient(senderPubkey);

            if (!isKnown)
            {
                var requireApproval = _options?.RequireManualApproval?.Invoke() == true;
                if (requireApproval)
                {
                    if (ParamsContainSecret(paramsArr, _secret))
                    {
                        _options?.AddPending?.Invoke(senderPubkey);
                        OnClientPendingApproval?.Invoke(senderPubkey);
                        Log($"Pending approval: {senderPubkey[..12]}...");
                    }
                    throw new UnauthorizedAccessException("pending approval");
                }

                if (!ParamsContainSecret(paramsArr, _secret))
                    throw new UnauthorizedAccessException("invalid secret");
            }
            else
            {
                Log($"Known client reconnecting: {senderPubkey[..12]}... (secret check skipped)");
            }

            var connectPerms = Nip46Permissions.FromConnectParams(paramsArr);
            var storedPerms = _options?.GetPermissions?.Invoke(senderPubkey);
            var perms = connectPerms ?? storedPerms;
            if (connectPerms != null)
            {
                var permsStr = PermsToString(paramsArr);
                _options?.SavePermissions?.Invoke(senderPubkey, permsStr);
            }
            _handler.SetPermissions(senderPubkey, perms ?? storedPerms);
            _handler.MarkConnected(senderPubkey);
            _connectedCount++;

            var isNew = _authorizedClients.Add(senderPubkey);
            if (isNew)
            {
                Log($"New client paired: {senderPubkey[..12]}...");
                _options?.OnClientPaired?.Invoke(senderPubkey, PermsToString(paramsArr), true);
                OnClientAuthorized?.Invoke(senderPubkey);
            }
            else
            {
                _options?.OnClientPaired?.Invoke(senderPubkey, PermsToString(paramsArr), false);
                OnClientReconnected?.Invoke(senderPubkey);
            }

            Log($"Client connected: {senderPubkey[..12]}... (total: {_connectedCount})");
            return "ack";
        }

        private bool IsKnownClient(string senderPubkey)
        {
            if (_authorizedClients.Contains(senderPubkey)) return true;
            return _options?.IsAuthorized?.Invoke(senderPubkey) == true;
        }

        private static string? PermsToString(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array) return null;
            foreach (var el in paramsArr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                if (s.Contains(',') || s.Contains("sign_event") || s.Contains("nip44") || s.Contains("nip04"))
                    return s;
            }
            return null;
        }

        private static bool ParamsContainSecret(JsonElement paramsArr, string secret)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array) return false;
            foreach (var el in paramsArr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() == secret)
                    return true;
            }
            return false;
        }

        private async Task<bool> SendTextAsync(string text, CancellationToken ct)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(text);
            try
            {
                await _wsLock.WaitAsync(ct);
                try
                {
                    if (_ws.State == WebSocketState.Open)
                    {
                        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                        return true;
                    }
                }
                finally { _wsLock.Release(); }
            }
            catch (Exception ex) { AppLog.W(TAG, "SendTextAsync: " + ex.Message); }
            return false;
        }

        public void DisconnectAllClients()
        {
            _handler.DisconnectAll();
            _connectedCount = 0;
            Log("All clients disconnected");
        }

        private void Log(string msg)
        {
            AppLog.D(TAG, msg);
            OnLog?.Invoke(msg);
        }
    }
}
