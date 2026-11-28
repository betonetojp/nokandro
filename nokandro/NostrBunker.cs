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
        public static readonly string[] DefaultRelays = ["wss://ephemeral.snowflare.cc/", "wss://yabu.me/"];

        private readonly List<NostrWebSocketClient> _wsClients = [];
        private CancellationTokenSource? _cts;
        private readonly byte[] _privKey;
        private readonly string _pubkeyHex;
        private readonly string[] _relays;
        private string _secret;
        private readonly Nip46Handler _handler;
        private readonly object _authLock = new();
        private readonly HashSet<string> _authorizedClients;
        private readonly BunkerOptions? _options;
        private readonly HashSet<string> _processedEventIds = new();

        public bool IsRunning { get; private set; }
        private int _connectedCount;
        public int ConnectedClientCount => _connectedCount;
        public string BunkerUri => Nip46Json.BuildBunkerUri(_pubkeyHex, _relays, _secret);
        public string[] Relays => [.. _relays];
        public string CurrentSecret => _secret;

        public event Action<string>? OnLog;
        /// <summary>Newly paired client (first connect).</summary>
        public event Action<string>? OnClientAuthorized;
        /// <summary>Known client reconnected.</summary>
        public event Action<string>? OnClientReconnected;
        /// <summary>Client activity performed (pubkey, method summary).</summary>
        public event Action<string, string>? OnActivity;

        public static string[] ParseRelayList(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var norm = part.TrimEnd('/');
                if (seen.Add(norm))
                    list.Add(part);
            }
            return [.. list];
        }

        public IReadOnlyCollection<string> GetAuthorizedClientPubkeys()
        {
            lock (_authLock) { return [.. _authorizedClients]; }
        }

        public void AddAuthorizedClient(string pubkey, Nip46Permissions? perms = null)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            lock (_authLock) { _authorizedClients.Add(pubkey); }
            _handler.MarkConnected(pubkey);
            if (perms != null)
                _handler.SetPermissions(pubkey, perms);
            Log($"Authorized client added: {pubkey[..Math.Min(12, pubkey.Length)]}...");
        }

        public void RemoveAuthorizedClient(string pubkey)
        {
            if (string.IsNullOrEmpty(pubkey)) return;
            lock (_authLock) { _authorizedClients.Remove(pubkey); }
            if (_handler.DisconnectClient(pubkey))
                _connectedCount = Math.Max(0, _connectedCount - 1);
            Log($"Authorized client removed: {pubkey[..Math.Min(12, pubkey.Length)]}...");
        }

        /// <summary>Rotates the global bunker secret; returns the new secret.</summary>
        public string RotateGlobalSecret()
        {
            _secret = GenerateSecret();
            Log("Bunker global secret rotated");
            return _secret;
        }

        public static string GenerateBunkerSecret() => GenerateSecret();

        public NostrBunker(byte[] privKey, string relay, string? secret = null, IEnumerable<string>? authorizedClients = null, BunkerOptions? options = null)
            : this(privKey, ParseRelayList(relay), secret, authorizedClients, options)
        {
        }

        public NostrBunker(byte[] privKey, string[] relays, string? secret = null, IEnumerable<string>? authorizedClients = null, BunkerOptions? options = null)
        {
            _privKey = privKey;
            _options = options;

            var parsedRelays = new List<string>();
            var seenRelays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (relays != null)
            {
                foreach (var entry in relays)
                {
                    foreach (var r in ParseRelayList(entry))
                    {
                        if (seenRelays.Add(r.TrimEnd('/')))
                            parsedRelays.Add(r);
                    }
                }
            }
            _relays = parsedRelays.Count > 0 ? [.. parsedRelays] : [.. DefaultRelays];

            _authorizedClients = authorizedClients != null
                ? new HashSet<string>(authorizedClients, StringComparer.OrdinalIgnoreCase)
                : [];

            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            _pubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();

            _secret = !string.IsNullOrEmpty(secret) ? secret : GenerateSecret();
            _handler = new Nip46Handler(_privKey, _pubkeyHex, _relays);
            _handler.OnActivity += (pk, summary) => OnActivity?.Invoke(pk, summary);

            foreach (var pk in _authorizedClients)
            {
                _handler.MarkConnected(pk);
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
            StopAllClients();
            _handler.DisconnectAll();
            _connectedCount = 0;
        }

        private void StopAllClients()
        {
            List<NostrWebSocketClient> clients;
            lock (_wsClients)
            {
                clients = [.. _wsClients];
                _wsClients.Clear();
            }
            foreach (var client in clients)
            {
                try { client.Stop(); client.Dispose(); } catch { }
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            Log(_relays.Length == 1
                ? $"Bunker starting ({_relays[0]})..."
                : $"Bunker starting ({_relays.Length} relays)...");

            StopAllClients();
            var created = new List<NostrWebSocketClient>(_relays.Length);
            foreach (var relay in _relays)
            {
                var r = relay;
                var tag = _relays.Length == 1 ? "NostrBunker" : $"NostrBunker_{r.GetHashCode():x}";
                var client = new NostrWebSocketClient(r, tag);
                client.OnMessageReceived += async msg =>
                {
                    try { await HandleRawMessage(msg, ct); }
                    catch (System.OperationCanceledException) { }
                    catch (Exception ex) { Log("HandleRawMessage error: " + ex.Message); }
                };
                client.OnStateChanged += async state =>
                {
                    if (state == WebSocketState.Open)
                    {
                        Log(_relays.Length == 1 ? "Connected to relay" : $"Connected to {r}");
                        var subId = _relays.Length == 1 ? "bunker" : "bunker_" + r.GetHashCode().ToString("x");
                        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
                        var reqJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[24133],\"#p\":[\"{_pubkeyHex}\"],\"since\":{since}}}]";
                        await client.SendTextAsync(reqJson, ct);
                        Log(_relays.Length == 1 ? "Subscribed (kind:24133)" : $"Subscribed (kind:24133) on {r}");
                    }
                };
                client.OnLog += msg => Log(msg);
                created.Add(client);
            }

            bool canceled;
            lock (_wsClients)
            {
                canceled = ct.IsCancellationRequested;
                if (!canceled)
                    _wsClients.AddRange(created);
            }

            if (canceled)
            {
                foreach (var client in created)
                {
                    try { client.Stop(); client.Dispose(); } catch { }
                }
                IsRunning = false;
                Log("Bunker stopped");
                return;
            }

            foreach (var client in created)
            {
                client.Start();
            }

            try
            {
                await Task.Delay(-1, ct);
            }
            catch (System.OperationCanceledException) { }
            finally
            {
                StopAllClients();
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
            var isNew = !IsKnownClient(senderPubkey);

            if (isNew)
            {
                if (!ParamsContainSecret(paramsArr, _secret))
                    throw new UnauthorizedAccessException("invalid secret");
                Log($"New client auto-authorized: {senderPubkey[..12]}...");
            }
            else
            {
                Log($"Known client reconnecting: {senderPubkey[..12]}... (secret check skipped)");
            }

            var permsStr = Nip46Permissions.ExtractPermsString(paramsArr);
            var connectPerms = Nip46Permissions.Parse(permsStr);
            var storedPerms = _options?.GetPermissions?.Invoke(senderPubkey);
            var perms = connectPerms ?? storedPerms;
            if (connectPerms != null)
            {
                _options?.SavePermissions?.Invoke(senderPubkey, permsStr);
            }
            _handler.SetPermissions(senderPubkey, perms ?? storedPerms);

            var clientMeta = Nip46ClientMetadata.FromConnectParams(paramsArr);
            if (clientMeta != null)
            {
                _options?.SaveClientMetadata?.Invoke(senderPubkey, clientMeta);
            }

            if (!_handler.IsClientConnected(senderPubkey))
                _connectedCount++;
            _handler.MarkConnected(senderPubkey);
            lock (_authLock) { _authorizedClients.Add(senderPubkey); }

            if (isNew)
            {
                Log($"New client paired: {senderPubkey[..12]}...");
                _options?.OnClientPaired?.Invoke(senderPubkey, permsStr, true);
                OnClientAuthorized?.Invoke(senderPubkey);
            }
            else
            {
                _options?.OnClientPaired?.Invoke(senderPubkey, permsStr, false);
                OnClientReconnected?.Invoke(senderPubkey);
            }

            Log($"Client connected: {senderPubkey[..12]}... (total: {_connectedCount})");
            return "ack";
        }

        private bool IsKnownClient(string senderPubkey)
        {
            lock (_authLock)
            {
                if (_authorizedClients.Contains(senderPubkey)) return true;
            }
            return _options?.IsAuthorized?.Invoke(senderPubkey) == true;
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
            List<NostrWebSocketClient> clients;
            lock (_wsClients) { clients = [.. _wsClients]; }
            var anySent = false;
            foreach (var client in clients)
            {
                try
                {
                    if (await client.SendTextAsync(text, ct))
                        anySent = true;
                }
                catch { }
            }
            return anySent;
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
