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
            foreach (var el in paramsArr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) continue;
                var s = el.GetString();
                if (string.IsNullOrEmpty(s)) continue;
                if (s.Contains(',') || s.Contains("sign_event") || s.Contains("nip44") || s.Contains("nip04"))
                    return s;
            }
            return null;
        }

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
}
