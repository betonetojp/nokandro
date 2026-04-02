using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Represents a parsed nostrconnect:// URI.
    /// Format: nostrconnect://&lt;client-pubkey&gt;?relay=wss://r1&amp;relay=wss://r2&amp;secret=xxx&amp;metadata={...}
    /// </summary>
    public sealed class NostrConnectUri
    {
        public string ClientPubkey { get; }
        public string[] Relays { get; }
        public string? Secret { get; }
        public string? Metadata { get; }
        public string? Name { get; }
        public string RawUri { get; }

        private NostrConnectUri(string clientPubkey, string[] relays, string? secret, string? metadata, string? name, string rawUri)
        {
            ClientPubkey = clientPubkey;
            Relays = relays;
            Secret = secret;
            Metadata = metadata;
            Name = name;
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
                // Extract client pubkey (host portion)
                var withoutScheme = uri["nostrconnect://".Length..];
                var qIndex = withoutScheme.IndexOf('?');
                var clientPubkey = qIndex >= 0 ? withoutScheme[..qIndex] : withoutScheme;

                if (clientPubkey.Length != 64) return false;
                // Validate hex
                foreach (var c in clientPubkey)
                {
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                        return false;
                }
                clientPubkey = clientPubkey.ToLowerInvariant();

                // Parse query parameters
                var relays = new List<string>();
                string? secret = null;
                string? metadata = null;
                string? name = null;

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
                        }
                    }
                }

                result = new NostrConnectUri(clientPubkey, [.. relays], secret, metadata, name, uri);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns this instance if relays are present; otherwise returns a copy with the given fallback relays.
        /// </summary>
        public NostrConnectUri WithFallbackRelays(params string[] fallback)
        {
            if (Relays.Length > 0) return this;
            return new NostrConnectUri(ClientPubkey, fallback, Secret, Metadata, Name, RawUri);
        }

        /// <summary>
        /// Extract a human-readable client name from the metadata JSON, if present.
        /// </summary>
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
    /// Manages a single nostrconnect:// session (client-initiated NIP-46).
    /// Connects to the relay(s) specified in the URI and handles NIP-46 requests
    /// from the known client pubkey.
    /// </summary>
    public sealed class NostrConnectSession
    {
        private const string TAG = "NostrConnectSession";
        private readonly byte[] _privKey;
        private readonly string _signerPubkeyHex;
        private readonly NostrConnectUri _connectUri;
        private CancellationTokenSource? _cts;
        private readonly List<ClientWebSocket> _sockets = [];
        private readonly SemaphoreSlim _wsLock = new(1, 1);

        public bool IsRunning { get; private set; }
        public string ClientPubkey => _connectUri.ClientPubkey;
        public string[] Relays => _connectUri.Relays;
        public string RawUri => _connectUri.RawUri;

        /// <summary>Raised when session wants to report status (runs on background thread).</summary>
        public event Action<string>? OnLog;

        public NostrConnectSession(byte[] privKey, NostrConnectUri connectUri)
        {
            _privKey = privKey;
            _connectUri = connectUri;
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _signerPubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();
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
        }

        private async Task RunRelayAsync(string relay, CancellationToken ct)
        {
            Log($"Connecting to {relay}...");
            var initialConnect = true;

            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    lock (_sockets) { _sockets.Add(ws); }

                    await ws.ConnectAsync(new Uri(relay), ct);
                    Log($"Connected to {relay}");

                    // Subscribe to kind 24133 events tagged with our pubkey from the known client
                    var subId = "nc_" + relay.GetHashCode().ToString("x");
                    var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var reqJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[24133],\"authors\":[\"{_connectUri.ClientPubkey}\"],\"#p\":[\"{_signerPubkeyHex}\"],\"since\":{since}}}]";
                    await SendTextAsync(ws, reqJson, ct);

                    // On first connect, send connect ack to the client
                    if (initialConnect)
                    {
                        initialConnect = false;
                        await SendConnectAckAsync(ws, ct);
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log($"Connection error ({relay}): {ex.Message}");
                }
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

        private async Task SendConnectAckAsync(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                // Build connect response: {"id":"<random>","result":"ack","error":""}
                var id = Guid.NewGuid().ToString("N")[..16];
                var responseJson = $"{{\"id\":{EscapeJsonString(id)},\"result\":{EscapeJsonString("ack")},\"error\":{EscapeJsonString("")}}}";

                var encrypted = NostrCrypto.EncryptNip44(responseJson, _connectUri.ClientPubkey, _privKey);

                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var tagsJson = $"[[\"p\",\"{_connectUri.ClientPubkey}\"]]";
                var eventId = ComputeEventId(_signerPubkeyHex, createdAt, 24133, tagsJson, encrypted);
                var sig = NostrCrypto.Sign(eventId, _privKey);

                var idHex = BytesToHex(eventId);
                var sigHex = BytesToHex(sig);
                var contentJson = EscapeJsonString(encrypted);

                var signedEvent = $"{{\"kind\":24133,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{_signerPubkeyHex}\",\"id\":\"{idHex}\",\"sig\":\"{sigHex}\"}}";
                await SendTextAsync(ws, $"[\"EVENT\",{signedEvent}]", ct);
                Log("Sent connect ack");
            }
            catch (Exception ex)
            {
                Log("Failed to send connect ack: " + ex.Message);
            }
        }

        private async Task HandleRawMessage(ClientWebSocket ws, string data, CancellationToken ct)
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

            if (!ev.TryGetProperty("pubkey", out var pkEl)) return;
            var senderPubkey = pkEl.GetString();
            if (string.IsNullOrEmpty(senderPubkey)) return;

            // Only accept from the known client
            if (!string.Equals(senderPubkey, _connectUri.ClientPubkey, StringComparison.OrdinalIgnoreCase)) return;

            if (!ev.TryGetProperty("content", out var contentEl)) return;
            var encryptedContent = contentEl.GetString();
            if (string.IsNullOrEmpty(encryptedContent)) return;

            var useNip44 = !encryptedContent.Contains("?iv=");
            var plaintext = NostrCrypto.Decrypt(encryptedContent, senderPubkey, _privKey);
            if (string.IsNullOrEmpty(plaintext))
            {
                AppLog.W(TAG, "Failed to decrypt NIP-46 request");
                return;
            }

            AppLog.D(TAG, "Decrypted request: " + plaintext);

            using var reqDoc = JsonDocument.Parse(plaintext);
            var reqRoot = reqDoc.RootElement;

            var id = reqRoot.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var method = reqRoot.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "" : "";
            var paramsArr = reqRoot.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Array ? pEl : default;

            string result;
            string error = "";

            try
            {
                result = method switch
                {
                    "connect" => HandleConnect(paramsArr),
                    "get_public_key" => _signerPubkeyHex,
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
                var responseJson = $"{{\"id\":{EscapeJsonString(id)},\"result\":{EscapeJsonString(result)},\"error\":{EscapeJsonString(error)}}}";

                var encrypted = useNip44
                    ? NostrCrypto.EncryptNip44(responseJson, senderPubkey, _privKey)
                    : NostrCrypto.EncryptNip04(responseJson, senderPubkey, _privKey);

                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var tagsJson = $"[[\"p\",\"{senderPubkey}\"]]";
                var eventId = ComputeEventId(_signerPubkeyHex, createdAt, 24133, tagsJson, encrypted);
                var sig = NostrCrypto.Sign(eventId, _privKey);

                var idHex = BytesToHex(eventId);
                var sigHex = BytesToHex(sig);
                var contentJson = EscapeJsonString(encrypted);

                var signedEvent = $"{{\"kind\":24133,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{_signerPubkeyHex}\",\"id\":\"{idHex}\",\"sig\":\"{sigHex}\"}}";
                var sent = await SendTextAsync(ws, $"[\"EVENT\",{signedEvent}]", ct);
                if (!sent) Log($"Failed to send {method} response (WebSocket not open)");
            }
            catch (Exception ex)
            {
                Log($"Error building/sending {method} response: {ex.Message}");
            }
        }

        private string HandleConnect(JsonElement paramsArr)
        {
            // Verify secret if URI contained one
            if (!string.IsNullOrEmpty(_connectUri.Secret))
            {
                string? clientSecret = null;
                if (paramsArr.ValueKind == JsonValueKind.Array && paramsArr.GetArrayLength() >= 2)
                    clientSecret = paramsArr[1].GetString();

                if (clientSecret != _connectUri.Secret)
                {
                    Log("Secret mismatch from client");
                    throw new UnauthorizedAccessException("invalid secret");
                }
            }

            Log($"Client connect request accepted: {_connectUri.ClientPubkey[..12]}...");
            return "ack";
        }

        private string HandleSignEvent(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 1)
                throw new ArgumentException("Missing event parameter");

            var eventParam = paramsArr[0];
            string unsignedJson;
            if (eventParam.ValueKind == JsonValueKind.String)
                unsignedJson = eventParam.GetString() ?? throw new ArgumentException("Empty event");
            else
                unsignedJson = eventParam.GetRawText();

            using var evDoc = JsonDocument.Parse(unsignedJson);
            var ev = evDoc.RootElement;

            var kind = ev.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) ? ctEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            var tags = ev.TryGetProperty("tags", out var tEl) ? tEl.GetRawText() : "[]";

            var eventId = ComputeEventId(_signerPubkeyHex, createdAt, kind, tags, content);
            var sig = NostrCrypto.Sign(eventId, _privKey);

            var idHex = BytesToHex(eventId);
            var sigHex = BytesToHex(sig);
            var contentJson = EscapeJsonString(content);

            return $"{{\"id\":\"{idHex}\",\"pubkey\":\"{_signerPubkeyHex}\",\"created_at\":{createdAt},\"kind\":{kind},\"tags\":{tags},\"content\":{contentJson},\"sig\":\"{sigHex}\"}}";
        }

        private string HandleNip04Encrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");
            var pk = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var pt = paramsArr[1].GetString() ?? throw new ArgumentException("Missing plaintext");
            return NostrCrypto.EncryptNip04(pt, pk, _privKey);
        }

        private string HandleNip04Decrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");
            var pk = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var ct = paramsArr[1].GetString() ?? throw new ArgumentException("Missing ciphertext");
            return NostrCrypto.Decrypt(ct, pk, _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
        }

        private string HandleNip44Encrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");
            var pk = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var pt = paramsArr[1].GetString() ?? throw new ArgumentException("Missing plaintext");
            return NostrCrypto.EncryptNip44(pt, pk, _privKey);
        }

        private string HandleNip44Decrypt(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 2)
                throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");
            var pk = paramsArr[0].GetString() ?? throw new ArgumentException("Missing pubkey");
            var ct = paramsArr[1].GetString() ?? throw new ArgumentException("Missing ciphertext");
            return NostrCrypto.Decrypt(ct, pk, _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
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

        private void Log(string msg)
        {
            AppLog.D(TAG, msg);
            OnLog?.Invoke(msg);
        }
    }
}
