using Android.Content;
using Android.OS;
using Android.Speech.Tts;
using Android.Views;
using System.Text.RegularExpressions;
using System.Text;

namespace nokandro
{
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public partial class MainActivity : Activity
    {
        const string PREFS_NAME = "nokandro_prefs";
        const string PREF_VOICE_FOLLOWED = "pref_voice_followed";
        const string PREF_VOICE_OTHER = "pref_voice_other";
        const string PREF_RELAY = "pref_relay";
        const string PREF_NPUB = "pref_npub";
        const string PREF_SIGNER_PACKAGE = "pref_signer_package";
        const string PREF_TRUNCATE_LEN = "pref_truncate_len";
        const string PREF_ALLOW_OTHERS = "pref_allow_others";
        // maximum length for displayed content before truncation
        private const int CONTENT_TRUNCATE_LENGTH = 20;

        private const string ACTION_LAST_CONTENT = "nokandro.ACTION_LAST_CONTENT";
        private const int RC_GET_PUBKEY = 1002;

        private TextView? _lastContentView;
        private BroadcastReceiver? _receiver;
        private TextView? _logTextView;
        // keep a reference to npub EditText so incoming intents can update it
        private EditText? _npubEditField;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
#pragma warning disable CS8600,CS8601,CS8602
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            // Use custom action bar layout to display app title with right-aligned small version
            try
            {
                var inflater = LayoutInflater.From(this);
                var custom = inflater.Inflate(Resource.Layout.actionbar_title_with_version, null);
                var titleTv = custom.FindViewById<TextView>(Resource.Id.appTitleText);
                var verTv = custom.FindViewById<TextView>(Resource.Id.versionText);
                titleTv?.Text = GetString(Resource.String.app_name);
                var versionName = "v0.0.0";
                try
                {
                    var pkg = PackageManager.GetPackageInfo(PackageName, 0);
                    versionName = "v" + (pkg?.VersionName ?? "0.0.0");
                }
                catch { }
                verTv?.Text = versionName;

                try
                {
                    if (ActionBar != null)
                    {
                        ActionBar.SetDisplayShowCustomEnabled(true);
                        ActionBar.SetDisplayShowTitleEnabled(false);
                        var layoutParams = new ActionBar.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                        ActionBar.SetCustomView(custom, layoutParams);
                    }
                    else
                    {
                        // fallback: set Activity title with version appended
                        this.Title = GetString(Resource.String.app_name) + " " + versionName;
                    }
                }
                catch { }
            }
            catch { }

            // Find log UI
            var refreshLogBtn = FindViewById<Button>(Resource.Id.refreshLogBtn);
            var shareLogBtn = FindViewById<Button>(Resource.Id.shareLogBtn);
            _logTextView = FindViewById<TextView>(Resource.Id.logTextView);

            // Refresh log display
            if (refreshLogBtn != null)
            {
                refreshLogBtn.Click += (s, e) =>
                {
                    try
                    {
#if ENABLE_LOG
                        var content = ReadLogContent();
                        RunOnUiThread(() => { _logTextView?.Text = content; });
#else
                        RunOnUiThread(() => { _logTextView?.Text = "(logging disabled in production build)"; });
#endif
                    }
                    catch { }
                };
            }

            // Share log content
            if (shareLogBtn != null)
            {
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
            }

            // Hide log UI entirely in production builds (when ENABLE_LOG not defined)
#if !ENABLE_LOG
            try
            {
                refreshLogBtn?.Visibility = ViewStates.Gone;
                shareLogBtn?.Visibility = ViewStates.Gone;
                _logTextView?.Visibility = ViewStates.Gone;
            }
            catch { }
#endif

            // Request POST_NOTIFICATIONS runtime permission (Android 13+)
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            {
                if (CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                {
                    RequestPermissions([Android.Manifest.Permission.PostNotifications], 1001);
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
            var amberBtn = FindViewById<Button>(Resource.Id.amberGetBtn);
            var allowOthersSwitch = FindViewById<Switch>(Resource.Id.allowOthersSwitch);
            var voiceFollowedSpinner = FindViewById<Spinner>(Resource.Id.voiceFollowedSpinner);
            var voiceOtherSpinner = FindViewById<Spinner>(Resource.Id.voiceOtherSpinner);
            var refreshVoicesBtn = FindViewById<Button>(Resource.Id.refreshVoicesBtn);
            // Resolve new view IDs via GetIdentifier to avoid requiring regenerated Resource.designer here
            var speechRateSeekBarId = Resources.GetIdentifier("speechRateSeekBar", "id", PackageName);
            var speechRateValueId = Resources.GetIdentifier("speechRateValue", "id", PackageName);
            var speechRateSeekBar = speechRateSeekBarId != 0 ? FindViewById<SeekBar>(speechRateSeekBarId) : null;
            var speechRateValue = speechRateValueId != 0 ? FindViewById<TextView>(speechRateValueId) : null;
            var startBtn = FindViewById<Button>(Resource.Id.startBtn);
            var stopBtn = FindViewById<Button>(Resource.Id.stopBtn);
            _lastContentView = FindViewById<TextView>(Resource.Id.lastContentText);
            var followStatusText = FindViewById<TextView>(Resource.Id.followStatusText);
            var muteStatusText = FindViewById<TextView>(Resource.Id.muteStatusText);
            var truncateEdit = FindViewById<EditText>(Resource.Id.truncateEdit);

            // Ensure required views are present to satisfy nullability and avoid runtime NREs
            if (relayEdit == null || npubEdit == null || allowOthersSwitch == null ||
                voiceFollowedSpinner == null || voiceOtherSpinner == null || refreshVoicesBtn == null ||
                startBtn == null || stopBtn == null || _lastContentView == null || followStatusText == null ||
                muteStatusText == null ||
                truncateEdit == null)
            {
                // Critical layout elements missing; bail out
                try { Toast.MakeText(this, "UI initialization failed", ToastLength.Short).Show(); } catch { }
                return;
            }

            // From this point locals are known to be non-null — create non-nullable aliases to inform the compiler
            var relay = (EditText)relayEdit!;
            var npub = (EditText)npubEdit!;
            // keep reference for updating from incoming intents
            _npubEditField = npub;
            var allowOthers = (Switch)allowOthersSwitch!;
            var voiceFollowed = (Spinner)voiceFollowedSpinner!;
            var voiceOther = (Spinner)voiceOtherSpinner!;
            var refreshVoices = (Button)refreshVoicesBtn!;
            var speechSeek = (SeekBar?)speechRateSeekBar;
            var speechVal = (TextView?)speechRateValue;
            var start = (Button)startBtn!;
            var stop = (Button)stopBtn!;
            var lastContent = (TextView)_lastContentView!;
            var followStatus = (TextView)followStatusText!;
            var muteStatus = (TextView)muteStatusText!;
            var truncate = (EditText)truncateEdit!;

            // restore saved relay + npub and truncate length if present
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                relay.Text = prefs.GetString(PREF_RELAY, "wss://yabu.me");
                npub.Text = prefs.GetString(PREF_NPUB, string.Empty);
                var savedLen = prefs.GetInt(PREF_TRUNCATE_LEN, CONTENT_TRUNCATE_LENGTH);
                truncate.Text = savedLen.ToString();
                // restore allowOthers switch
                allowOthers.Checked = prefs.GetBoolean(PREF_ALLOW_OTHERS, false);
                // restore saved speech rate (mapped range 0.50..1.50)
                try
                {
                    float savedRate = 1.0f;
                    try { savedRate = prefs?.GetFloat("pref_speech_rate", 1.0f) ?? 1.0f; } catch { }
                    // also support string fallback
                    try
                    {
                        if (savedRate == 1.0f)
                        {
                            var s = prefs?.GetString("pref_speech_rate", null);
                            if (!string.IsNullOrEmpty(s) && float.TryParse(s, out var fv)) savedRate = fv;
                        }
                    }
                    catch { }

                    if (speechSeek != null)
                    {
                        var prog = (int)Math.Round((savedRate - 0.5f) * 200.0f);
                        prog = Math.Max(0, Math.Min(200, prog));
                        speechSeek.Progress = prog;
                        speechVal?.Text = string.Format("{0:0.00}x", savedRate);
                    }
                }
                catch { }
            }
            catch { }

            // save allowOthers when toggled
            allowOthers.CheckedChange += (s, e) =>
            {
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var edit = prefs?.Edit();
                    if (edit != null)
                    {
                        edit.PutBoolean(PREF_ALLOW_OTHERS, allowOthers.Checked);
                        edit.Apply();
                    }
                }
                catch { }
            };

            // save truncate length when edit loses focus
            if (truncate != null)
            {
                truncate.FocusChange += (s, e) =>
                {
                    if (!e.HasFocus)
                    {
                        try
                        {
                            var val = int.Parse(truncate.Text ?? string.Empty);
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var edit = prefs?.Edit();
                            if (edit != null)
                            {
                                edit.PutInt(PREF_TRUNCATE_LEN, val);
                                edit.Apply();
                            }
                        }
                        catch { }
                    }
                };
            }

            // Populate voices initially (async)
            _ = PopulateVoicesAsync(voiceFollowed, voiceOther);

            refreshVoices.Click += async (s, e) => await PopulateVoicesAsync(voiceFollowed, voiceOther);

            // Save preference when spinner selection changes
            voiceFollowed.ItemSelected += (s, e) =>
            {
                var selected = voiceFollowed.SelectedItem?.ToString() ?? string.Empty;
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    edit.PutString(PREF_VOICE_FOLLOWED, selected);
                    edit.Apply();
                }
            };

            voiceOther.ItemSelected += (s, e) =>
            {
                var selected = voiceOther.SelectedItem?.ToString() ?? string.Empty;
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    edit.PutString(PREF_VOICE_OTHER, selected);
                    edit.Apply();
                }
            };

            // Amber button: try to invoke Amber (NIP-55) via a VIEW intent; use nostrsigner scheme when available and prefer matching package
            if (amberBtn != null)
            {
                amberBtn.Click += (s, e) =>
                {
                    try
                    {
                        var callback = "nokandro://amber_callback";
                        string reqUri;

                        // Build base intent using nostrsigner or nostr scheme per NIP-55 Using Intents
                        var baseUri = IsExternalSignerInstalled(this) ? Android.Net.Uri.Parse("nostrsigner:") : Android.Net.Uri.Parse("nostr:");
                        var intent = new Intent(Intent.ActionView, baseUri);

                        // Mark this as a get_public_key request per NIP-55 Using Intents
                        try { intent.PutExtra("type", "get_public_key"); } catch { }

                        // Provide optional default permissions for user to approve permanently
                        try
                        {
                            var permissionsJson = "[{\"type\":\"sign_event\",\"kind\":22242},{\"type\":\"nip44_decrypt\"}]";
                            intent.PutExtra("permissions", permissionsJson);
                        }
                        catch { }

                        // Add flags so signer can handle multiple requests without opening multiple activities
                        intent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

                        // prefer using stored signer package if present
                        try
                        {
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var storedPkg = prefs?.GetString(PREF_SIGNER_PACKAGE, null);
                            if (!string.IsNullOrEmpty(storedPkg))
                            {
                                intent.SetPackage(storedPkg);
                            }
                            else
                            {
                                // Prefer Amber package if installed
                                var amberPkg = "com.greenart7c3.nostrsigner";
                                try
                                {
                                    var info = PackageManager.GetPackageInfo(amberPkg, 0);
                                    if (info != null)
                                    {
                                        intent.SetPackage(amberPkg);
                                        try
                                        {
                                            var edit = prefs?.Edit();
                                            if (edit != null) { edit.PutString(PREF_SIGNER_PACKAGE, amberPkg); edit.Apply(); }
                                        }
                                        catch { }
                                    }
                                }
                                catch (Android.Content.PM.PackageManager.NameNotFoundException)
                                {
                                    // Amber not installed — fallback to any handler
                                    try
                                    {
                                        var infos = PackageManager.QueryIntentActivities(intent, 0);
                                        if (infos != null && infos.Count > 0)
                                        {
                                            var pkgName = infos[0].ActivityInfo?.PackageName;
                                            if (!string.IsNullOrEmpty(pkgName)) intent.SetPackage(pkgName);
                                        }
                                    }
                                    catch { }
                                }
                                catch { }
                            }
                        }
                        catch { }

                        // Use StartActivityForResult to receive pubkey/package back
                        StartActivityForResult(intent, RC_GET_PUBKEY);
                    }
                    catch
                    {
                        try { Toast.MakeText(this, "Failed to invoke Amber (no handler)", ToastLength.Short).Show(); } catch { }
                    }
                };
            }

            start.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                var truncateLen = CONTENT_TRUNCATE_LENGTH;
                try { truncateLen = int.Parse(truncate.Text ?? string.Empty); } catch { }

                intent.PutExtra("relay", relay.Text ?? "wss://yabu.me");
                intent.PutExtra("npub", npub.Text ?? string.Empty);
                intent.PutExtra("allowOthers", allowOthers.Checked);
                intent.PutExtra("truncateLen", truncateLen);
                var followedVoice = voiceFollowed.SelectedItem?.ToString() ?? string.Empty;
                var otherVoice = voiceOther.SelectedItem?.ToString() ?? string.Empty;
                intent.PutExtra("voiceFollowed", followedVoice);
                intent.PutExtra("voiceOther", otherVoice);
                // include speech rate (float)
                // map SeekBar progress (0..200) to speech rate range 0.50..1.50
                var speechRate = 0.5f + ((speechSeek != null ? (float)speechSeek.Progress : 100f) / 200.0f);
                intent.PutExtra("speechRate", speechRate);

                // persist selections
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    edit.PutString(PREF_VOICE_FOLLOWED, followedVoice);
                    edit.PutString(PREF_VOICE_OTHER, otherVoice);
                    edit.PutString(PREF_RELAY, relay.Text ?? "wss://yabu.me");
                    edit.PutString(PREF_NPUB, npub.Text ?? string.Empty);
                    edit.PutInt(PREF_TRUNCATE_LEN, truncateLen);
                    edit.PutBoolean(PREF_ALLOW_OTHERS, allowOthers.Checked);
                    // persist speech rate as mapped value (0.50..1.50)
                    try { edit.PutFloat("pref_speech_rate", speechRate); } catch { }
                    edit.Apply();
                }

                // Use StartService; the service will call StartForeground
                StartService(intent);
            };

            stop.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                StopService(intent);
            };

            // Handle SeekBar changes
            if (speechSeek != null)
            {
                speechSeek.ProgressChanged += (s, e) =>
                {
                    var p = speechSeek.Progress;
                    // map progress to 0.50..1.50
                    var r = 0.5f + p / 200.0f;
                    speechVal?.Text = string.Format("{0:0.00}x", r);
                };

                speechSeek.StopTrackingTouch += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            // save mapped speech rate (0.50..1.50)
                            edit.PutFloat("pref_speech_rate", 0.5f + speechSeek.Progress / 200.0f);
                            edit.Apply();
                        }
                    }
                    catch { }

                    // notify service in-process to update speech rate immediately
                    try
                    {
                        var intent = new Intent("nokandro.ACTION_SET_SPEECH_RATE");
                        intent.PutExtra("speechRate", 0.5f + speechSeek.Progress / 200.0f);
                        LocalBroadcast.SendBroadcast(this, intent);
                    }
                    catch { }
                };
            }

            // register receiver
            _receiver = new BroadcastReceiver();
            _receiver.Receive += (ctx, intent) =>
            {
                if (intent.Action == "nokandro.ACTION_FOLLOW_UPDATE")
                {
                    var loaded = intent.GetBooleanExtra("followLoaded", false);
                    var count = intent.GetIntExtra("followCount", 0);
                    var text = loaded ? $"Follow list: loaded ({count})" : "Follow list: (not loaded)";
                    RunOnUiThread(() => { followStatus.Text = text; });
                    return;
                }

                if (intent.Action == "nokandro.ACTION_MUTE_UPDATE")
                {
                    var loaded = intent.GetBooleanExtra("muteLoaded", false);
                    var count = intent.GetIntExtra("muteCount", 0);
                    var text = loaded ? $"Mute list: loaded ({count})" : "Mute list: (not loaded)";
                    RunOnUiThread(() => { muteStatus.Text = text; });
                    return;
                }

                var text2 = intent.GetStringExtra("content") ?? string.Empty;
                var isFollowed = intent.GetBooleanExtra("isFollowed", false);
                var displayText = ShortenUrls(text2);
                var prefix = isFollowed ? "* " : "- ";
                RunOnUiThread(() => { lastContent.Text = prefix + displayText; });
            };
            var filter = new IntentFilter();
            filter.AddAction(ACTION_LAST_CONTENT);
            filter.AddAction("nokandro.ACTION_FOLLOW_UPDATE");
            filter.AddAction("nokandro.ACTION_MUTE_UPDATE");
            LocalBroadcast.RegisterReceiver(_receiver, filter);
#pragma warning restore CS8600,CS8601,CS8602
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            try
            {
                if (requestCode == RC_GET_PUBKEY)
                {
                    if (resultCode != Result.Ok)
                    {
                        try { Toast.MakeText(this, "Sign request rejected", ToastLength.Short).Show(); } catch { }
                        return;
                    }

                    string? result = null;
                    string? signerPkg = null;

                    // try extras
                    try { if (data != null) { result = data.GetStringExtra("result"); signerPkg = data.GetStringExtra("package"); } } catch { }
                    // try uri query parameters if not in extras
                    try
                    {
                        if (string.IsNullOrEmpty(result) && data?.Data != null)
                        {
                            var uri = data.Data;
                            result = uri.GetQueryParameter("result");
                            if (string.IsNullOrEmpty(signerPkg)) signerPkg = uri.GetQueryParameter("package");
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty(result))
                    {
                        // fallback: try DataString body
                        try { var ds = data?.DataString; if (!string.IsNullOrEmpty(ds)) result = ds; } catch { }
                    }

                    if (string.IsNullOrEmpty(result))
                    {
                        try { Toast.MakeText(this, "No pubkey returned", ToastLength.Short).Show(); } catch { }
                        return;
                    }

                    // normalize: if result contains npub, extract; if hex 64 chars, convert to npub via bech32 if needed
                    string npubVal = result;
                    var m = CreateNpubPlainRegex().Match(result);
                    if (m.Success) npubVal = m.Value;
                    else
                    {
                        // if looks like 64-hex
                        var hexm = CreateHex64Regex().Match(result);
                        if (hexm.Success)
                        {
                            try { var bytes = HexToBytes(hexm.Value); npubVal = Bech32Encode("npub", ConvertBits(bytes, 8, 5, true)); } catch { }
                        }
                    }

                    if (string.IsNullOrEmpty(npubVal))
                    {
                        try { Toast.MakeText(this, "Invalid pubkey format", ToastLength.Short).Show(); } catch { }
                        return;
                    }

                    // Save to prefs and update UI
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            edit.PutString(PREF_NPUB, npubVal);
                            if (!string.IsNullOrEmpty(signerPkg)) edit.PutString(PREF_SIGNER_PACKAGE, signerPkg);
                            edit.Apply();
                        }
                    }
                    catch { }

                    RunOnUiThread(() =>
                    {
                        try { _npubEditField?.Text = npubVal; } catch { }
                        try { Toast.MakeText(this, "npub acquired", ToastLength.Short).Show(); } catch { }
                    });
                }
            }
            catch { }
        }

        private static bool IsExternalSignerInstalled(Context context)
        {
            try
            {
                var intent = new Intent(Intent.ActionView);
                intent.SetData(Android.Net.Uri.Parse("nostrsigner:"));
                var infos = context.PackageManager.QueryIntentActivities(intent, 0);
                return infos != null && infos.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            try { HandleIncomingNpub(intent); } catch { }
        }

        protected override void OnDestroy()
        {
            if (_receiver != null)
            {
                try { LocalBroadcast.UnregisterReceiver(_receiver); } catch { }
            }
            base.OnDestroy();
        }

        private void HandleIncomingNpub(Intent? intent)
        {
            if (intent == null) return;
            try
            {
                // Try to extract npub from data URI first
                var data = intent.DataString ?? string.Empty;

                // If no data, try common extras
                if (string.IsNullOrEmpty(data))
                {
                    data = intent.GetStringExtra("npub") ?? intent.GetStringExtra("pubkey") ?? string.Empty;
                }

                if (string.IsNullOrEmpty(data)) return;

                // Normalize and extract npub (support "nostr:npub1..." and plain "npub1...")
                var m = CreateNeventNoteRegex().Match(data);
                string npubVal = string.Empty;
                if (m.Success) npubVal = m.Value;
                else
                {
                    // fallback: if data is like nostr:<npub>
                    if (data.StartsWith("nostr:", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = data[6..];
                        var mm = CreateNpubPlainRegex().Match(rest);
                        if (mm.Success) npubVal = mm.Value;
                        else npubVal = rest;
                    }
                    else
                    {
                        npubVal = data;
                    }
                }

                if (string.IsNullOrEmpty(npubVal)) return;

                // Save to prefs and update UI
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var edit = prefs?.Edit();
                    if (edit != null)
                    {
                        edit.PutString(PREF_NPUB, npubVal);
                        edit.Apply();
                    }
                }
                catch { }

                RunOnUiThread(() =>
                {
                    try { _npubEditField?.Text = npubVal; } catch { }
                    try { Toast.MakeText(this, "npub acquired", ToastLength.Short).Show(); } catch { }
                });
            }
            catch { }
        }

        private static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0) throw new ArgumentException("Invalid hex length");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        // convertbits from BIP173 reference
        private static byte[] ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            var acc = 0;
            var bits = 0;
            var maxv = (1 << toBits) - 1;
            var result = new List<byte>();
            foreach (var value in data)
            {
                acc = (acc << fromBits) | (value & ((1 << fromBits) - 1));
                bits += fromBits;
                while (bits >= toBits)
                {
                    bits -= toBits;
                    result.Add((byte)((acc >> bits) & maxv));
                }
            }
            if (pad)
            {
                if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
            else
            {
                if (bits >= fromBits) throw new ArgumentException("Illegal zero padding");
                if (((acc << (toBits - bits)) & maxv) != 0) throw new ArgumentException("Non-zero padding");
            }
            return [.. result];
        }

        private static readonly string Bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

        private static int Polymod(byte[] values)
        {
            var chk = 1;
            var generators = new int[] { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffff) << 5) ^ v;
                for (int i = 0; i < 5; i++) if (((top >> i) & 1) != 0) chk ^= generators[i];
            }
            return chk;
        }

        private static byte[] HrpExpand(string hrp)
        {
            var hrpBytes = Encoding.ASCII.GetBytes(hrp);
            var expand = new List<byte>();
            foreach (var b in hrpBytes) expand.Add((byte)(b >> 5));
            expand.Add(0);
            foreach (var b in hrpBytes) expand.Add((byte)(b & 31));
            return [.. expand];
        }

        private static string Bech32Encode(string hrp, byte[] data)
        {
            var combined = new List<byte>();
            combined.AddRange(data);
            // create checksum
            var checksum = CreateChecksum(hrp, data);
            combined.AddRange(checksum);
            var sb = new StringBuilder();
            sb.Append(hrp);
            sb.Append('1');
            foreach (var b in combined) sb.Append(Bech32Chars[b]);
            return sb.ToString();
        }

        private static byte[] CreateChecksum(string hrp, byte[] data)
        {
            var values = new List<byte>();
            values.AddRange(HrpExpand(hrp));
            values.AddRange(data);
            values.AddRange(new byte[6]);
            var polymod = Polymod([.. values]) ^ 1;
            var ret = new byte[6];
            for (int i = 0; i < 6; ++i) ret[i] = (byte)((polymod >> (5 * (5 - i))) & 31);
            return ret;
        }

        private string ShortenUrls(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            // replace any URL with the placeholder "（URL省略）"
            var replaced = UrlPattern.Replace(input, "（URL省略）");

            // replace nostr npub/event/note references
            try
            {
                replaced = NpubPattern.Replace(replaced, "（メンション）");
                replaced = NeventNotePattern.Replace(replaced, "（引用）");
            }
            catch { }

            int len = CONTENT_TRUNCATE_LENGTH;
            // try to read current preference value from shared prefs
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                len = prefs?.GetInt(PREF_TRUNCATE_LEN, CONTENT_TRUNCATE_LENGTH) ?? len;
            }
            catch { }

            if (replaced.Length > len)
            {
                return string.Concat(replaced.AsSpan(0, len), "（以下略）");
            }
            return replaced;
        }

        private async Task PopulateVoicesAsync(Spinner followedSpinner, Spinner otherSpinner)
        {
            List<string> voices = ["default"];
            TextToSpeech? tts = null;
            try
            {
                tts = new TextToSpeech(this, null);

                // Wait until voices are available (polling), timeout after 3 seconds
                var timeout = 3000;
                var waited = 0;
                while ((tts == null || tts.Voices == null || tts.Voices.Count == 0) && waited < timeout)
                {
                    await Task.Delay(200);
                    waited += 200;
                }

                // Build a non-nullable list of voice names
                var names = tts?.Voices?.Where(v => !string.IsNullOrEmpty(v?.Name)).Select(v => v!.Name!).ToList();
                if (names != null && names.Count > 0) voices = names;
                if (voices.Count == 0) voices = ["default"];
            }
            catch
            {
                voices = ["default"];
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
            var savedFollowed = prefs?.GetString(PREF_VOICE_FOLLOWED, null);
            var savedOther = prefs?.GetString(PREF_VOICE_OTHER, null);
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
                if (text.Length > 20000) return string.Concat("...", text.AsSpan(text.Length - 20000));
                return text;
            }
            catch
            {
                return "(failed to read log)";
            }
        }

        private static readonly Regex UrlPattern = CreateUrlRegex();
        private static readonly Regex NpubPattern = CreateNpubRegex();
        private static readonly Regex NeventNotePattern = CreateNeventNoteRegex();

        [GeneratedRegex("(https?://\\S+|www\\.\\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateUrlRegex();
        [GeneratedRegex("\\bnostr:npub1\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateNpubRegex();
        [GeneratedRegex("\\bnostr:(?:nevent1|note1)\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateNeventNoteRegex();
        [GeneratedRegex("npub1[0-9a-zA-Z]+", RegexOptions.IgnoreCase, "ja-JP")]
        private static partial Regex CreateNpubPlainRegex();
        [GeneratedRegex("^[0-9a-fA-F]{64}$")]
        private static partial Regex CreateHex64Regex();
    }
}