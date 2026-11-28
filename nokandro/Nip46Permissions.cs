using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Parses NIP-46 permission strings (method[:params], comma-separated).
    /// </summary>
    public sealed class Nip46Permissions
    {
        private readonly HashSet<string> _methods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<int>> _signEventKinds = new(StringComparer.Ordinal);

        public static Nip46Permissions? Parse(string? perms)
        {
            if (string.IsNullOrWhiteSpace(perms)) return null;
            var result = new Nip46Permissions();
            foreach (var part in perms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colon = part.IndexOf(':');
                var method = colon >= 0 ? part[..colon] : part;
                if (string.IsNullOrEmpty(method)) continue;
                result._methods.Add(method);
                if (method == "sign_event" && colon >= 0 && int.TryParse(part[(colon + 1)..], out var kind))
                {
                    if (!result._signEventKinds.TryGetValue("sign_event", out var kinds))
                    {
                        kinds = [];
                        result._signEventKinds["sign_event"] = kinds;
                    }
                    kinds.Add(kind);
                }
            }
            return result._methods.Count > 0 ? result : null;
        }

        public static Nip46Permissions? FromConnectParams(JsonElement paramsArr)
        {
            var permsStr = ExtractPermsString(paramsArr);
            return permsStr != null ? Parse(permsStr) : null;
        }

        public static string? ExtractPermsString(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array) return null;

            // NIP-46: connect(remote_signer_pubkey, optional_secret, optional_requested_permissions, optional_client_metadata)
            if (paramsArr.GetArrayLength() >= 3)
            {
                var p2 = paramsArr[2];
                if (p2.ValueKind == JsonValueKind.String)
                {
                    var s = p2.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(s) && !IsJsonObjectString(s))
                        return s;
                }
                return null;
            }

            // Fallback for legacy 2-parameter connect requests that omit secret
            for (int i = 1; i < paramsArr.GetArrayLength(); i++)
            {
                var el = paramsArr[i];
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString()?.Trim();
                if (string.IsNullOrEmpty(s) || IsJsonObjectString(s)) continue;
                if (LooksLikePerms(s))
                    return s;
            }
            return null;
        }

        internal static bool IsJsonObjectString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.TrimStart().StartsWith('{');
        }

        private static bool LooksLikePerms(string s) =>
            s.Contains(',')
            || s.Contains("sign_event", StringComparison.Ordinal)
            || s.Contains("nip44", StringComparison.Ordinal)
            || s.Contains("nip04", StringComparison.Ordinal)
            || s is "get_public_key" or "get_relays" or "switch_relays" or "ping" or "describe";

        public void EnsureAllowed(string method, JsonElement paramsArr)
        {
            if (_methods.Count == 0) return;
            if (!_methods.Contains(method))
                throw new UnauthorizedAccessException($"permission denied: {method}");

            if (method == "sign_event" && _signEventKinds.TryGetValue("sign_event", out var kinds) && kinds.Count > 0)
            {
                if (paramsArr.ValueKind != JsonValueKind.Array || paramsArr.GetArrayLength() < 1)
                    throw new ArgumentException("Missing event parameter");
                var eventParam = paramsArr[0];
                var unsignedJson = eventParam.ValueKind == JsonValueKind.String
                    ? eventParam.GetString() ?? ""
                    : eventParam.GetRawText();
                using var evDoc = JsonDocument.Parse(unsignedJson);
                var kind = evDoc.RootElement.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
                if (!kinds.Contains(kind))
                    throw new UnauthorizedAccessException($"permission denied: sign_event:{kind}");
            }
        }
    }

    /// <summary>
    /// Parsed NIP-46 connect optional_client_metadata (params[3]: { name, url, image }).
    /// </summary>
    public sealed class Nip46ClientMetadata
    {
        public string? Name { get; }
        public string? Url { get; }
        public string? Image { get; }

        public Nip46ClientMetadata(string? name, string? url, string? image)
        {
            Name = name;
            Url = url;
            Image = image;
        }

        public static Nip46ClientMetadata? FromConnectParams(JsonElement paramsArr)
        {
            if (paramsArr.ValueKind != JsonValueKind.Array) return null;

            var len = paramsArr.GetArrayLength();
            if (len >= 4)
            {
                var fromSlot3 = TryParseElement(paramsArr[3]);
                if (fromSlot3 != null) return fromSlot3;
            }

            // Fallback: check index >= 2 for JSON object metadata if placed earlier
            for (int i = 2; i < len; i++)
            {
                if (i == 3) continue;
                var parsed = TryParseElement(paramsArr[i]);
                if (parsed != null) return parsed;
            }

            return null;
        }

        private static Nip46ClientMetadata? TryParseElement(JsonElement el)
        {
            try
            {
                if (el.ValueKind == JsonValueKind.Object)
                    return ParseObject(el);

                if (el.ValueKind == JsonValueKind.String)
                {
                    var raw = el.GetString();
                    if (!Nip46Permissions.IsJsonObjectString(raw)) return null;
                    using var doc = JsonDocument.Parse(raw!);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        return ParseObject(doc.RootElement);
                }
            }
            catch { }
            return null;
        }

        private static Nip46ClientMetadata? ParseObject(JsonElement obj)
        {
            string? name = TryGetNonEmptyString(obj, "name");
            string? url = TryGetHttpUrl(obj, "url");
            string? image = TryGetHttpUrl(obj, "image")
                ?? TryGetHttpUrl(obj, "icon")
                ?? TryGetHttpUrl(obj, "picture")
                ?? TryGetHttpUrl(obj, "logo")
                ?? TryGetFirstIconUrl(obj);

            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(url) && string.IsNullOrEmpty(image))
                return null;

            return new Nip46ClientMetadata(name, url, image);
        }

        private static string? TryGetNonEmptyString(JsonElement obj, string propName)
        {
            if (obj.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var s = prop.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return null;
        }

        private static string? TryGetHttpUrl(JsonElement obj, string propName)
        {
            var s = TryGetNonEmptyString(obj, propName);
            if (string.IsNullOrEmpty(s)) return null;
            return IsHttpUrl(s) ? s : null;
        }

        private static string? TryGetFirstIconUrl(JsonElement obj)
        {
            if (!obj.TryGetProperty("icons", out var iconsProp)) return null;
            if (iconsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in iconsProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s) && IsHttpUrl(s)) return s;
                    }
                }
            }
            else if (iconsProp.ValueKind == JsonValueKind.String)
            {
                var s = iconsProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && IsHttpUrl(s)) return s;
            }
            return null;
        }

        private static bool IsHttpUrl(string url) =>
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
