using System.Text;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Decrypts private zap events (NIP-55 decrypt_zap_event).
    /// Supports NIP-44 encrypted content and kind 9735 description payloads.
    /// </summary>
    internal static class NostrZapDecrypt
    {
        public static string Decrypt(string eventJson, byte[] privKey, string userPubkeyHex)
        {
            using var doc = JsonDocument.Parse(eventJson);
            var root = doc.RootElement;
            var changed = false;

            if (root.TryGetProperty("content", out var contentEl))
            {
                var content = contentEl.GetString() ?? "";
                if (TryDecryptField(content, root, privKey, userPubkeyHex, out var plain))
                {
                    root = ReplaceContent(root, plain);
                    changed = true;
                }
            }

            if (root.TryGetProperty("kind", out var kindEl) && kindEl.GetInt32() == 9735
                && root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                var newTags = DecryptDescriptionTags(tagsEl, privKey, userPubkeyHex, ref changed);
                if (changed)
                    root = ReplaceTags(root, newTags);
            }

            return changed ? root.GetRawText() : eventJson;
        }

        private static JsonElement ReplaceContent(JsonElement root, string plain)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "content")
                        writer.WriteString("content", plain);
                    else
                        prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var outDoc = JsonDocument.Parse(ms.ToArray());
            return outDoc.RootElement.Clone();
        }

        private static JsonElement ReplaceTags(JsonElement root, List<string[]> newTags)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "tags")
                    {
                        writer.WritePropertyName("tags");
                        writer.WriteStartArray();
                        foreach (var tag in newTags)
                        {
                            writer.WriteStartArray();
                            foreach (var v in tag) writer.WriteStringValue(v);
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
                    else
                        prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var outDoc = JsonDocument.Parse(ms.ToArray());
            return outDoc.RootElement.Clone();
        }

        private static List<string[]> DecryptDescriptionTags(JsonElement tagsEl, byte[] privKey, string userPubkeyHex, ref bool changed)
        {
            var tags = new List<string[]>();
            foreach (var tag in tagsEl.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Array || tag.GetArrayLength() < 2)
                {
                    tags.Add(TagToArray(tag));
                    continue;
                }
                var name = tag[0].GetString();
                if (name != "description")
                {
                    tags.Add(TagToArray(tag));
                    continue;
                }
                var desc = tag[1].GetString() ?? "";
                if (TryDecryptEmbeddedZapJson(desc, privKey, userPubkeyHex, out var decryptedDesc))
                {
                    tags.Add(["description", decryptedDesc]);
                    changed = true;
                }
                else
                    tags.Add(TagToArray(tag));
            }
            return tags;
        }

        private static bool TryDecryptEmbeddedZapJson(string desc, byte[] privKey, string userPubkeyHex, out string decryptedDesc)
        {
            decryptedDesc = desc;
            try
            {
                using var inner = JsonDocument.Parse(desc);
                var innerRoot = inner.RootElement;
                if (!innerRoot.TryGetProperty("content", out var cEl)) return false;
                var content = cEl.GetString() ?? "";
                if (!TryDecryptField(content, innerRoot, privKey, userPubkeyHex, out var plain)) return false;
                decryptedDesc = ReplaceContent(innerRoot, plain).GetRawText();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string[] TagToArray(JsonElement tag)
        {
            var list = new List<string>();
            foreach (var v in tag.EnumerateArray())
                list.Add(v.GetString() ?? "");
            return [.. list];
        }

        private static bool TryDecryptField(string content, JsonElement eventRoot, byte[] privKey, string userPubkeyHex, out string plaintext)
        {
            plaintext = content;
            if (string.IsNullOrEmpty(content)) return false;

            var counterparty = FindCounterpartyPubkey(eventRoot);
            if (string.IsNullOrEmpty(counterparty)) return false;

            var decrypted = content.Contains("?iv=")
                ? NostrCrypto.Decrypt(content, counterparty, privKey)
                : NostrCrypto.DecryptNip44(content, counterparty, privKey);

            if (string.IsNullOrEmpty(decrypted)) return false;
            plaintext = decrypted;
            return true;
        }

        private static string? FindCounterpartyPubkey(JsonElement eventRoot)
        {
            if (!eventRoot.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
                return null;

            string? p = null;
            string? bigP = null;
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.ValueKind != JsonValueKind.Array || tag.GetArrayLength() < 2) continue;
                var k = tag[0].GetString();
                var v = tag[1].GetString();
                if (string.IsNullOrEmpty(v) || v.Length != 64) continue;
                if (k == "p") p = v.ToLowerInvariant();
                if (k == "P") bigP = v.ToLowerInvariant();
            }
            return bigP ?? p;
        }
    }
}
