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

        // nostrconnect:// sessions (shared across service lifetime)
        private static readonly Lock _ncLock = new();
        private static readonly Dictionary<string, NostrConnectSession> _ncSessions = [];

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            AppLog.D(TAG, "OnStartCommand: action=" + (intent?.Action ?? "(null)"));

            if (intent?.Action == "STOP")
            {
                return HandleStopBunker();
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

            if (string.IsNullOrEmpty(nsecHex) || nsecHex!.Length != 64)
            {
                AppLog.W(TAG, "Invalid nsecHex — stopping");
                // Must call StartForeground before StopSelf on Android 12+ if started via StartForegroundService
                try { StartForegroundNotification(); } catch { }
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            // Convert hex to bytes
            var privKey = new byte[32];
            for (int i = 0; i < 32; i++)
                privKey[i] = Convert.ToByte(nsecHex.Substring(i * 2, 2), 16);

            if (string.IsNullOrEmpty(relay))
                relay = "wss://ephemeral.snowflare.cc/";

            // Build foreground notification
            StartForegroundNotification();

            // Stop existing bunker if running (prevents duplicate instances with different secrets)
            try { _bunker?.Stop(); } catch { }
            _bunker = null;

            // Start bunker (reuse persisted secret if available)
            var secret = intent?.GetStringExtra("secret");
            _bunker = new NostrBunker(privKey, relay, secret);
            _bunker.OnLog += msg =>
            {
                try
                {
                    var b = new Intent("nokandro.ACTION_BUNKER_LOG");
                    b.PutExtra("message", msg);
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
            UpdateNotification();
            AppLog.D(TAG, "Bunker started");

            return StartCommandResult.Sticky;
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

            // Apply fallback relay if URI has no relay hints
            if (connectUri.Relays.Length == 0)
            {
                var fallbackRelay = intent.GetStringExtra("fallbackRelay") ?? "wss://nos.lol/";
                connectUri = connectUri.WithFallbackRelays(fallbackRelay);
                AppLog.D(TAG, $"No relay hint in URI, using fallback: {fallbackRelay}");
                try
                {
                    var b = new Intent("nokandro.ACTION_NC_LOG");
                    b.PutExtra("clientPubkey", connectUri.ClientPubkey);
                    b.PutExtra("message", $"No relay in URI, using {fallbackRelay}");
                    LocalBroadcast.SendBroadcast(this, b);
                }
                catch { }
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

                var session = new NostrConnectSession(privKey, connectUri);
                session.OnLog += msg =>
                {
                    try
                    {
                        var b = new Intent("nokandro.ACTION_NC_LOG");
                        b.PutExtra("clientPubkey", connectUri.ClientPubkey);
                        b.PutExtra("message", msg);
                        LocalBroadcast.SendBroadcast(this, b);
                    }
                    catch { }
                };

                _ncSessions[connectUri.ClientPubkey] = session;
                session.Start();
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
            try { _bunker?.Stop(); } catch { }
            _bunker = null;
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
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
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
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
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
                        .SetSmallIcon(Resource.Mipmap.ic_launcher)
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
                        .SetSmallIcon(Resource.Mipmap.ic_launcher)
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
    }
}
