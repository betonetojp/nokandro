using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using Android.Speech.Tts;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.IO;
using Android.Views;

namespace nokandro
{
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const string PREFS_NAME = "nokandro_prefs";
        const string PREF_VOICE_FOLLOWED = "pref_voice_followed";
        const string PREF_VOICE_OTHER = "pref_voice_other";
        const string PREF_RELAY = "pref_relay";
        const string PREF_NPUB = "pref_npub";
        const string PREF_TRUNCATE_LEN = "pref_truncate_len";
        const string PREF_ALLOW_OTHERS = "pref_allow_others";
        // maximum length for displayed content before truncation
        private const int CONTENT_TRUNCATE_LENGTH = 20;

        private const string ACTION_LAST_CONTENT = "nokandro.ACTION_LAST_CONTENT";

        private TextView? _lastContentView;
        private BroadcastReceiver? _receiver;
        private TextView? _logTextView;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            // Find log UI
            var refreshLogBtn = FindViewById<Button>(Resource.Id.refreshLogBtn);
            var shareLogBtn = FindViewById<Button>(Resource.Id.shareLogBtn);
            _logTextView = FindViewById<TextView>(Resource.Id.logTextView);

            // Refresh log display
            refreshLogBtn.Click += (s, e) =>
            {
                try
                {
#if ENABLE_LOG
                    var content = ReadLogContent();
                    RunOnUiThread(() => { _logTextView.Text = content; });
#else
                    RunOnUiThread(() => { _logTextView.Text = "(logging disabled in production build)"; });
#endif
                }
                catch { }
            };

            // Share log content
            shareLogBtn.Click += (s, e) =>
            {
                try
                {
#if ENABLE_LOG
                    var content = ReadLogContent();
                    var send = new Intent(Intent.ActionSend);
                    send.SetType("text/plain");
                    send.PutExtra(Intent.ExtraSubject, "Nostr log");
                    send.PutExtra(Intent.ExtraText, content);
                    StartActivity(Intent.CreateChooser(send, "Share log"));
#else
                    Toast.MakeText(this, "Logging disabled in production build", ToastLength.Short).Show();
#endif
                }
                catch { }
            };

            // Hide log UI entirely in production builds (when ENABLE_LOG not defined)
#if !ENABLE_LOG
            try
            {
                refreshLogBtn.Visibility = ViewStates.Gone;
                shareLogBtn.Visibility = ViewStates.Gone;
                if (_logTextView != null) _logTextView.Visibility = ViewStates.Gone;
            }
            catch { }
#endif

            // Request POST_NOTIFICATIONS runtime permission (Android 13+)
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                {
                    RequestPermissions(new string[] { Android.Manifest.Permission.PostNotifications }, 1001);
                }
            }

            // Ensure notification channel exists so system Settings shows controllable channel
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var chanId = "nostr_tts_channel";
                var nm = (NotificationManager)GetSystemService(NotificationService);
                try
                {
                    var existing = nm.GetNotificationChannel(chanId);
                    if (existing == null)
                    {
                        var chan = new NotificationChannel(chanId, "Nostr TTS", NotificationImportance.Low);
                        nm.CreateNotificationChannel(chan);
                    }
                }
                catch { }
            }

            // Find views
            var relayEdit = FindViewById<EditText>(Resource.Id.relayEdit);
            var npubEdit = FindViewById<EditText>(Resource.Id.npubEdit);
            var allowOthersSwitch = FindViewById<Switch>(Resource.Id.allowOthersSwitch);
            var voiceFollowedSpinner = FindViewById<Spinner>(Resource.Id.voiceFollowedSpinner);
            var voiceOtherSpinner = FindViewById<Spinner>(Resource.Id.voiceOtherSpinner);
            var refreshVoicesBtn = FindViewById<Button>(Resource.Id.refreshVoicesBtn);
            var startBtn = FindViewById<Button>(Resource.Id.startBtn);
            var stopBtn = FindViewById<Button>(Resource.Id.stopBtn);
            _lastContentView = FindViewById<TextView>(Resource.Id.lastContentText);
            var followStatusText = FindViewById<TextView>(Resource.Id.followStatusText);
            var truncateEdit = FindViewById<EditText>(Resource.Id.truncateEdit);

            // restore saved relay + npub and truncate length if present
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                relayEdit.Text = prefs.GetString(PREF_RELAY, "wss://yabu.me");
                npubEdit.Text = prefs.GetString(PREF_NPUB, string.Empty);
                var savedLen = prefs.GetInt(PREF_TRUNCATE_LEN, CONTENT_TRUNCATE_LENGTH);
                truncateEdit.Text = savedLen.ToString();
                // restore allowOthers switch
                allowOthersSwitch.Checked = prefs.GetBoolean(PREF_ALLOW_OTHERS, false);
            }
            catch { }

            // save allowOthers when toggled
            allowOthersSwitch.CheckedChange += (s, e) =>
            {
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var edit = prefs.Edit();
                    edit.PutBoolean(PREF_ALLOW_OTHERS, allowOthersSwitch.Checked);
                    edit.Apply();
                }
                catch { }
            };

            // save truncate length when edit loses focus
            truncateEdit.FocusChange += (s, e) =>
            {
                if (!e.HasFocus)
                {
                    try
                    {
                        var val = int.Parse(truncateEdit.Text ?? string.Empty);
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs.Edit();
                        edit.PutInt(PREF_TRUNCATE_LEN, val);
                        edit.Apply();
                    }
                    catch { }
                }
            };

            // Populate voices initially (async)
            _ = PopulateVoicesAsync(voiceFollowedSpinner, voiceOtherSpinner);

            refreshVoicesBtn.Click += async (s, e) => await PopulateVoicesAsync(voiceFollowedSpinner, voiceOtherSpinner);

            // Save preference when spinner selection changes
            voiceFollowedSpinner.ItemSelected += (s, e) =>
            {
                var selected = voiceFollowedSpinner.SelectedItem?.ToString() ?? string.Empty;
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs.Edit();
                edit.PutString(PREF_VOICE_FOLLOWED, selected);
                edit.Apply();
            };

            voiceOtherSpinner.ItemSelected += (s, e) =>
            {
                var selected = voiceOtherSpinner.SelectedItem?.ToString() ?? string.Empty;
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs.Edit();
                edit.PutString(PREF_VOICE_OTHER, selected);
                edit.Apply();
            };

            startBtn.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                var truncateLen = CONTENT_TRUNCATE_LENGTH;
                try { truncateLen = int.Parse(truncateEdit.Text ?? string.Empty); } catch { }

                intent.PutExtra("relay", relayEdit.Text ?? "wss://relay.damus.io");
                intent.PutExtra("npub", npubEdit.Text ?? string.Empty);
                intent.PutExtra("allowOthers", allowOthersSwitch.Checked);
                intent.PutExtra("truncateLen", truncateLen);
                var followedVoice = voiceFollowedSpinner.SelectedItem?.ToString() ?? string.Empty;
                var otherVoice = voiceOtherSpinner.SelectedItem?.ToString() ?? string.Empty;
                intent.PutExtra("voiceFollowed", followedVoice);
                intent.PutExtra("voiceOther", otherVoice);

                // persist selections
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs.Edit();
                edit.PutString(PREF_VOICE_FOLLOWED, followedVoice);
                edit.PutString(PREF_VOICE_OTHER, otherVoice);
                edit.PutString(PREF_RELAY, relayEdit.Text ?? "wss://yabu.me");
                edit.PutString(PREF_NPUB, npubEdit.Text ?? string.Empty);
                edit.PutInt(PREF_TRUNCATE_LEN, truncateLen);
                edit.PutBoolean(PREF_ALLOW_OTHERS, allowOthersSwitch.Checked);
                edit.Apply();

                // Use StartService; the service will call StartForeground
                StartService(intent);
            };

            stopBtn.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                StopService(intent);
            };

            // register receiver
            _receiver = new BroadcastReceiver();
            _receiver.Receive += (ctx, intent) =>
            {
                if (intent.Action == "nokandro.ACTION_FOLLOW_UPDATE")
                {
                    var loaded = intent.GetBooleanExtra("followLoaded", false);
                    var count = intent.GetIntExtra("followCount", 0);
                    var text = loaded ? $"Follow list: loaded ({count})" : "Follow list: (not loaded)";
                    RunOnUiThread(() => { followStatusText.Text = text; });
                    return;
                }

                var text2 = intent.GetStringExtra("content") ?? string.Empty;
                var isFollowed = intent.GetBooleanExtra("isFollowed", false);
                var displayText = ShortenUrls(text2);
                var prefix = isFollowed ? "* " : "- ";
                RunOnUiThread(() => { _lastContentView.Text = prefix + displayText; });
            };
            var filter = new IntentFilter();
            filter.AddAction(ACTION_LAST_CONTENT);
            filter.AddAction("nokandro.ACTION_FOLLOW_UPDATE");
            LocalBroadcast.RegisterReceiver(_receiver, filter);
        }

        protected override void OnDestroy()
        {
            if (_receiver != null)
            {
                try { LocalBroadcast.UnregisterReceiver(_receiver); } catch { }
            }
            base.OnDestroy();
        }

        private string ShortenUrls(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            // replace any URL with the placeholder "（URL省略）"
            var rx = new Regex("(https?://\\S+|www\\.\\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var replaced = rx.Replace(input, "（URL省略）");
            int len = CONTENT_TRUNCATE_LENGTH;
            // try to read current preference value from shared prefs
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                len = prefs.GetInt(PREF_TRUNCATE_LEN, CONTENT_TRUNCATE_LENGTH);
            }
            catch { }

            if (replaced.Length > len)
            {
                return replaced.Substring(0, len) + "（以下略）";
            }
            return replaced;
        }

        private async Task PopulateVoicesAsync(Spinner followedSpinner, Spinner otherSpinner)
        {
            List<string> voices = new List<string> { "default" };
            TextToSpeech? tts = null;
            try
            {
                tts = new TextToSpeech(this, null);

                // Wait until voices are available (polling), timeout after 3 seconds
                var timeout = 3000;
                var waited = 0;
                while ((tts.Voices == null || tts.Voices.Count == 0) && waited < timeout)
                {
                    await Task.Delay(200);
                    waited += 200;
                }

                voices = tts?.Voices?.Select(v => v.Name).ToList() ?? voices;
                if (voices.Count == 0) voices = new List<string> { "default" };
            }
            catch
            {
                voices = new List<string> { "default" };
            }
            finally
            {
                try { tts?.Shutdown(); } catch { }
            }

            var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, voices);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            followedSpinner.Adapter = adapter;
            otherSpinner.Adapter = adapter;

            // restore saved selections if present
            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
            var savedFollowed = prefs.GetString(PREF_VOICE_FOLLOWED, null);
            var savedOther = prefs.GetString(PREF_VOICE_OTHER, null);
            if (!string.IsNullOrEmpty(savedFollowed))
            {
                var idx = voices.IndexOf(savedFollowed);
                if (idx >= 0) followedSpinner.SetSelection(idx);
            }
            if (!string.IsNullOrEmpty(savedOther))
            {
                var idx = voices.IndexOf(savedOther);
                if (idx >= 0) otherSpinner.SetSelection(idx);
            }
        }

        // local broadcast receiver wrapper
        class BroadcastReceiver : Android.Content.BroadcastReceiver
        {
            public event Action<Context, Intent> Receive = delegate { };
            public override void OnReceive(Context? context, Intent? intent)
            {
                if (context == null || intent == null) return;
                Receive(context, intent);
            }
        }

        private string ReadLogContent()
        {
            try
            {
                string path = string.Empty;
                try { path = Path.Combine(FilesDir?.AbsolutePath ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "nostr_log.txt"); } catch { }
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(no log)";
                var text = File.ReadAllText(path);
                if (text.Length > 20000) return "..." + text.Substring(text.Length - 20000);
                return text;
            }
            catch
            {
                return "(failed to read log)";
            }
        }
    }
}