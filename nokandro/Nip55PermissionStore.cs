using Android.Content;
using System.Collections.Generic;
using System.Text.Json;

namespace nokandro
{
    internal static class Nip55PermissionStore
    {
        private const string PrefsName = "nip55_permissions";
        private const string PrefixGrant = "grant:";
        private const string PrefixReject = "reject:";

        public static bool IsAlwaysRejected(Context context, string? packageName, string requestType)
        {
            if (string.IsNullOrEmpty(packageName)) return false;
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            return prefs?.GetBoolean(PrefixReject + packageName + ":" + requestType, false) ?? false;
        }

        public static void SetAlwaysRejected(Context context, string? packageName, string requestType)
        {
            if (string.IsNullOrEmpty(packageName)) return;
            prefs(context)?.Edit()?.PutBoolean(PrefixReject + packageName + ":" + requestType, true)?.Apply();
        }

        public static bool HasPermission(Context context, string? packageName, string requestType, int? eventKind = null)
        {
            if (string.IsNullOrEmpty(packageName)) return false;
            var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;

            if (prefs.GetBoolean(PrefixGrant + packageName + ":" + requestType, false))
                return true;

            if (requestType == "sign_event" && eventKind.HasValue
                && prefs.GetBoolean(PrefixGrant + packageName + ":sign_event:" + eventKind.Value, false))
                return true;

            return false;
        }

        public static void Grant(Context context, string? packageName, string requestType, int? eventKind = null)
        {
            if (string.IsNullOrEmpty(packageName)) return;
            var edit = prefs(context)?.Edit();
            if (edit == null) return;
            edit.PutBoolean(PrefixGrant + packageName + ":" + requestType, true);
            if (requestType == "sign_event" && eventKind.HasValue)
                edit.PutBoolean(PrefixGrant + packageName + ":sign_event:" + eventKind.Value, true);
            edit.Apply();
        }

        public static void GrantFromPermissionsJson(Context context, string? packageName, string? permissionsJson)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(permissionsJson)) return;
            try
            {
                using var doc = JsonDocument.Parse(permissionsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(type)) continue;
                    int? kind = item.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number
                        ? k.GetInt32()
                        : null;
                    Grant(context, packageName, type, kind);
                }
            }
            catch { }
        }

        private static ISharedPreferences? prefs(Context context) =>
            context.GetSharedPreferences(PrefsName, FileCreationMode.Private);

        public static List<PermissionEntry> GetAllPermissions(Context context)
        {
            var list = new List<PermissionEntry>();
            var p = prefs(context);
            if (p == null) return list;
            var all = p.All;
            if (all == null) return list;

            foreach (var kvp in all)
            {
                var key = kvp.Key;
                if (kvp.Value is bool val && val)
                {
                    if (key.StartsWith(PrefixGrant))
                    {
                        var rem = key[PrefixGrant.Length..];
                        ParseKey(key, rem, true, list);
                    }
                    else if (key.StartsWith(PrefixReject))
                    {
                        var rem = key[PrefixReject.Length..];
                        ParseKey(key, rem, false, list);
                    }
                }
            }
            return list;
        }

        private static void ParseKey(string rawKey, string rem, bool isGranted, List<PermissionEntry> list)
        {
            var firstColon = rem.IndexOf(':');
            if (firstColon < 0) return;

            var pkg = rem[..firstColon];
            var actionPart = rem[(firstColon + 1)..];

            var entry = new PermissionEntry
            {
                RawKey = rawKey,
                PackageName = pkg,
                IsGranted = isGranted
            };

            var secondColon = actionPart.IndexOf(':');
            if (secondColon >= 0)
            {
                var type = actionPart[..secondColon];
                var kindStr = actionPart[(secondColon + 1)..];
                entry.RequestType = type;
                if (int.TryParse(kindStr, out var kind))
                {
                    entry.EventKind = kind;
                }
            }
            else
            {
                entry.RequestType = actionPart;
            }

            list.Add(entry);
        }

        public static void DeletePermission(Context context, string rawKey)
        {
            prefs(context)?.Edit()?.Remove(rawKey)?.Apply();
        }
    }

    internal class PermissionEntry
    {
        public string RawKey { get; set; } = "";
        public string PackageName { get; set; } = "";
        public string RequestType { get; set; } = "";
        public int? EventKind { get; set; }
        public bool IsGranted { get; set; }
    }
}
