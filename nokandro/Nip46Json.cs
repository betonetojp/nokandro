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
            var sb = new StringBuilder(text.Length + 16);
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
                        if (c < ' ')
                        {
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        public static List<List<string>> ParseTags(JsonElement tagsElement)
        {
            var result = new List<List<string>>();
            if (tagsElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in tagsElement.EnumerateArray())
            {
                var tag = new List<string>();
                if (item.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sub in item.EnumerateArray())
                    {
                        if (sub.ValueKind == JsonValueKind.String)
                        {
                            tag.Add(sub.GetString() ?? "");
                        }
                        else
                        {
                            tag.Add(sub.ToString() ?? "");
                        }
                    }
                }
                result.Add(tag);
            }
            return result;
        }

        public static List<List<string>> ParseTags(string tagsJson)
        {
            if (string.IsNullOrWhiteSpace(tagsJson)) return [];
            try
            {
                using var doc = JsonDocument.Parse(tagsJson);
                return ParseTags(doc.RootElement);
            }
            catch
            {
                return [];
            }
        }

        public static string SerializeTags(List<List<string>> tags)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < tags.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[');
                var tag = tags[i];
                for (int j = 0; j < tag.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    sb.Append(EscapeJsonString(tag[j] ?? ""));
                }
                sb.Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static byte[] ComputeEventId(string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
        {
            var sb = new StringBuilder();
            sb.Append("[0,\"");
            sb.Append(pubkey.ToLowerInvariant());
            sb.Append("\",");
            sb.Append(createdAt);
            sb.Append(',');
            sb.Append(kind);
            sb.Append(',');
            sb.Append(SerializeTags(tags));
            sb.Append(',');
            sb.Append(EscapeJsonString(content ?? ""));
            sb.Append(']');

            var json = sb.ToString();
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        }

        public static byte[] ComputeEventId(string pubkey, long createdAt, int kind, string tagsJson, string content)
        {
            var tagsList = ParseTags(tagsJson);
            return ComputeEventId(pubkey, createdAt, kind, tagsList, content);
        }

        public static string SignUnsignedEvent(string unsignedJson, string pubkeyHex, byte[] privKey)
        {
            using var evDoc = JsonDocument.Parse(unsignedJson);
            var ev = evDoc.RootElement;

            var kind = ev.TryGetProperty("kind", out var kEl) && kEl.TryGetInt32(out var k) ? k : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) && ctEl.TryGetInt64(out var ct) ? ct : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var eventPubkey = ev.TryGetProperty("pubkey", out var pkEl) ? pkEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(eventPubkey) || eventPubkey.Length != 64)
            {
                eventPubkey = pubkeyHex;
            }
            eventPubkey = eventPubkey.ToLowerInvariant();

            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? "" : "";
            var tagsList = ev.TryGetProperty("tags", out var tEl) ? ParseTags(tEl) : [];

            var eventIdBytes = ComputeEventId(eventPubkey, createdAt, kind, tagsList, content);
            var eventIdHex = BytesToHex(eventIdBytes);

            var sigBytes = NostrCrypto.Sign(eventIdBytes, privKey);
            var sigHex = BytesToHex(sigBytes);

            var sb = new StringBuilder();
            sb.Append("{\"id\":\"").Append(eventIdHex).Append("\",");
            sb.Append("\"pubkey\":\"").Append(eventPubkey).Append("\",");
            sb.Append("\"created_at\":").Append(createdAt).Append(",");
            sb.Append("\"kind\":").Append(kind).Append(",");
            sb.Append("\"tags\":").Append(SerializeTags(tagsList)).Append(",");
            sb.Append("\"content\":").Append(EscapeJsonString(content)).Append(",");
            sb.Append("\"sig\":\"").Append(sigHex).Append("\"}");

            return sb.ToString();
        }

        public static string BuildSignedKind24133(string signerPubkeyHex, string clientPubkeyHex, string encryptedContent, byte[] privKey)
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tags = new List<List<string>> { new() { "p", clientPubkeyHex } };
            var eventId = ComputeEventId(signerPubkeyHex, createdAt, 24133, tags, encryptedContent);
            var sig = NostrCrypto.Sign(eventId, privKey);

            var sb = new StringBuilder();
            sb.Append("{\"id\":\"").Append(BytesToHex(eventId)).Append("\",");
            sb.Append("\"pubkey\":\"").Append(signerPubkeyHex.ToLowerInvariant()).Append("\",");
            sb.Append("\"created_at\":").Append(createdAt).Append(",");
            sb.Append("\"kind\":24133,");
            sb.Append("\"tags\":").Append(SerializeTags(tags)).Append(",");
            sb.Append("\"content\":").Append(EscapeJsonString(encryptedContent)).Append(",");
            sb.Append("\"sig\":\"").Append(BytesToHex(sig)).Append("\"}");

            return sb.ToString();
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
