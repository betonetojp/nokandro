using Android.Content;
using Android.OS;

namespace nokandro
{
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = true)]
    [IntentFilter([Intent.ActionBootCompleted, Intent.ActionLockedBootCompleted, Intent.ActionMyPackageReplaced])]
    public sealed class BootReceiver : BroadcastReceiver
    {
        private const string PREFS_NAME = "nokandro_prefs";
        private const string PREF_BUNKER_AUTOSTART_BOOT = "pref_bunker_autostart_boot";
        private const string PREF_BUNKER_ENABLED = "pref_bunker_enabled";
        private const string PREF_BUNKER_RELAY = "pref_bunker_relay";
        private const string PREF_BUNKER_SECRET = "pref_bunker_secret";
        private const string PREF_NSEC = "pref_nsec";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null) return;

            if (intent.Action != Intent.ActionBootCompleted &&
                intent.Action != Intent.ActionLockedBootCompleted &&
                intent.Action != Intent.ActionMyPackageReplaced)
                return;

            try
            {
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                if (prefs == null) return;

                var autoStart = prefs.GetBoolean(PREF_BUNKER_AUTOSTART_BOOT, false);
                var wantBunker = prefs.GetBoolean(PREF_BUNKER_ENABLED, false);
                if (!autoStart || !wantBunker) return;

                var nsec = SecurePreferences.GetNsec(context);
                if (!NostrKeyDecoder.TryDecodeNsecToHex(nsec, out var nsecHex) || string.IsNullOrEmpty(nsecHex)) return;

                var relay = prefs.GetString(PREF_BUNKER_RELAY, "wss://ephemeral.snowflare.cc/") ?? "wss://ephemeral.snowflare.cc/";
                var secret = prefs.GetString(PREF_BUNKER_SECRET, null);

                var serviceIntent = new Intent(context, typeof(BunkerService));
                serviceIntent.PutExtra("nsecHex", nsecHex);
                serviceIntent.PutExtra("relay", relay);
                if (!string.IsNullOrEmpty(secret)) serviceIntent.PutExtra("secret", secret);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(serviceIntent);
                else
                    context.StartService(serviceIntent);
            }
            catch { }
        }
    }
}
