using Android.Content;
using System.Text.Json;

namespace nokandro
{
    /// <summary>
    /// Persists bunker:// paired clients (names, permissions).
    /// Amber-style long-lived pairing.
    /// </summary>
    public sealed class BunkerClientStore
    {
        private const string PrefsName = "nokandro_prefs";
        private const string KeyClientsJson = "pref_bunker_clients_json";
        private const string KeyAuthorized = "pref_bunker_authorized";
        private const string KeyLegacyPending = "pref_bunker_pending";
        private const string KeyLegacyRequireApproval = "pref_bunker_require_approval";
        private const string KeyNostrConnectClients = "pref_nc_clients";

        private readonly ISharedPreferences _prefs;

        public BunkerClientStore(Context context)
        {
            _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
                ?? throw new InvalidOperationException("SharedPreferences unavailable");
            PurgeNostrConnectClients(context);
        }

        /// <summary>Pubkeys from persisted nostrconnect:// URIs (not bunker:// clients).</summary>
        public static HashSet<string> LoadNostrConnectClientPubkeys(Context context)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                var raw = prefs?.GetString(KeyNostrConnectClients, "") ?? "";
                foreach (var uri in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                        set.Add(Normalize(parsed.ClientPubkey));
                }
            }
            catch { }
            return set;
        }

        /// <summary>Authorized bunker:// clients only (excludes nostrconnect://).</summary>
        public IReadOnlyCollection<string> GetBunkerAuthorized(Context context)
        {
            var ncSet = LoadNostrConnectClientPubkeys(context);
            return GetAuthorizedSet()
                .Where(pk => !ncSet.Contains(pk))
                .ToList();
        }

        private void PurgeNostrConnectClients(Context context)
        {
            var ncSet = LoadNostrConnectClientPubkeys(context);
            if (ncSet.Count == 0) return;

            var set = ReadAuthorizedSetFromPrefs();
            var data = LoadClientsRoot();
            var changed = false;
            foreach (var pk in ncSet)
            {
                if (set.Remove(pk)) changed = true;
                if (data.Clients.Remove(pk)) changed = true;
            }
            if (changed)
            {
                SaveAuthorizedSet(set);
                SaveClientsRoot(data);
            }
        }

        public bool IsAuthorized(string pubkey) =>
            GetAuthorizedSet().Contains(Normalize(pubkey));

        public IReadOnlyCollection<string> GetAuthorized() => GetAuthorizedSet();

        public void Authorize(string pubkey, string? perms = null, string? name = null)
        {
            pubkey = Normalize(pubkey);
            var set = GetAuthorizedSet();
            set.Add(pubkey);
            SaveAuthorizedSet(set);

            UpdateClientRecord(pubkey, name, perms);
        }

        public void Revoke(string pubkey)
        {
            pubkey = Normalize(pubkey);
            var set = GetAuthorizedSet();
            if (!set.Remove(pubkey)) return;
            SaveAuthorizedSet(set);

            var data = LoadClientsRoot();
            data.Clients.Remove(pubkey);
            SaveClientsRoot(data);
        }

        public string? GetName(string pubkey)
        {
            var rec = GetRecord(Normalize(pubkey));
            return rec?.Name;
        }

        public void SetName(string pubkey, string? name)
        {
            pubkey = Normalize(pubkey);
            var data = LoadClientsRoot();
            if (!data.Clients.TryGetValue(pubkey, out var rec))
                rec = new ClientRecord();
            rec.Name = name;
            data.Clients[pubkey] = rec;
            SaveClientsRoot(data);
        }

        public string? GetPermsString(string pubkey) => GetRecord(Normalize(pubkey))?.Perms;

        public Nip46Permissions? GetPermissions(string pubkey)
        {
            var perms = GetPermsString(pubkey);
            return Nip46Permissions.Parse(perms);
        }

        public void SetPermissions(string pubkey, string? perms)
        {
            UpdateClientRecord(Normalize(pubkey), null, perms);
        }

        public void ClearAllPairings()
        {
            _prefs.Edit()?
                .Remove(KeyAuthorized)
                .Remove(KeyLegacyPending)
                .Remove(KeyLegacyRequireApproval)
                .Remove(KeyClientsJson)
                ?.Apply();
        }

        private void UpdateClientRecord(string pubkey, string? name, string? perms)
        {
            var data = LoadClientsRoot();
            if (!data.Clients.TryGetValue(pubkey, out var rec))
                rec = new ClientRecord();
            if (name != null) rec.Name = name;
            if (perms != null) rec.Perms = perms;
            data.Clients[pubkey] = rec;
            SaveClientsRoot(data);
        }

        private ClientRecord? GetRecord(string pubkey)
        {
            var data = LoadClientsRoot();
            return data.Clients.TryGetValue(pubkey, out var rec) ? rec : null;
        }

        private ClientsRoot LoadClientsRoot()
        {
            try
            {
                var json = _prefs.GetString(KeyClientsJson, null);
                if (string.IsNullOrEmpty(json)) return new ClientsRoot();
                return JsonSerializer.Deserialize<ClientsRoot>(json) ?? new ClientsRoot();
            }
            catch
            {
                return new ClientsRoot();
            }
        }

        private void SaveClientsRoot(ClientsRoot data)
        {
            _prefs.Edit()?.PutString(KeyClientsJson, JsonSerializer.Serialize(data))?.Apply();
        }

        private HashSet<string> ReadAuthorizedSetFromPrefs()
        {
            var raw = _prefs.GetString(KeyAuthorized, "") ?? "";
            return new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
        }

        private HashSet<string> GetAuthorizedSet()
        {
            var set = ReadAuthorizedSetFromPrefs();

            if (set.Count == 0)
            {
                var data = LoadClientsRoot();
                if (data.Clients.Count > 0)
                {
                    foreach (var pk in data.Clients.Keys)
                    {
                        if (!string.IsNullOrEmpty(pk))
                            set.Add(Normalize(pk));
                    }
                    if (set.Count > 0)
                        SaveAuthorizedSet(set);
                }
            }

            return set;
        }

        private void SaveAuthorizedSet(HashSet<string> set) =>
            _prefs.Edit()?.PutString(KeyAuthorized, string.Join(",", set))?.Apply();

        private static string Normalize(string pubkey) => pubkey.Trim().ToLowerInvariant();

        private sealed class ClientsRoot
        {
            public Dictionary<string, ClientRecord> Clients { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ClientRecord
        {
            public string? Name { get; set; }
            public string? Perms { get; set; }
        }
    }
}
