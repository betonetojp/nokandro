using Android.Content;
using Android.OS;

namespace nokandro
{
    [BroadcastReceiver(Exported = true)]
    [IntentFilter(new[] { "com.nokakoi.nokandro.ACTION_START", "com.nokakoi.nokandro.ACTION_STOP" })]
    public class TaskerReceiver : BroadcastReceiver
    {
        private const string PREFS_NAME = "nokandro_prefs";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null) return;

            switch (intent.Action)
            {
                case "com.nokakoi.nokandro.ACTION_START":
                    HandleStart(context);
                    break;
                case "com.nokakoi.nokandro.ACTION_STOP":
                    HandleStop(context);
                    break;
            }
        }

        private static void HandleStop(Context context)
        {
            try
            {
                var stopIntent = new Intent(context, typeof(NostrService));
                stopIntent.SetAction("STOP");
                context.StartService(stopIntent);
            }
            catch { }
        }

        private static void HandleStart(Context context)
        {
            if (NostrService.IsRunning) return;

            try
            {
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                if (prefs == null) return;

                var npub = prefs.GetString("pref_npub", string.Empty) ?? string.Empty;
                if (string.IsNullOrEmpty(npub)) return;

                var relay = prefs.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/") ?? "wss://relay-jp.nostr.wirednet.jp/";

                var serviceIntent = new Intent(context, typeof(NostrService));
                serviceIntent.PutExtra("relay", relay);
                serviceIntent.PutExtra("npub", npub);

                var nsec = prefs.GetString("pref_nsec", string.Empty) ?? string.Empty;
                if (!string.IsNullOrEmpty(nsec)) serviceIntent.PutExtra("nsec", nsec);

                serviceIntent.PutExtra("allowOthers", prefs.GetBoolean("pref_allow_others", false));
                serviceIntent.PutExtra("enableTts", prefs.GetBoolean("pref_enable_tts", true));
                serviceIntent.PutExtra("skipContentWarning", prefs.GetBoolean("pref_skip_cw", false));
                serviceIntent.PutExtra("autoStop", prefs.GetBoolean("pref_auto_stop", true));
                serviceIntent.PutExtra("truncateLen", prefs.GetInt("pref_truncate_len", 50));
                serviceIntent.PutExtra("truncateEllipsis", prefs.GetString("pref_truncate_ellipsis", " ...") ?? " ...");
                serviceIntent.PutExtra("voiceFollowed", prefs.GetString("pref_voice_followed", string.Empty) ?? string.Empty);
                serviceIntent.PutExtra("voiceOther", prefs.GetString("pref_voice_other", string.Empty) ?? string.Empty);
                serviceIntent.PutExtra("speechRate", prefs.GetFloat("pref_speech_rate", 1.0f));
                serviceIntent.PutExtra("speakPetname", prefs.GetBoolean("pref_speak_petname", false));
                serviceIntent.PutExtra("enableMusicStatus", prefs.GetBoolean("pref_music_status", false));

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    context.StartForegroundService(serviceIntent);
                }
                else
                {
                    context.StartService(serviceIntent);
                }
            }
            catch { }
        }
    }
}
