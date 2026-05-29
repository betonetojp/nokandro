using System.Text;
using System.Text.Json;

namespace nokandro
{
    internal sealed class Nip46Handler
    {
        private readonly byte[] _privKey;
        private readonly string _signerPubkeyHex;
        private readonly string[] _relays;
        private readonly HashSet<string> _connectedClients = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Nip46Permissions?> _clientPermissions = new(StringComparer.OrdinalIgnoreCase);

        public Nip46Handler(byte[] privKey, string signerPubkeyHex, IEnumerable<string> relays)
        {
            _privKey = privKey;
            _signerPubkeyHex = signerPubkeyHex;
            _relays = relays.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray();
        }

        public bool IsClientConnected(string pubkey) => _connectedClients.Contains(pubkey);

        public void DisconnectAll() => _connectedClients.Clear();

        public async Task<(bool sent, string method, bool ok, string? error)> ProcessRequestAsync(
            string senderPubkey,
            string encryptedContent,
            Func<string, CancellationToken, Task<bool>> sendEventAsync,
            CancellationToken ct,
            Func<string, JsonElement, string>? handleConnect = null)
        {
            var plaintext = NostrCrypto.DecryptNip44(encryptedContent, senderPubkey, _privKey);
            if (string.IsNullOrEmpty(plaintext))
                throw new InvalidOperationException("Failed to decrypt NIP-46 request (NIP-44 required)");

            using var reqDoc = JsonDocument.Parse(plaintext);
            var reqRoot = reqDoc.RootElement;
            var id = reqRoot.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var method = reqRoot.TryGetProperty("method", out var methodEl) ? methodEl.GetString() ?? "" : "";
            var paramsArr = reqRoot.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Array ? pEl : default;

            if (method is not "connect" and not "ping" and not "get_public_key" && !_connectedClients.Contains(senderPubkey))
                throw new UnauthorizedAccessException("client not connected");

            string result;
            string error = "";
            try
            {
                result = method switch
                {
                    "connect" => handleConnect != null
                        ? handleConnect(senderPubkey, paramsArr)
                        : HandleConnect(senderPubkey, paramsArr),
                    "get_public_key" => _signerPubkeyHex,
                    "sign_event" => HandleSignEvent(senderPubkey, paramsArr),
                    "nip04_encrypt" => HandleNip04Encrypt(senderPubkey, paramsArr),
                    "nip04_decrypt" => HandleNip04Decrypt(senderPubkey, paramsArr),
                    "nip44_encrypt" => HandleNip44Encrypt(senderPubkey, paramsArr),
                    "nip44_decrypt" => HandleNip44Decrypt(senderPubkey, paramsArr),
                    "switch_relays" => HandleSwitchRelays(),
                    "ping" => "pong",
                    _ => throw new NotSupportedException($"Unknown method: {method}")
                };
            }
            catch (Exception ex)
            {
                result = "";
                error = ex.Message;
            }

            var responseJson = $"{{\"id\":{Nip46Json.EscapeJsonString(id)},\"result\":{Nip46Json.EscapeJsonString(result)},\"error\":{Nip46Json.EscapeJsonString(error)}}}";
            var encrypted = NostrCrypto.EncryptNip44(responseJson, senderPubkey, _privKey);
            var signedEvent = Nip46Json.BuildSignedKind24133(_signerPubkeyHex, senderPubkey, encrypted, _privKey);
            var sent = await sendEventAsync($"[\"EVENT\",{signedEvent}]", ct);
            return (sent, method, string.IsNullOrEmpty(error), string.IsNullOrEmpty(error) ? null : error);
        }

        public string BuildConnectResponseForClient(string requestId, string connectResult, string clientPubkey)
        {
            var responseJson = $"{{\"id\":{Nip46Json.EscapeJsonString(requestId)},\"result\":{Nip46Json.EscapeJsonString(connectResult)},\"error\":{Nip46Json.EscapeJsonString("")}}}";
            var encrypted = NostrCrypto.EncryptNip44(responseJson, clientPubkey, _privKey);
            return Nip46Json.BuildSignedKind24133(_signerPubkeyHex, clientPubkey, encrypted, _privKey);
        }

        private string HandleConnect(string senderPubkey, JsonElement paramsArr)
        {
            ApplyPermissions(senderPubkey, Nip46Permissions.FromConnectParams(paramsArr));
            _connectedClients.Add(senderPubkey);
            return "ack";
        }

        private string HandleSwitchRelays()
        {
            if (_relays.Length == 0) return "null";
            var items = string.Join(",", _relays.Select(r => Nip46Json.EscapeJsonString(r)));
            return $"[{items}]";
        }

        private string HandleSignEvent(string senderPubkey, JsonElement paramsArr)
        {
            GetPermissions(senderPubkey)?.EnsureAllowed("sign_event", paramsArr);
            if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 1)
                throw new ArgumentException("Missing event parameter");
            var eventParam = paramsArr[0];
            var unsignedJson = eventParam.ValueKind == JsonValueKind.String
                ? eventParam.GetString() ?? throw new ArgumentException("Empty event")
                : eventParam.GetRawText();
            return Nip46Json.SignUnsignedEvent(unsignedJson, _signerPubkeyHex, _privKey);
        }

        private string HandleNip04Encrypt(string senderPubkey, JsonElement paramsArr)
        {
            GetPermissions(senderPubkey)?.EnsureAllowed("nip04_encrypt", paramsArr);
            if (paramsArr.GetArrayLength() < 2) throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");
            return NostrCrypto.EncryptNip04(paramsArr[1].GetString() ?? "", paramsArr[0].GetString() ?? "", _privKey);
        }

        private string HandleNip04Decrypt(string senderPubkey, JsonElement paramsArr)
        {
            GetPermissions(senderPubkey)?.EnsureAllowed("nip04_decrypt", paramsArr);
            if (paramsArr.GetArrayLength() < 2) throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");
            return NostrCrypto.Decrypt(paramsArr[1].GetString() ?? "", paramsArr[0].GetString() ?? "", _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
        }

        private string HandleNip44Encrypt(string senderPubkey, JsonElement paramsArr)
        {
            GetPermissions(senderPubkey)?.EnsureAllowed("nip44_encrypt", paramsArr);
            if (paramsArr.GetArrayLength() < 2) throw new ArgumentException("params: [thirdPartyPubkey, plaintext]");
            return NostrCrypto.EncryptNip44(paramsArr[1].GetString() ?? "", paramsArr[0].GetString() ?? "", _privKey);
        }

        private string HandleNip44Decrypt(string senderPubkey, JsonElement paramsArr)
        {
            GetPermissions(senderPubkey)?.EnsureAllowed("nip44_decrypt", paramsArr);
            if (paramsArr.GetArrayLength() < 2) throw new ArgumentException("params: [thirdPartyPubkey, ciphertext]");
            return NostrCrypto.DecryptNip44(paramsArr[1].GetString() ?? "", paramsArr[0].GetString() ?? "", _privKey)
                ?? throw new InvalidOperationException("Decryption failed");
        }

        public void SetPermissions(string pubkey, Nip46Permissions? permissions) =>
            ApplyPermissions(pubkey, permissions);

        public void MarkConnected(string pubkey) => _connectedClients.Add(pubkey);

        private void ApplyPermissions(string pubkey, Nip46Permissions? permissions)
        {
            if (permissions == null) return;
            _clientPermissions[pubkey] = permissions;
        }

        private Nip46Permissions? GetPermissions(string pubkey) =>
            _clientPermissions.TryGetValue(pubkey, out var p) ? p : null;
    }
}
