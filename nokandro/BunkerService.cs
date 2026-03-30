using Android.Content;
using Android.OS;

namespace nokandro
{
    [Service]
    public class BunkerService : Service
    {
        public static bool IsRunning { get; private set; }

        private const string TAG = "BunkerService";
        private const int NOTIF_ID = 1002;
        private NostrBunker? _bunker;

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            AppLog.D(TAG, "OnStartCommand: action=" + (intent?.Action ?? "(null)"));

            if (intent?.Action == "STOP")
            {
                StopSelf();
                return StartCommandResult.NotSticky;
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

            // Start bunker
            _bunker = new NostrBunker(privKey, relay);
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

            // Broadcast bunker URI and started state
            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_STARTED");
                b.PutExtra("bunkerUri", _bunker.BunkerUri);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            _bunker.Start();
            AppLog.D(TAG, "Bunker started");

            return StartCommandResult.Sticky;
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
                var mainPending = PendingIntent.GetActivity(this, 0, mainIntent, piFlags);

                var stopIntent = new Intent(this, typeof(BunkerService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 1, stopIntent, piFlags);

                var notif = new Notification.Builder(this, chanId)
                    .SetContentTitle("Nostr Bunker")
                    .SetContentText("NIP-46 remote signer active")
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }

                StartForeground(NOTIF_ID, notif.Build());
            }
            else
            {
                var mainIntent = new Intent(this, typeof(MainActivity));
                var mainPending = PendingIntent.GetActivity(this, 0, mainIntent, piFlags);

                var stopIntent = new Intent(this, typeof(BunkerService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 1, stopIntent, piFlags);

                var notif = new Notification.Builder(this)
                    .SetContentTitle("Nostr Bunker")
                    .SetContentText("NIP-46 remote signer active")
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch { }

                StartForeground(NOTIF_ID, notif.Build());
            }
#pragma warning restore CA1416, CA1422
        }

        public override void OnDestroy()
        {
            AppLog.D(TAG, "OnDestroy");
            IsRunning = false;
            try { _bunker?.Stop(); } catch { }
            _bunker = null;

            try
            {
                var b = new Intent("nokandro.ACTION_BUNKER_STOPPED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            base.OnDestroy();
        }
    }
}
