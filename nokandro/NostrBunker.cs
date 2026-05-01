using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Minimal NIP-46 (Nostr Connect) bunker implementation.
    /// Supports both NIP-04 and NIP-44 encryption (auto-detected from client request).
    /// Holds the user's private key and signs events on behalf of remote clients.
    /// </summary>
    public sealed class NostrBunker
    {
        private const string TAG = "NostrBunker";
        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private readonly byte[] _privKey;
        private readonly string _pubkeyHex;
        private readonly string _relay;
        private readonly string _secret;
        private readonly SemaphoreSlim _wsLock = new(1, 1);

        private readonly HashSet<string> _connectedClients = new();
        private readonly HashSet<string> _authorizedClients;
        private readonly HashSet<string> _processedEventIds = new();

        public bool IsRunning { get; private set; }
        public int ConnectedClientCount => _connectedClients.Count;
        public string BunkerUri { get; }

        /// <summary>Raised when bunker wants to report status (runs on background thread).</summary>
        public event Action<string>? OnLog;

        /// <summary>Raised when a new client pubkey is authorized. Persist this for reconnection support.</summary>
        public event Action<string>? OnClientAuthorized;

        /// <summary>
        /// Remove an authorized client (and disconnect if currently connected).
        /// </summary>
        public void RemoveAuthorizedClient(string pubkey)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            try
            {
                if (_authorizedClients.Remove(pubkey))
                {
                    _connectedClients.Remove(pubkey);
                    Log($"Authorized client removed: {pubkey[..Math.Min(12, pubkey.Length)]}...");
                }
            }
            catch { }
        }

        public NostrBunker(byte[] privKey, string relay, string? secret = null, IEnumerable<string>? authorizedClients = null)
        {
            _privKey = privKey;
            _relay = relay;
            _authorizedClients = authorizedClients != null ? new HashSet<string>(authorizedClients) : [];
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _pubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();

            if (!string.IsNullOrEmpty(secret))
            {
                _secret = secret;
            }
            else
            {
                var secretBytes = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(secretBytes);
                _secret = BitConverter.ToString(secretBytes).Replace("-", "").ToLowerInvariant();
            }

            BunkerUri = $"bunker://{_pubkeyHex}?relay={Uri.EscapeDataString(relay)}&secret={_secret}";
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
            _connectedClients.Clear();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            Log("Bunker starting...");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_relay), ct);
                    Log("Connected to relay");

                    // Subscribe to kind 24133 events tagged with our pubkey
                    // Use a 60-second buffer to catch events published just before (re)connection
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log("Connection error: " + ex.Message);
                }
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
                    var accepted = root.GetArrayLength() >= 3 && root[2].GetBoolean();
                    if (!accepted)
                    {
                        var msg = root.GetArrayLength() >= 4 ? root[3].GetString() ?? "" : "";
                        Log($"Relay rejected: {msg}");
                    }
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

            // Deduplicate: skip events we have already processed
            if (ev.TryGetProperty("id", out var evIdEl))
            {
                var evId = evIdEl.GetString();
                if (!string.IsNullOrEmpty(evId))
                {
                    if (!_processedEventIds.Add(evId)) return;
                    // Keep the set from growing unbounded
                    if (_processedEventIds.Count > 1000) _processedEventIds.Clear();
                }
            }

            // Get sender pubkey
            if (!ev.TryGetProperty("pubkey", out var pkEl)) return;
            var senderPubkey = pkEl.GetString();
            if (string.IsNullOrEmpty(senderPubkey)) return;

            // Get encrypted content
            if (!ev.TryGetProperty("content", out var contentEl)) return;
            var encryptedContent = contentEl.GetString();
            if (string.IsNullOrEmpty(encryptedContent)) return;

            // Decrypt (auto-detect NIP-04 / NIP-44)
            var useNip44 = !encryptedContent.Contains("?iv=");
            var plaintext = NostrCrypto.Decrypt(encryptedContent, senderPubkey, _privKey);
            if (string.IsNullOrEmpty(plaintext))
            {
                AppLog.W(TAG, "Failed to decrypt NIP-46 request");
                return;
            }

            AppLog.D(TAG, "Decrypted request: " + plaintext);

            // Parse request JSON: {"id":"...","method":"...","params":[...]}
            using var reqDoc = JsonDocument.Parse(plaintext);
            var reqRoot = reqDoc.RootElement;

            var id = reqRoot.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var method = reqRoot.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "" : "";
            var paramsArr = reqRoot.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Array ? pEl : default;

            // Require connection for methods other than connect, ping, and get_public_key
            if (method is not "connect" and not "ping" and not "get_public_key" && !_connectedClients.Contains(senderPubkey))
            {
                Log($"Rejected '{method}' from unconnected client {senderPubkey[..12]}...");
                return;
            }

            string result;
            string error = "";

            try
            {
                result = method switch
                {
                    "connect" => HandleConnect(senderPubkey, paramsArr),
                    "get_public_key" => _pubkeyHex,
                    "sign_event" => HandleSignEvent(paramsArr),
                    "nip04_encrypt" => HandleNip04Encrypt(paramsArr),
                    "nip04_decrypt" => HandleNip04Decrypt(paramsArr),
                    "nip44_encrypt" => HandleNip44Encrypt(paramsArr),
                    "nip44_decrypt" => HandleNip44Decrypt(paramsArr),
                    "ping" => "pong",
                    _ => throw new NotSupportedException($"Unknown method: {method}")
                };
            }
            catch (Exception ex)
            {
                result = "";
                error = ex.Message;
                Log($"Error handling '{method}': {error}");
            }

            Log($"{method} → {(string.IsNullOrEmpty(error) ? "OK" : "ERR: " + error)}");

            try
            {
                // Build response JSON (manual to avoid reflection-based JsonSerializer under trimming)
                var responseJson = $"{{\"id\":{EscapeJsonString(id)},\"result\":{EscapeJsonString(result)},\"error\":{EscapeJsonString(error)}}}";

                // Encrypt response using the same method the client used
                var encrypted = useNip44
                    ? NostrCrypto.EncryptNip44(responseJson, senderPubkey, _privKey)
                    : NostrCrypto.EncryptNip04(responseJson, senderPubkey, _privKey);

                // Build and sign kind 24133 event
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var tagsJson = $"[[\"p\",\"{senderPubkey}\"]]";
                var eventId = ComputeEventId(_pubkeyHex, createdAt, 24133, tagsJson, encrypted);
                var sig = NostrCrypto.Sign(eventId, _privKey);

                var idHex = BytesToHex(eventId);
                var sigHex = BytesToHex(sig);
                var contentJson = EscapeJsonString(encrypted);

                var signedEvent = $"{{\"kind\":24133,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{_pubkeyHex}\",\"id\":\"{idHex}\",\"sig\":\"{sigHex}\"}}";
                var sent = await SendTextAsync($"[\"EVENT\",{signedEvent}]", ct);
                if (!sent) Log($"Failed to send {method} response (WebSocket not open)");
            }
            catch (Exception ex)
            {
                Log($"Error building/sending {method} response: {ex.Message}");
            }
        }

        private string HandleConnect(string senderPubkey, JsonElement paramsArr)
        {
            // params: [<remote_user_pubkey>, <secret>?, ...]
            string? clientSecret = null;
            if (paramsArr.ValueKind == JsonValueKind.Array && paramsArr.GetArrayLength() >= 2)
            {
                clientSecret = paramsArr[1].GetString();
            }
            if (_authorizedClients.Contains(senderPubkey))
            {
                // Known client reconnecting — skip secret check (may have stale cached token)
                Log($"Known client reconnecting: {senderPubkey[..12]}...");
            }
            else
            {
                // For security, require secret even on initial connect. Reject if missing or wrong.
                if (string.IsNullOrEmpty(clientSecret) || clientSecret != _secret)
                {
                    var receivedPreview = string.IsNullOrEmpty(clientSecret) ? "(missing)" : clientSecret[..Math.Min(8, clientSecret.Length)];
                    Log($"Secret mismatch or missing: received={receivedPreview}... expected={_secret[..8]}...");
                    throw new UnauthorizedAccessException("invalid secret");
                }
            }
            _connectedClients.Add(senderPubkey);
            if (_authorizedClients.Add(senderPubkey))
            {
                Log($"New client authorized: {senderPubkey[..12]}...");
                OnClientAuthorized?.Invoke(senderPubkey);
            }
            Log($"Client connected: {senderPubkey[..12]}... (total: {_connectedClients.Count})");
            return "ack";
        }

        private string HandleSignEvent(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 1)
                throw new ArgumentException("Missing event parameter");

            var eventParam = paramsArr[0];
            string unsignedJson;
            if (eventParam.ValueKind == JsonValueKind.String)
            {
                unsignedJson = eventParam.GetString() ?? throw new ArgumentException("Empty event");
            }
            else
            {
                unsignedJson = eventParam.GetRawText();
            }

            // Parse the unsigned event
            using var evDoc = JsonDocument.Parse(unsignedJson);
            var ev = evDoc.RootElement;

            var kind = ev.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) ? ctEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            var tags = ev.TryGetProperty("tags", out var tEl) ? tEl.GetRawText() : "[]";

            // Compute event id and sign
            var eventId = ComputeEventId(_pubkeyHex, createdAt, kind, tags, content);
            var sig = NostrCrypto.Sign(eventId, _privKey);

            var idHex = BytesToHex(eventId);
            var sigHex = BytesToHex(sig);
            var contentJson = EscapeJsonString(content);

            return $"{{\"id\":\"{idHex}\",\"pubkey\":\"{_pubkeyHex}\",\"created_at\":{createdAt},\"kind\":{kind},\"tags\":{tags},\"content\":{contentJson},\"sig\":\"{sigHex}\"}}";
        }

        private string HandleNip04Encrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");

            var thirdPartyPubkey = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var plaintext = paramsArr[1].GetString() ?? throw new ArgumentException("Missing plaintext");

            return NostrCrypto.EncryptNip04(plaintext, thirdPartyPubkey, _privKey);
        }

        private string HandleNip04Decrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");

            var thirdPartyPubkey = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var ciphertext = paramsArr[1].GetString() ?? throw new ArgumentException("Missing ciphertext");

            return NostrCrypto.Decrypt(ciphertext, thirdPartyPubkey, _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
        }

        private string HandleNip44Encrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");

            var thirdPartyPubkey = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var plaintext = paramsArr[1].GetString() ?? throw new ArgumentException("Missing plaintext");

            return NostrCrypto.EncryptNip44(plaintext, thirdPartyPubkey, _privKey);
        }

        private string HandleNip44Decrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");

            var thirdPartyPubkey = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var ciphertext = paramsArr[1].GetString() ?? throw new ArgumentException("Missing ciphertext");

            return NostrCrypto.Decrypt(ciphertext, thirdPartyPubkey, _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
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

        private static byte[] ComputeEventId(string pubkey, long createdAt, int kind, string tagsJson, string content)
        {
            var sb = new StringBuilder();
            sb.Append("[0,\"");
            sb.Append(pubkey);
            sb.Append("\",");
            sb.Append(createdAt);
            sb.Append(',');
            sb.Append(kind);
            sb.Append(',');
            sb.Append(tagsJson);
            sb.Append(',');
            sb.Append(EscapeJsonString(content));
            sb.Append(']');

            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string EscapeJsonString(string text)
        {
            if (text == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        public void DisconnectAllClients()
        {
            _connectedClients.Clear();
            Log("All clients disconnected");
        }

        private void Log(string msg)
        {
            AppLog.D(TAG, msg);
            OnLog?.Invoke(msg);
        }
    }
}
