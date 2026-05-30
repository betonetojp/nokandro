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

        private readonly ISharedPreferences _prefs;

        public BunkerClientStore(Context context)
        {
            _prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private)
                ?? throw new InvalidOperationException("SharedPreferences unavailable");
            MigrateLegacyAuthorized();
        }

        private void MigrateLegacyAuthorized()
        {
            const string legacyKey = "pref_bunker_authorized";
            var raw = _prefs.GetString(legacyKey, null);
            if (string.IsNullOrEmpty(raw)) return;
            foreach (var pk in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrEmpty(pk))
                    Authorize(pk);
            }
            _prefs.Edit()?.Remove(legacyKey)?.Apply();
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

        private HashSet<string> GetAuthorizedSet()
        {
            var raw = _prefs.GetString(KeyAuthorized, "") ?? "";
            return new HashSet<string>(
                raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
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
