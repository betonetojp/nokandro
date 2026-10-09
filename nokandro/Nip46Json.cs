using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace nokandro
{
    internal static class Nip46Json
    {
        private static readonly JsonWriterOptions CompactJsonOptions = new()
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string EscapeJsonString(string? text)
        {
            if (text == null) return "null";
            return JsonSerializer.Serialize(text, new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
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

        public static byte[] ComputeEventId(string pubkey, long createdAt, int kind, List<List<string>> tags, string content)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(0);
                writer.WriteStringValue(pubkey.ToLowerInvariant());
                writer.WriteNumberValue(createdAt);
                writer.WriteNumberValue(kind);
                writer.WriteStartArray();
                foreach (var tag in tags)
                {
                    writer.WriteStartArray();
                    foreach (var elem in tag)
                    {
                        writer.WriteStringValue(elem ?? "");
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteStringValue(content ?? "");
                writer.WriteEndArray();
            }

            using var sha = SHA256.Create();
            return sha.ComputeHash(ms.ToArray());
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

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("id", eventIdHex);
                writer.WriteString("pubkey", eventPubkey);
                writer.WriteNumber("created_at", createdAt);
                writer.WriteNumber("kind", kind);
                writer.WriteStartArray("tags");
                foreach (var tag in tagsList)
                {
                    writer.WriteStartArray();
                    foreach (var elem in tag)
                    {
                        writer.WriteStringValue(elem);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteString("content", content);
                writer.WriteString("sig", sigHex);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public static string BuildSignedKind24133(string signerPubkeyHex, string clientPubkeyHex, string encryptedContent, byte[] privKey)
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var tags = new List<List<string>> { new() { "p", clientPubkeyHex } };
            var eventId = ComputeEventId(signerPubkeyHex, createdAt, 24133, tags, encryptedContent);
            var sig = NostrCrypto.Sign(eventId, privKey);

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("id", BytesToHex(eventId));
                writer.WriteString("pubkey", signerPubkeyHex.ToLowerInvariant());
                writer.WriteNumber("created_at", createdAt);
                writer.WriteNumber("kind", 24133);
                writer.WriteStartArray("tags");
                writer.WriteStartArray();
                writer.WriteStringValue("p");
                writer.WriteStringValue(clientPubkeyHex);
                writer.WriteEndArray();
                writer.WriteEndArray();
                writer.WriteString("content", encryptedContent);
                writer.WriteString("sig", BytesToHex(sig));
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
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
