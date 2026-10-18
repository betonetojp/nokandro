using Android.Content;
using Android.OS;

namespace nokandro
{
    [Service]
    public class BunkerService : Service
    {
        public static bool IsRunning { get; private set; }
        /// <summary>True when the bunker:// (NIP-46 signer) part is active, independent of nostrconnect sessions.</summary>
        public static bool IsBunkerActive { get; private set; }
        /// <summary>Last known bunker:// URI so the Activity can display it after recreation.</summary>
        public static string? LastBunkerUri { get; private set; }
        public static int NostrConnectSessionCount
        {
            get { lock (_ncLock) { return _ncSessions.Count; } }
        }

        private const string TAG = "BunkerService";
        private const int NOTIF_ID = 1002;
        private NostrBunker? _bunker;
        public static NostrBunker? CurrentBunkerInstance { get; private set; }

        // nostrconnect:// sessions (shared across service lifetime)
        private static readonly Lock _ncLock = new();
        private static readonly Dictionary<string, NostrConnectSession> _ncSessions = [];

        // In-memory log buffers
        private static readonly List<string> _bunkerLogs = [];
        private static readonly List<string> _ncLogs = [];
        private static readonly object _logLock = new();
        private const int MaxLogCount = 100;

        public static void AddBunkerLog(string msg)
        {
            lock (_logLock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                _bunkerLogs.Add($"[{timestamp}] {msg}");
                if (_bunkerLogs.Count > MaxLogCount)
                    _bunkerLogs.RemoveAt(0);
            }
        }

        public static void AddNcLog(string? clientPubkey, string msg)
        {
            lock (_logLock)
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                var label = !string.IsNullOrEmpty(clientPubkey) && clientPubkey.Length >= 12
                    ? clientPubkey[..12] + "..."
                    : clientPubkey;
                var entry = string.IsNullOrEmpty(label)
                    ? $"[{timestamp}] {msg}"
                    : $"[{timestamp}] [{label}] {msg}";
                _ncLogs.Add(entry);
                if (_ncLogs.Count > MaxLogCount)
                    _ncLogs.RemoveAt(0);
            }
        }

        public static IReadOnlyList<string> GetBunkerLogs()
        {
            lock (_logLock) { return _bunkerLogs.ToList(); }
        }

        public static IReadOnlyList<string> GetNcLogs()
        {
            lock (_logLock) { return _ncLogs.ToList(); }
        }

        public static void ClearBunkerLogs()
        {
            lock (_logLock) { _bunkerLogs.Clear(); }
        }

        public static void ClearNcLogs()
        {
            lock (_logLock) { _ncLogs.Clear(); }
        }

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            AppLog.D(TAG, "OnStartCommand: action=" + (intent?.Action ?? "(null)"));

            if (intent?.Action == "STOP")
            {
                return HandleStopBunker();
            }

            if (intent?.Action == "nokandro.ACTION_BUNKER_REVOKE_CLIENT")
            {
                var clientPubkey = intent.GetStringExtra("clientPubkey");
                if (!string.IsNullOrEmpty(clientPubkey))
                {
                    try
                    {
                        HandleRevokeBunkerClient(clientPubkey);
                    }
                    catch { }
                }
                return StartCommandResult.Sticky;
            }

            if (intent?.Action == "START_NOSTRCONNECT")
            {
                return HandleStartNostrConnect(intent);
            }

            if (intent?.Action == "STOP_NOSTRCONNECT")
            {
                var clientPubkey = intent.GetStringExtra("clientPubkey");
                if (!string.IsNullOrEmpty(clientPubkey))
                    RemoveNostrConnectSession(clientPubkey);
                return StartCommandResult.Sticky;
            }

            var nsecHex = intent?.GetStringExtra("nsecHex");
            var relay = intent?.GetStringExtra("relay");
            var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);

            // Sticky restart often delivers a null Intent (no extras). Reload from prefs when enabled.
            if (string.IsNullOrEmpty(nsecHex) || nsecHex!.Length != 64)
            {
                if (TryRestoreBunkerCredentials(prefs, out nsecHex, out var restoredRelay, out _))
                {
                    AppLog.D(TAG, "Restored bunker credentials from prefs (sticky / null Intent)");
                    if (string.IsNullOrEmpty(relay))
                        relay = restoredRelay;
                }
                else
                {
                    AppLog.W(TAG, "Invalid nsecHex and prefs restore failed — stopping");
                    // Must call StartForeground before StopSelf on Android 12+ if started via StartForegroundService
                    try { StartForegroundNotification(); } catch { }
                    StopSelf();
                    return StartCommandResult.NotSticky;
                }
            }

            // Convert hex to bytes
            var privKey = new byte[32];
            for (int i = 0; i < 32; i++)
                privKey[i] = Convert.ToByte(nsecHex!.Substring(i * 2, 2), 16);

            if (string.IsNullOrEmpty(relay))
                relay = "wss://ephemeral.snowflare.cc/";

            // Build foreground notification
            StartForegroundNotification();

            // Stop existing bunker if running (prevents duplicate instances with different secrets)
            try { _bunker?.Stop(); } catch { }
            _bunker = null;

            var store = new BunkerClientStore(this);
            var secret = intent?.GetStringExtra("secret") ?? prefs?.GetString("pref_bunker_secret", null);

            var bunkerOptions = CreateBunkerOptions(store);
            var savedClients = store.GetAuthorized().Concat(BunkerClientStore.LoadNostrConnectClientPubkeys(this)).Distinct().ToList();
            _bunker = new NostrBunker(privKey, relay, secret, savedClients, bunkerOptions);
            CurrentBunkerInstance = _bunker;
            _bunker.OnLog += msg =>
            {
                AddBunkerLog(msg);
                try
                {
                    var b = new Intent("nokandro.ACTION_BUNKER_LOG");
                    b.PutExtra("message", msg);
                    LocalBroadcast.SendBroadcast(this, b);
                }
                catch { }
            };
            _bunker.OnClientAuthorized += pubkey =>
            {
                AddBunkerLog($"Client authorized: {pubkey[..Math.Min(12, pubkey.Length)]}...");
                try
                {
                    var b = new Intent("nokandro.ACTION_BUNKER_CLIENT_AUTHORIZED");
                    b.PutExtra("clientPubkey", pubkey);
                    b.PutExtra("isNew", true);
                    LocalBroadcast.SendBroadcast(this, b);
                }
                catch { }
            };
            _bunker.OnClientReconnected += pubkey =>
            {
                AddBunkerLog($"Client reconnected: {pubkey[..Math.Min(12, pubkey.Length)]}...");
                try
                {
                    var b = new Intent("nokandro.ACTION_BUNKER_CLIENT_RECONNECTED");
                    b.PutExtra("clientPubkey", pubkey);
                    LocalBroadcast.SendBroadcast(this, b);
                }
                catch { }
            };
            _bunker.OnActivity += (pubkey, methodSummary) =>
            {
                try
                {
                    store.RecordActivity(pubkey, methodSummary);
                    var b = new Intent("nokandro.ACTION_BUNKER_CLIENT_ACTIVITY");
                    b.PutExtra("clientPubkey", pubkey);
                    b.PutExtra("method", methodSummary);
                    LocalBroadcast.SendBroadcast(this, b);
                }
                catch { }
            };
            IsRunning = true;
            IsBunkerActive = true;
            LastBunkerUri = _bunker.BunkerUri;

            // Broadcast bunker URI and started state
            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_STARTED");
                b.PutExtra("bunkerUri", _bunker.BunkerUri);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            _bunker.Start();
            RestorePersistedNostrConnectSessions(prefs, privKey);
            UpdateNotification();
            AppLog.D(TAG, "Bunker started");

            return StartCommandResult.Sticky;
        }

        private void RestorePersistedNostrConnectSessions(ISharedPreferences? prefs, byte[] privKey)
        {
            try
            {
                var existing = prefs?.GetString("pref_nc_clients", "") ?? "";
                if (string.IsNullOrEmpty(existing)) return;

                var list = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var store = new BunkerClientStore(this);
                var addedAny = false;

                lock (_ncLock)
                {
                    foreach (var uriStr in list)
                    {
                        if (!NostrConnectUri.TryParse(uriStr, out var connectUri) || connectUri == null)
                            continue;
                        if (_ncSessions.ContainsKey(connectUri.ClientPubkey))
                            continue;

                        var storedPerms = Nip46Permissions.Parse(connectUri.Perms);
                        var switchRelaysRaw = prefs?.GetString(SettingsRepository.PREF_NOSTRCONNECT_SWITCH_RELAYS, SettingsRepository.DEFAULT_NOSTRCONNECT_SWITCH_RELAYS) ?? SettingsRepository.DEFAULT_NOSTRCONNECT_SWITCH_RELAYS;
                        var switchRelays = switchRelaysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        var session = new NostrConnectSession(privKey, connectUri, preAuthenticated: true, storedPerms, switchRelays);
                        session.OnLog += msg =>
                        {
                            AddNcLog(connectUri.ClientPubkey, msg);
                            try
                            {
                                var b = new Intent("nokandro.ACTION_NC_LOG");
                                b.PutExtra("clientPubkey", connectUri.ClientPubkey);
                                b.PutExtra("message", msg);
                                LocalBroadcast.SendBroadcast(this, b);
                            }
                            catch { }
                        };
                        session.OnActivity += (pubkey, methodSummary) =>
                        {
                            try
                            {
                                store.RecordActivity(pubkey, methodSummary);
                                var b = new Intent("nokandro.ACTION_NC_CLIENT_ACTIVITY");
                                b.PutExtra("clientPubkey", pubkey);
                                b.PutExtra("method", methodSummary);
                                LocalBroadcast.SendBroadcast(this, b);
                            }
                            catch { }
                        };

                        _ncSessions[connectUri.ClientPubkey] = session;
                        session.Start();
                        AddNcLog(connectUri.ClientPubkey, "Session restored and started");
                        addedAny = true;
                        AppLog.D(TAG, $"Restored NostrConnect session for {connectUri.ClientPubkey[..12]}...");
                    }
                }

                if (addedAny)
                {
                    BroadcastNostrConnectList();
                }
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "Failed to restore NostrConnect sessions: " + ex.Message);
            }
        }

        private StartCommandResult HandleStartNostrConnect(Intent intent)
        {
            var connectUriStr = intent.GetStringExtra("connectUri");
            var nsecHex = intent.GetStringExtra("nsecHex");

            if (string.IsNullOrEmpty(connectUriStr) || string.IsNullOrEmpty(nsecHex) || nsecHex!.Length != 64)
            {
                AppLog.W(TAG, "Invalid START_NOSTRCONNECT params");
                try { StartForegroundNotification(); } catch { }
                return StartCommandResult.NotSticky;
            }

            if (!NostrConnectUri.TryParse(connectUriStr, out var connectUri) || connectUri == null)
            {
                AppLog.W(TAG, "Invalid nostrconnect:// URI");
                try { StartForegroundNotification(); } catch { }
                return StartCommandResult.NotSticky;
            }

            var privKey = new byte[32];
            for (int i = 0; i < 32; i++)
                privKey[i] = Convert.ToByte(nsecHex.Substring(i * 2, 2), 16);

            StartForegroundNotification();

            lock (_ncLock)
            {
                // Stop existing session for same client if any
                if (_ncSessions.TryGetValue(connectUri.ClientPubkey, out var existing))
                {
                    try { existing.Stop(); } catch { }
                    _ncSessions.Remove(connectUri.ClientPubkey);
                }

                var store = new BunkerClientStore(this);
                var preAuth = intent.GetBooleanExtra("preAuthenticated", false);
                var storedPerms = Nip46Permissions.Parse(connectUri.Perms);
                var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                var switchRelaysRaw = prefs?.GetString(SettingsRepository.PREF_NOSTRCONNECT_SWITCH_RELAYS, SettingsRepository.DEFAULT_NOSTRCONNECT_SWITCH_RELAYS) ?? SettingsRepository.DEFAULT_NOSTRCONNECT_SWITCH_RELAYS;
                var switchRelays = switchRelaysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var session = new NostrConnectSession(privKey, connectUri, preAuth, storedPerms, switchRelays);
                session.OnClientPaired += (pk, permsStr) =>
                {
                    try
                    {
                        var p = Nip46Permissions.Parse(permsStr);
                        _bunker?.AddAuthorizedClient(pk, p);
                    }
                    catch { }
                };
                session.OnLog += msg =>
                {
                    AddNcLog(connectUri.ClientPubkey, msg);
                    try
                    {
                        var b = new Intent("nokandro.ACTION_NC_LOG");
                        b.PutExtra("clientPubkey", connectUri.ClientPubkey);
                        b.PutExtra("message", msg);
                        LocalBroadcast.SendBroadcast(this, b);
                    }
                    catch { }
                };
                session.OnActivity += (pubkey, methodSummary) =>
                {
                    try
                    {
                        store.RecordActivity(pubkey, methodSummary);
                        var b = new Intent("nokandro.ACTION_NC_CLIENT_ACTIVITY");
                        b.PutExtra("clientPubkey", pubkey);
                        b.PutExtra("method", methodSummary);
                        LocalBroadcast.SendBroadcast(this, b);
                    }
                    catch { }
                };

                _ncSessions[connectUri.ClientPubkey] = session;
                _bunker?.AddAuthorizedClient(connectUri.ClientPubkey, storedPerms);
                session.Start();
                AddNcLog(connectUri.ClientPubkey, "Session started");
            }

            IsRunning = true;

            // Broadcast updated session list
            BroadcastNostrConnectList();
            UpdateNotification();
            AppLog.D(TAG, $"NostrConnect session started for {connectUri.ClientPubkey[..12]}...");

            return StartCommandResult.Sticky;
        }

        private StartCommandResult HandleStopBunker()
        {
            // Stop only the bunker:// part, keep nostrconnect sessions alive
            AddBunkerLog("Bunker stopped");
            try { _bunker?.Stop(); } catch { }
            _bunker = null;
            CurrentBunkerInstance = null;
            IsBunkerActive = false;
            LastBunkerUri = null;

            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_STOPPED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            // If no nostrconnect sessions remain, stop the entire service
            bool hasNcSessions;
            lock (_ncLock) { hasNcSessions = _ncSessions.Count > 0; }

            if (!hasNcSessions)
            {
                IsRunning = false;
                StopSelf();
            }
            else
            {
                UpdateNotification();
            }

            return StartCommandResult.NotSticky;
        }

        private void RemoveNostrConnectSession(string clientPubkey)
        {
            AddNcLog(clientPubkey, "Session stopped");
            lock (_ncLock)
            {
                if (_ncSessions.TryGetValue(clientPubkey, out var session))
                {
                    try { session.Stop(); } catch { }
                    _ncSessions.Remove(clientPubkey);
                    AppLog.D(TAG, $"NostrConnect session removed: {clientPubkey[..Math.Min(12, clientPubkey.Length)]}...");
                }
            }

            BroadcastNostrConnectList();
            UpdateNotification();

            // If nothing is running, stop service
            if (_bunker == null || !_bunker.IsRunning)
            {
                lock (_ncLock)
                {
                    if (_ncSessions.Count == 0)
                    {
                        IsRunning = false;
                        StopSelf();
                    }
                }
            }
        }

        private void BroadcastNostrConnectList()
        {
            try
            {
                var b = new Intent("nokandro.ACTION_NC_LIST");
                string[] pubkeys;
                string[] uris;
                lock (_ncLock)
                {
                    pubkeys = [.. _ncSessions.Keys];
                    uris = new string[pubkeys.Length];
                    for (int i = 0; i < pubkeys.Length; i++)
                        uris[i] = _ncSessions[pubkeys[i]].RawUri;
                }
                b.PutExtra("pubkeys", pubkeys);
                b.PutExtra("uris", uris);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }
        }

        private void StartForegroundNotification()
        {
            PendingIntentFlags piFlags = PendingIntentFlags.UpdateCurrent;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                piFlags |= PendingIntentFlags.Immutable;

#pragma warning disable CA1416, CA1422
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var chanId = "bunker_channel";
                var chan = new NotificationChannel(chanId, "Nostr Bunker", NotificationImportance.Low);
                if (GetSystemService(NotificationService) is NotificationManager nm)
                    nm.CreateNotificationChannel(chan);

                var mainIntent = new Intent(this, typeof(MainActivity));
                mainIntent.PutExtra("openTab", "bunker");
                var mainPending = PendingIntent.GetActivity(this, 10, mainIntent, piFlags);

                var stopIntent = new Intent(this, typeof(BunkerService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 1, stopIntent, piFlags);

                var notif = new Notification.Builder(this, chanId)
                    .SetContentTitle("Nostr Bunker")
                    .SetContentText(BuildNotificationStatusText())
                    .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                    .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }

                StartForeground(NOTIF_ID, notif.Build());
            }
            else
            {
                var mainIntent = new Intent(this, typeof(MainActivity));
                mainIntent.PutExtra("openTab", "bunker");
                var mainPending = PendingIntent.GetActivity(this, 10, mainIntent, piFlags);

                var stopIntent = new Intent(this, typeof(BunkerService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 1, stopIntent, piFlags);

                var notif = new Notification.Builder(this)
                    .SetContentTitle("Nostr Bunker")
                    .SetContentText(BuildNotificationStatusText())
                    .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                    .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }

                StartForeground(NOTIF_ID, notif.Build());
            }
#pragma warning restore CA1416, CA1422
        }

        private string BuildNotificationStatusText()
        {
            var parts = new List<string>();
            if (_bunker != null) parts.Add("Bunker: active");
            int ncCount;
            lock (_ncLock) { ncCount = _ncSessions.Count; }
            if (ncCount > 0) parts.Add($"nostrconnect: {ncCount} session(s)");
            if (parts.Count == 0) return "NIP-46 remote signer";
            return string.Join(" | ", parts);
        }

        private void UpdateNotification()
        {
            try
            {
                if (!IsRunning) return;
                var text = BuildNotificationStatusText();
                PendingIntentFlags piFlags = PendingIntentFlags.UpdateCurrent;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    piFlags |= PendingIntentFlags.Immutable;

                var mainIntent = new Intent(this, typeof(MainActivity));
                mainIntent.PutExtra("openTab", "bunker");
                var mainPending = PendingIntent.GetActivity(this, 10, mainIntent, piFlags);

                var stopIntent = new Intent(this, typeof(BunkerService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 1, stopIntent, piFlags);

#pragma warning disable CA1416, CA1422
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var chanId = "bunker_channel";
                    var notif = new Notification.Builder(this, chanId)
                        .SetContentTitle("Nostr Bunker")
                        .SetContentText(text)
                        .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                        .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                        .SetContentIntent(mainPending)
                        .SetOngoing(true);
                    try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }
                    if (GetSystemService(NotificationService) is NotificationManager nm)
                        nm.Notify(NOTIF_ID, notif.Build());
                }
                else
                {
                    var notif = new Notification.Builder(this)
                        .SetContentTitle("Nostr Bunker")
                        .SetContentText(text)
                        .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                        .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                        .SetContentIntent(mainPending)
                        .SetOngoing(true);
                    try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }
                    if (GetSystemService(NotificationService) is NotificationManager nm)
                        nm.Notify(NOTIF_ID, notif.Build());
                }
#pragma warning restore CA1416, CA1422
            }
            catch { }
        }

        public override void OnDestroy()
        {
            AppLog.D(TAG, "OnDestroy");
            AddBunkerLog("Service destroyed");
            AddNcLog(null, "Service destroyed");
            IsRunning = false;
            IsBunkerActive = false;
            LastBunkerUri = null;
            try { _bunker?.Stop(); } catch { }
            _bunker = null;

            // Stop all nostrconnect sessions
            lock (_ncLock)
            {
                foreach (var session in _ncSessions.Values)
                {
                    try { session.Stop(); } catch { }
                }
                _ncSessions.Clear();
            }

            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_STOPPED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            try
            {
                var b = new Intent("nokandro.ACTION_NC_LIST");
                b.PutExtra("pubkeys", Array.Empty<string>());
                b.PutExtra("uris", Array.Empty<string>());
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            base.OnDestroy();
        }

        /// <summary>Returns the list of active nostrconnect client pubkeys.</summary>
        public static string[] GetActiveClientPubkeys()
        {
            lock (_ncLock) { return [.. _ncSessions.Keys]; }
        }

        /// <summary>Authorized bunker client pubkeys (in-memory when running, else from store).</summary>
        public static IReadOnlyCollection<string> GetAuthorizedBunkerClientPubkeys(Context context)
        {
            var ncSet = BunkerClientStore.LoadNostrConnectClientPubkeys(context);
            if (CurrentBunkerInstance != null)
            {
                return CurrentBunkerInstance.GetAuthorizedClientPubkeys()
                    .Where(pk => !ncSet.Contains(pk))
                    .ToList();
            }
            return new BunkerClientStore(context).GetBunkerAuthorized(context);
        }

        private BunkerOptions CreateBunkerOptions(BunkerClientStore store) => new()
        {
            IsAuthorized = store.IsAuthorized,
            GetPermissions = store.GetPermissions,
            SavePermissions = store.SetPermissions,
            OnClientPaired = (pk, perms, isNew) =>
            {
                if (isNew)
                {
                    store.Authorize(pk, perms);
                    PersistRotatedBunkerSecret();
                    AppLog.D(TAG, $"New client paired; bunker secret rotated: {pk[..Math.Min(12, pk.Length)]}...");
                }
                else if (!string.IsNullOrEmpty(perms))
                    store.SetPermissions(pk, perms);
                BroadcastBunkerClientsChanged();
            }
        };

        private void HandleRevokeBunkerClient(string clientPubkey)
        {
            AppLog.D(TAG, $"Revoking bunker client: {clientPubkey[..Math.Min(12, clientPubkey.Length)]}...");
            new BunkerClientStore(this).Revoke(clientPubkey);
            _bunker?.RemoveAuthorizedClient(clientPubkey);
            PersistRotatedBunkerSecret();
            BroadcastBunkerClientsChanged();
        }

        /// <summary>Rotate global bunker secret, persist to prefs, and broadcast URI change when bunker is running.</summary>
        private void PersistRotatedBunkerSecret()
        {
            string newSecret;
            if (_bunker != null)
            {
                newSecret = _bunker.RotateGlobalSecret();
                LastBunkerUri = _bunker.BunkerUri;
                BroadcastBunkerUriChanged(_bunker.BunkerUri);
            }
            else
            {
                newSecret = NostrBunker.GenerateBunkerSecret();
                LastBunkerUri = null;
            }

            var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
            prefs?.Edit()?.PutString("pref_bunker_secret", newSecret)?.Apply();
        }

        private void BroadcastBunkerUriChanged(string bunkerUri)
        {
            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_URI_CHANGED");
                b.PutExtra("bunkerUri", bunkerUri);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }
        }

        private void BroadcastBunkerClientsChanged()
        {
            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_CLIENTS_CHANGED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }
        }

        /// <summary>
        /// Reload nsec/relay/secret when Sticky restart delivers a null Intent (no extras).
        /// Only succeeds when the user still wants bunker enabled and a valid nsec is stored.
        /// </summary>
        private bool TryRestoreBunkerCredentials(
            ISharedPreferences? prefs,
            out string? nsecHex,
            out string? relay,
            out string? secret)
        {
            nsecHex = null;
            relay = null;
            secret = null;

            if (prefs == null) return false;
            if (!prefs.GetBoolean("pref_bunker_enabled", false)) return false;

            var nsec = SecurePreferences.GetNsec(this);
            if (!NostrKeyDecoder.TryDecodeNsecToHex(nsec, out nsecHex) || string.IsNullOrEmpty(nsecHex) || nsecHex.Length != 64)
                return false;

            relay = prefs.GetString("pref_bunker_relay", "wss://ephemeral.snowflare.cc/") ?? "wss://ephemeral.snowflare.cc/";
            secret = prefs.GetString("pref_bunker_secret", null);
            return true;
        }
    }
}
