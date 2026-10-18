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
        private readonly string[]? _switchRelays;
        private readonly NostrConnectUri _connectUri;
        private readonly Nip46Handler _handler;
        private CancellationTokenSource? _cts;
        private readonly List<NostrWebSocketClient> _wsClients = [];
        private readonly HashSet<string> _connectAckSentRelays = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _processedEventIds = new();
        private bool _clientAuthenticated;
        private readonly bool _preAuthenticated;
        private string? _initialConnectResponseEvent;

        public bool IsRunning { get; private set; }
        public string ClientPubkey => _connectUri.ClientPubkey;
        public string[] Relays => _connectUri.Relays;
        public string RawUri => _connectUri.RawUri;

        public event Action<string>? OnLog;
        /// <summary>pubkey, perms string</summary>
        public event Action<string, string?>? OnClientPaired;
        /// <summary>Client activity performed (pubkey, method summary).</summary>
        public event Action<string, string>? OnActivity;

        public NostrConnectSession(byte[] privKey, NostrConnectUri connectUri, bool preAuthenticated = false, Nip46Permissions? initialPerms = null, IEnumerable<string>? switchRelays = null)
        {
            _privKey = privKey;
            _connectUri = connectUri;
            _switchRelays = switchRelays?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
            _clientAuthenticated = preAuthenticated;
            _preAuthenticated = preAuthenticated;
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _signerPubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();
            _handler = new Nip46Handler(_privKey, _signerPubkeyHex, _connectUri.Relays, _switchRelays);
            _handler.OnActivity += (pk, summary) => OnActivity?.Invoke(pk, summary);
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

            var targetRelays = new List<string>(_connectUri.Relays);
            if (_switchRelays != null)
            {
                foreach (var relay in _switchRelays)
                {
                    if (!string.IsNullOrEmpty(relay) && !targetRelays.Any(r => string.Equals(r.TrimEnd('/'), relay.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                    {
                        targetRelays.Add(relay);
                    }
                }
            }

            lock (_wsClients)
            {
                _wsClients.Clear();
                _connectAckSentRelays.Clear();
                _initialConnectResponseEvent = null;
                foreach (var relay in targetRelays)
                {
                    var client = new NostrWebSocketClient(relay, $"NostrConnectSession_{relay.GetHashCode():x}");
                    _wsClients.Add(client);

                    var r = relay;
                    var cToken = _cts.Token;

                    client.OnMessageReceived += async msg =>
                    {
                        try { await HandleRawMessage(client, msg, cToken); }
                        catch (System.OperationCanceledException) { }
                        catch (Exception ex) { Log("HandleRawMessage error: " + ex.Message); }
                    };

                    client.OnStateChanged += async state =>
                    {
                        if (state == WebSocketState.Open)
                        {
                            Log($"Connected to {r}");
                            var subId = "nc_" + r.GetHashCode().ToString("x");
                            var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                            var reqJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[24133],\"authors\":[\"{_connectUri.ClientPubkey}\"],\"#p\":[\"{_signerPubkeyHex}\"],\"since\":{since}}}]";
                            await SendTextAsync(client, reqJson, cToken);

                            bool shouldSendAck = false;
                            lock (_connectAckSentRelays)
                            {
                                if (!_preAuthenticated && _connectAckSentRelays.Add(r))
                                    shouldSendAck = true;
                            }
                            if (shouldSendAck)
                            {
                                await SendConnectResponseAsync(cToken);
                            }
                        }
                    };

                    client.OnLog += msg => Log(msg);
                    client.Start();
                }
            }
        }

        public void Stop()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            lock (_wsClients)
            {
                foreach (var client in _wsClients)
                {
                    try { client.Stop(); client.Dispose(); } catch { }
                }
                _wsClients.Clear();
            }
            lock (_connectAckSentRelays)
            {
                _connectAckSentRelays.Clear();
            }
            _handler.DisconnectAll();
            _clientAuthenticated = false;
            _initialConnectResponseEvent = null;
        }

        /// <summary>NIP-46: initial nostrconnect:// response result MUST be the URI secret.</summary>
        private async Task SendConnectResponseAsync(CancellationToken ct)
        {
            try
            {
                string signedEvent;
                lock (_connectAckSentRelays)
                {
                    _initialConnectResponseEvent ??= _handler.BuildConnectResponseForClient(
                        Guid.NewGuid().ToString("N")[..16],
                        _connectUri.Secret,
                        _connectUri.ClientPubkey);
                    signedEvent = _initialConnectResponseEvent;
                }
                await BroadcastTextAsync($"[\"EVENT\",{signedEvent}]", ct);
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

        private async Task HandleRawMessage(NostrWebSocketClient client, string data, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            var type = root[0].GetString();
            if (type is "OK" or "NOTICE") return;
            if (type != "EVENT" || root.GetArrayLength() < 3) return;

            var ev = root[2];
            if (!ev.TryGetProperty("kind", out var kEl) || kEl.GetInt32() != 24133) return;

            if (ev.TryGetProperty("id", out var evIdEl))
            {
                var evId = evIdEl.GetString();
                if (!string.IsNullOrEmpty(evId))
                {
                    lock (_processedEventIds)
                    {
                        if (!_processedEventIds.Add(evId)) return;
                        if (_processedEventIds.Count > 1000) _processedEventIds.Clear();
                    }
                }
            }

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
                    BroadcastTextAsync,
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
            if (!_clientAuthenticated
                && !string.IsNullOrEmpty(_connectUri.Secret)
                && paramsArr.ValueKind == JsonValueKind.Array
                && paramsArr.GetArrayLength() >= 2
                && paramsArr[1].ValueKind == JsonValueKind.String)
            {
                var providedSecret = paramsArr[1].GetString();
                if (!string.IsNullOrEmpty(providedSecret)
                    && !providedSecret.Contains(',')
                    && !providedSecret.StartsWith("sign_event", StringComparison.Ordinal)
                    && !providedSecret.StartsWith("nip", StringComparison.Ordinal)
                    && !string.Equals(providedSecret, senderPubkey, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(providedSecret, _signerPubkeyHex, StringComparison.OrdinalIgnoreCase)
                    && providedSecret != _connectUri.Secret)
                {
                    throw new UnauthorizedAccessException("invalid secret");
                }
            }

            _clientAuthenticated = true;
            _handler.MarkConnected(senderPubkey);
            var connectPerms = Nip46Permissions.FromConnectParams(paramsArr);
            var perms = connectPerms ?? Nip46Permissions.Parse(_connectUri.Perms);
            if (perms != null)
                _handler.SetPermissions(senderPubkey, perms);
            OnClientPaired?.Invoke(senderPubkey, Nip46Permissions.ExtractPermsString(paramsArr) ?? _connectUri.Perms);
            Log($"Client connect accepted: {senderPubkey[..12]}...");
            return "ack";
        }

        private async Task<bool> BroadcastTextAsync(string text, CancellationToken ct)
        {
            List<NostrWebSocketClient> clients;
            lock (_wsClients) { clients = [.. _wsClients]; }
            var anySent = false;
            foreach (var c in clients)
            {
                try
                {
                    if (await c.SendTextAsync(text, ct))
                        anySent = true;
                }
                catch { }
            }
            return anySent;
        }

        private async Task<bool> SendTextAsync(NostrWebSocketClient client, string text, CancellationToken ct)
        {
            return await client.SendTextAsync(text, ct);
        }

        private void Log(string msg)
        {
            AppLog.D(TAG, msg);
            OnLog?.Invoke(msg);
        }
    }
}
