using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    internal static class Nip46Json
    {
        public static string EscapeJsonString(string? text)
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

        public static byte[] ComputeEventId(string pubkey, long createdAt, int kind, string tagsJson, string content)
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

        public static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        public static string SignUnsignedEvent(string unsignedJson, string pubkeyHex, byte[] privKey)
        {
            using var evDoc = JsonDocument.Parse(unsignedJson);
            var ev = evDoc.RootElement;

            var kind = ev.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) ? ctEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            var tags = ev.TryGetProperty("tags", out var tEl) ? tEl.GetRawText() : "[]";

            var eventId = ComputeEventId(pubkeyHex, createdAt, kind, tags, content);
            var sig = NostrCrypto.Sign(eventId, privKey);

            return $"{{\"id\":\"{BytesToHex(eventId)}\",\"pubkey\":\"{pubkeyHex}\",\"created_at\":{createdAt},\"kind\":{kind},\"tags\":{tags},\"content\":{EscapeJsonString(content)},\"sig\":\"{BytesToHex(sig)}\"}}";
        }

        public static string BuildSignedKind24133(string signerPubkeyHex, string clientPubkeyHex, string encryptedContent, byte[] privKey)
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tagsJson = $"[[\"p\",\"{clientPubkeyHex}\"]]";
            var eventId = ComputeEventId(signerPubkeyHex, createdAt, 24133, tagsJson, encryptedContent);
            var sig = NostrCrypto.Sign(eventId, privKey);
            var contentJson = EscapeJsonString(encryptedContent);
            return $"{{\"kind\":24133,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{signerPubkeyHex}\",\"id\":\"{BytesToHex(eventId)}\",\"sig\":\"{BytesToHex(sig)}\"}}";
        }

        public static string BuildBunkerUri(string pubkeyHex, IEnumerable<string> relays, string secret)
        {
            var sb = new StringBuilder();
            sb.Append("bunker://");
            sb.Append(pubkeyHex);
            var first = true;
            foreach (var relay in relays)
            {
                if (string.IsNullOrWhiteSpace(relay)) continue;
                sb.Append(first ? '?' : '&');
                sb.Append("relay=").Append(Uri.EscapeDataString(relay.Trim()));
                first = false;
            }
            sb.Append(first ? '?' : '&');
            sb.Append("secret=").Append(secret);
            return sb.ToString();
        }
    }
}
