using Android.Content;
using Android.OS;
using Android.Speech.Tts;
using Android.Views;
using System.Text;
using System.Text.RegularExpressions;

namespace nokandro
{
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public partial class MainActivity : Activity
    {
        const string PREFS_NAME = "nokandro_prefs";
        const string PREF_VOICE_FOLLOWED = "pref_voice_followed";
        const string PREF_VOICE_OTHER = "pref_voice_other";
        const string PREF_VOICE_LANG = "pref_voice_lang";
        const string PREF_RELAY = "pref_relay";
        const string PREF_NPUB = "pref_npub";
        const string PREF_SIGNER_PACKAGE = "pref_signer_package";
        const string PREF_TRUNCATE_LEN = "pref_truncate_len";
        const string PREF_ALLOW_OTHERS = "pref_allow_others";
        const string PREF_SPEAK_PETNAME = "pref_speak_petname";
        // maximum length for displayed content before truncation
        private const int CONTENT_TRUNCATE_LENGTH = 20;

        private const string ACTION_LAST_CONTENT = "nokandro.ACTION_LAST_CONTENT";
        private const int RC_GET_PUBKEY = 1002;

        private TextView? _lastContentView;
        private BroadcastReceiver? _receiver;
        private BroadcastReceiver? _serviceStateReceiver;
        private TextView? _logTextView;
        private Android.OS.Handler? _mainHandler;
        private readonly object _logLock = new object();
        private List<string> _logBuffer = new List<string>();
        private bool _logFlushScheduled = false;
        private const int LOG_FLUSH_MS = 150;
        // keep a reference to npub EditText so incoming intents can update it
        private EditText? _npubEdit_field;

        // Language spinner state to avoid resetting while user interacts
        private bool _langPopulating = false;
        private List<string> _availableLangCodes = new List<string>();
        private string? _lastSelectedLangCode = null;

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

            // Make log TextView scrollable and selectable so debug lines can be viewed/copied
            try
            {
                if (_logTextView != null)
                {
                    try { _logTextView.MovementMethod = new Android.Text.Method.ScrollingMovementMethod(); } catch { }
                    try { _logTextView.SetTextIsSelectable(true); } catch { }
                    try { _logTextView.VerticalScrollBarEnabled = true; } catch { }
                }
            }
            catch { }

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
                // hide log text view in production builds
                try { _logTextView?.Visibility = ViewStates.Gone; } catch { }
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
            var voiceLangSpinner = FindViewById<Spinner>(Resource.Id.voiceLangSpinner);
            // speak petname switch (optional)
            TextView? npubError = null;
            Switch? speakPetSwitch = null;
            try
            {
                var npubErrId = Resources.GetIdentifier("npubErrorText", "id", PackageName);
                if (npubErrId != 0) npubError = FindViewById<TextView>(npubErrId);
            }
            catch { }
            try
            {
                var speakId = Resources.GetIdentifier("speakPetnameSwitch", "id", PackageName);
                if (speakId != 0) speakPetSwitch = FindViewById<Switch>(speakId);
            }
            catch { }
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
            try
            {
                var npubErrId = Resources.GetIdentifier("npubErrorText", "id", PackageName);
                if (npubErrId != 0) npubError = FindViewById<TextView>(npubErrId);
            }
            catch { }

            // Ensure required views are present to satisfy nullability and avoid runtime NREs
            if (relayEdit == null || npubEdit == null || allowOthersSwitch == null ||
                voiceFollowedSpinner == null || voiceOtherSpinner == null || refreshVoicesBtn == null ||
                startBtn == null || stopBtn == null || _lastContentView == null || followStatusText == null ||
                muteStatusText == null ||
                truncateEdit == null || voiceLangSpinner == null)
            {
                // Critical layout elements missing; bail out
                try { Toast.MakeText(this, "UI initialization failed", ToastLength.Short).Show(); } catch { }
                return;
            }

            // From this point locals are known to be non-null — create non-nullable aliases to inform the compiler
            var relay = (EditText)relayEdit!;
            var npub = (EditText)npubEdit!;
            // keep reference for updating from incoming intents
            _npubEdit_field = npub;
            var allowOthers = (Switch)allowOthersSwitch!;
            var voiceFollowed = (Spinner)voiceFollowedSpinner!;
            var voiceOther = (Spinner)voiceOtherSpinner!;
            var refreshVoices = (Button)refreshVoicesBtn!;
            var voiceLang = (Spinner)voiceLangSpinner!;
            var speechSeek = (SeekBar?)speechRateSeekBar;
            var speechVal = (TextView?)speechRateValue;
            var start = (Button)startBtn!;
            var stop = (Button)stopBtn!;
            var lastContent = (TextView)_lastContentView!;
            var followStatus = (TextView)followStatusText!;
            var muteStatus = (TextView)muteStatusText!;
            var truncate = (EditText)truncateEdit!;

            // helper to set Enabled + visual appearance
            void SetControlEnabled(View? v, bool enabled)
            {
                if (v == null) return;
                try
                {
                    v.Enabled = enabled;
                    // subtle visual cue when disabled
                    v.Alpha = enabled ? 1.0f : 0.45f;

                    if (v is EditText et)
                    {
                        // make non-focusable when disabled so keyboard won't appear
                        try { et.Focusable = enabled; } catch { }
                        try { et.FocusableInTouchMode = enabled; } catch { }
                        et.Clickable = enabled;
                        try { et.SetTextColor(enabled ? Android.Graphics.Color.Black : Android.Graphics.Color.DarkGray); } catch { }
                    }

                    if (v is Spinner sp)
                    {
                        try { sp.Alpha = enabled ? 1.0f : 0.45f; } catch { }
                    }

                    if (v is Button btn)
                    {
                        try { btn.Alpha = enabled ? 1.0f : 0.45f; } catch { }
                    }

                    if (v is Switch sw)
                    {
                        try { sw.Alpha = enabled ? 1.0f : 0.45f; } catch { }
                    }
                }
                catch { }
            }

            // Helper to validate npub input format
            bool IsNpubValid(string? input)
            {
                if (string.IsNullOrWhiteSpace(input)) return false;
                var s = input.Trim();

                // Accept only exact npub1... token or exact 64-hex string
                try
                {
                    var m = CreateNpubPlainRegex().Match(s);
                    if (m.Success && m.Index == 0 && m.Length == s.Length)
                    {
                        // Typical npub bech32 length for 32-byte payload + 6 checksum: hrp(4) + '1' + 58 = 63
                        if (s.Length == 63) return true;
                        return false;
                    }
                }
                catch { }

                try { if (CreateHex64Regex().IsMatch(s)) return true; } catch { }

                return false;
            }

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
                // restore speak petname switch
                try { speakPetSwitch?.Checked = prefs.GetBoolean(PREF_SPEAK_PETNAME, true); } catch { }
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

            // Debug: show saved language preference at startup
            try
            {
                var prefsDbg = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var savedLangDbg = prefsDbg?.GetString(PREF_VOICE_LANG, null) ?? "(none)";
                Android.Util.Log.Info("nokandro", $"OnCreate: saved PREF_VOICE_LANG={savedLangDbg}");
                try { Toast.MakeText(this, $"Saved lang: {savedLangDbg}", ToastLength.Short).Show(); } catch { }
                try { AppendLog($"OnCreate: saved PREF_VOICE_LANG={savedLangDbg}"); } catch { }
            }
            catch { }

            // Update start button enabled state based on npub validity (and service running state)
            try
            {
                var npubText = npub.Text ?? string.Empty;
                var canStart = !NostrService.IsRunning && IsNpubValid(npubText);
                SetControlEnabled(start, canStart);
            }
            catch { }

            // React to changes in npub field and enable/disable Start accordingly
            npub.TextChanged += (s, e) =>
            {
                try
                {
                    var txt = npub.Text ?? string.Empty;
                    var canStartNow = !NostrService.IsRunning && IsNpubValid(txt);
                    SetControlEnabled(start, canStartNow);
                    // show inline validation when user types invalid non-empty value
                    try
                    {
                        if (!string.IsNullOrEmpty(txt) && !IsNpubValid(txt))
                        {
                            if (npubError != null) { npubError.Text = "Invalid npub format"; npubError.Visibility = ViewStates.Visible; }
                        }
                        else
                        {
                            npubError?.Visibility = ViewStates.Gone;
                        }
                    }
                    catch { }
                }
                catch { }
            };

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

            // Populate voices initially (async) with language spinner
            _ = PopulateVoicesAsync(voiceLang, voiceFollowed, voiceOther, true);

            refreshVoices.Click += async (s, e) => await PopulateVoicesAsync(voiceLang, voiceFollowed, voiceOther, true);

            // When language selection changes, repopulate voices filtered by language (do not rebuild language list)
            voiceLang.ItemSelected += (s, e) =>
            {
                try
                {
                    if (_langPopulating) return;
                    // Determine selected code if available (use _availableLangCodes which stores primary codes)
                    var pos = voiceLang.SelectedItemPosition;
                    string? selCode = null;
                    try
                    {
                        // Prefer mapping stored in Spinner.Tag (set when adapter was created)
                        var tagStr = voiceLang.Tag?.ToString();
                        if (!string.IsNullOrEmpty(tagStr))
                        {
                            var parts = tagStr.Split('\u001F');
                            if (pos >= 0 && pos < parts.Length) selCode = parts[pos];
                        }
                        if (string.IsNullOrEmpty(selCode) && pos >= 0 && pos < _availableLangCodes.Count) selCode = _availableLangCodes[pos];
                    }
                    catch { }
                    // fallback: if codes not available, try to map label back to code by searching languageLabels via PopulateVoices path (best-effort)
                    if (string.IsNullOrEmpty(selCode)) selCode = voiceLang.SelectedItem?.ToString();
                    if (string.IsNullOrEmpty(selCode)) return;
                    if (selCode == _lastSelectedLangCode) return;
                    _lastSelectedLangCode = selCode;

                    // Persist language selection immediately
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null) { edit.PutString(PREF_VOICE_LANG, _lastSelectedLangCode); edit.Apply(); }
                    }
                    catch { }

                    // Rebuild language mappings and refresh voice lists so filtering uses up-to-date language->voice mapping.
                    // Persist selection already done above; calling with refreshLanguages=true will restore selection from prefs.
                    _ = PopulateVoicesAsync(voiceLang, voiceFollowed, voiceOther, true);
                }
                catch { }
            };

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

            // set initial start/stop button state from service flag and npub validity
            try
            {
                SetControlEnabled(start, !NostrService.IsRunning && IsNpubValid(npub.Text));
                SetControlEnabled(stop, NostrService.IsRunning);
                // disable all settings if service already running
                SetControlEnabled(relay, !NostrService.IsRunning);
                SetControlEnabled(npub, !NostrService.IsRunning);
                SetControlEnabled(truncate, !NostrService.IsRunning);
                SetControlEnabled(amberBtn, !NostrService.IsRunning);
                SetControlEnabled(allowOthers, !NostrService.IsRunning);
                SetControlEnabled(speakPetSwitch, !NostrService.IsRunning);
                SetControlEnabled(voiceFollowed, !NostrService.IsRunning);
                SetControlEnabled(voiceOther, !NostrService.IsRunning);
                SetControlEnabled(refreshVoices, !NostrService.IsRunning);
                SetControlEnabled(voiceLang, !NostrService.IsRunning);
            }
            catch { }

            start.Click += (s, e) =>
            {
                // validate npub before attempting to start
                try
                {
                    if (!IsNpubValid(npub.Text))
                    {
                        try { Toast.MakeText(this, "Invalid npub format", ToastLength.Short).Show(); } catch { }
                        if (npubError != null) { npubError.Text = "Invalid npub format"; npubError.Visibility = ViewStates.Visible; }
                        return;
                    }
                }
                catch { }

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
                // pass speakPetname preference to service so it consistently uses the UI setting
                try { intent.PutExtra("speakPetname", speakPetSwitch == null || speakPetSwitch.Checked); } catch { }

                // Reset UI status to not loaded; service will broadcast updates when lists are loaded
                try { followStatus.Text = "Follow list: not loaded"; } catch { }
                try { muteStatus.Text = "Public mute: not loaded"; } catch { }

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
                    // persist selected language
                    try { edit.PutString(PREF_VOICE_LANG, _lastSelectedLangCode ?? voiceLang.SelectedItem?.ToString() ?? "Any"); } catch { }
                    edit.Apply();
                }

                // Use StartService; the service will call StartForeground
                StartService(intent);
                // update UI state
                try { SetControlEnabled(start, false); SetControlEnabled(stop, true); } catch { }
                // disable all settings while running
                SetControlEnabled(relay, false);
                SetControlEnabled(npub, false);
                SetControlEnabled(truncate, false);
                SetControlEnabled(amberBtn, false);
                SetControlEnabled(allowOthers, false);
                SetControlEnabled(speakPetSwitch, false);
                SetControlEnabled(voiceFollowed, false);
                SetControlEnabled(voiceOther, false);
                SetControlEnabled(refreshVoices, false);
                SetControlEnabled(voiceLang, false);
            };

            stop.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                StopService(intent);
                try { SetControlEnabled(start, true); SetControlEnabled(stop, false); } catch { }
                // re-enable all settings
                SetControlEnabled(relay, true);
                SetControlEnabled(npub, true);
                SetControlEnabled(truncate, true);
                SetControlEnabled(amberBtn, true);
                SetControlEnabled(allowOthers, true);
                SetControlEnabled(speakPetSwitch, true);
                SetControlEnabled(voiceFollowed, true);
                SetControlEnabled(voiceOther, true);
                SetControlEnabled(refreshVoices, true);
                try { SetControlEnabled(voiceLang, true); } catch { }
                // speechSeek remains enabled

                // Immediately reset follow/mute UI to not loaded when stopping
                try { followStatus.Text = "Follow list: not loaded"; } catch { }
                try { muteStatus.Text = "Public mute: not loaded"; } catch { }
            };

            // register for service start/stop broadcasts to update button state
            try
            {
                _serviceStateReceiver = new BroadcastReceiver();
                _serviceStateReceiver.Receive += (ctx, intent) =>
                {
                    if (intent == null) return;
                    if (intent.Action == "nokandro.ACTION_SERVICE_STARTED")
                    {
                        RunOnUiThread(() =>
                        {
                            try { SetControlEnabled(start, false); SetControlEnabled(stop, true); } catch { }
                            try { SetControlEnabled(relay, false); } catch { }
                            try { SetControlEnabled(npub, false); } catch { }
                            try { SetControlEnabled(truncate, false); } catch { }
                            try { SetControlEnabled(amberBtn, false); } catch { }
                            try { SetControlEnabled(allowOthers, false); } catch { }
                            try { SetControlEnabled(speakPetSwitch, false); } catch { }
                            try { SetControlEnabled(voiceFollowed, false); } catch { }
                            try { SetControlEnabled(voiceOther, false); } catch { }
                            try { SetControlEnabled(refreshVoices, false); } catch { }
                            // do not disable speechSeek; speech rate applies immediately
                        });
                    }
                    else if (intent.Action == "nokandro.ACTION_SERVICE_STOPPED")
                    {
                        RunOnUiThread(() =>
                        {
                            try { SetControlEnabled(start, !NostrService.IsRunning && IsNpubValid(npub.Text)); SetControlEnabled(stop, false); } catch { }
                            try { SetControlEnabled(relay, true); } catch { }
                            try { SetControlEnabled(npub, true); } catch { }
                            try { SetControlEnabled(truncate, true); } catch { }
                            try { SetControlEnabled(amberBtn, true); } catch { }
                            try { SetControlEnabled(allowOthers, true); } catch { }
                            try { SetControlEnabled(speakPetSwitch, true); } catch { }
                            try { SetControlEnabled(voiceFollowed, true); } catch { }
                            try { SetControlEnabled(voiceOther, true); } catch { }
                            try { SetControlEnabled(refreshVoices, true); } catch { }
                            // speechSeek remains enabled

                            // Ensure follow/mute UI reflect unloaded state when service has stopped
                            try { followStatus.Text = "Follow list: not loaded"; } catch { }
                            try { muteStatus.Text = "Public mute: not loaded"; } catch { }
                        });
                    }
                };
                var svcFilter = new IntentFilter();
                svcFilter.AddAction("nokandro.ACTION_SERVICE_STARTED");
                svcFilter.AddAction("nokandro.ACTION_SERVICE_STOPPED");
                LocalBroadcast.RegisterReceiver(_serviceStateReceiver, svcFilter);
            }
            catch { }

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
                    var text = loaded ? $"Follow list: loaded ({count})" : "Follow list: not loaded";
                    RunOnUiThread(() => { followStatus.Text = text; });
                    return;
                }

                if (intent.Action == "nokandro.ACTION_MUTE_UPDATE")
                {
                    var loaded = intent.GetBooleanExtra("muteLoaded", false);
                    var count = intent.GetIntExtra("muteCount", 0);
                    var text = loaded ? $"Public mute: loaded ({count})" : "Public mute: not loaded";
                    RunOnUiThread(() => { muteStatus.Text = text; });
                    return;
                }

                var text2 = intent.GetStringExtra("content") ?? string.Empty;
                var isFollowed = intent.GetBooleanExtra("isFollowed", false);
                var pet = intent.GetStringExtra("petname");
                var displayText = ShortenUrls(text2);
                var prefix = isFollowed ? "* " : "- ";
                RunOnUiThread(() =>
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(pet)) lastContent.Text = prefix + pet + " " + displayText;
                        else lastContent.Text = prefix + displayText;
                    }
                    catch { }
                });
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
                        try { _npubEdit_field?.Text = npubVal; } catch { }
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
            if (_serviceStateReceiver != null)
            {
                try { LocalBroadcast.UnregisterReceiver(_serviceStateReceiver); } catch { }
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
                    try { _npubEdit_field?.Text = npubVal; } catch { }
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
            // Replace URLs with context-aware placeholders: image -> [picture], video -> [movie], else -> [URL]
            var replaced = UrlPattern.Replace(input, new MatchEvaluator(match =>
            {
                var url = match.Value ?? string.Empty;
                var checkUrl = url;
                if (checkUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) checkUrl = "http://" + checkUrl;

                try
                {
                    if (Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri))
                    {
                        var ext = Path.GetExtension(uri.LocalPath ?? string.Empty);
                        if (!string.IsNullOrEmpty(ext))
                        {
                            ext = ext.ToLowerInvariant();
                            var imageExts = evaluator;
                            var videoExts = evaluatorArray;
                            if (Array.IndexOf(imageExts, ext) >= 0) return "[picture]";
                            if (Array.IndexOf(videoExts, ext) >= 0) return "[movie]";
                        }
                    }
                }
                catch { }

                // Fallback: check common extensions inside the URL string
                try
                {
                    if (CreateImageExtensionRegex().IsMatch(url)) return "[picture]";
                    if (CreateVideoExtensionRegex().IsMatch(url)) return "[movie]";
                }
                catch { }

                return "[URL]";
            }));

            // replace nostr npub/event/note references
            try
            {
                replaced = NpubNprofilePattern.Replace(replaced, "[mention]");
                replaced = NeventNotePattern.Replace(replaced, "[quote]");
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
                return string.Concat(replaced.AsSpan(0, len), " ...");
            }
            return replaced;
        }

        private async Task PopulateVoicesAsync(Spinner langSpinner, Spinner followedSpinner, Spinner otherSpinner, bool refreshLanguages = true)
        {
            _langPopulating = true;
            // Debug: show entering populate
            try
            {
                var prefsDbg = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var saved = prefsDbg?.GetString(PREF_VOICE_LANG, null) ?? "(none)";
                Android.Util.Log.Info("nokandro", $"PopulateVoicesAsync start: refreshLanguages={refreshLanguages} savedPref={saved} lastSelected={_lastSelectedLangCode}");
                try { AppendLog($"Populate start: refreshLanguages={refreshLanguages} savedPref={saved} lastSelected={_lastSelectedLangCode}"); } catch { }
            }
            catch { }
            List<string> voices = new List<string>() { "default" };
            // parallel lists: codes (e.g., "ja-JP","ja") and labels (e.g., "Japanese (Japan)")
            var languageCodes = new List<string>() { "Any" };
            var languageLabels = new List<string>() { "Any" };
            TextToSpeech? tts = null;
            List<Android.Speech.Tts.Voice>? initialVoiceObjs = null;

            try
            {
                tts = new TextToSpeech(this, null);

                // Wait until voices are available (polling), timeout after 8 seconds
                var timeout = 8000;
                var waited = 0;
                while ((tts == null || tts.Voices == null || tts.Voices.Count == 0) && waited < timeout)
                {
                    await Task.Delay(200);
                    waited += 200;
                }

                // Build a non-nullable list of voice names and languages
                initialVoiceObjs = tts?.Voices?.Where(v => v != null).ToList();
                if ((initialVoiceObjs == null || initialVoiceObjs.Count == 0))
                {
                    try
                    {
                        // Fallback: try a fresh TextToSpeech instance in case the first wasn't ready
                        try { AppendLog("Initial TTS voices empty — trying fallback TTS instance..."); } catch { }
                        var ttsFallback = new TextToSpeech(this, null);
                        try { initialVoiceObjs = ttsFallback?.Voices?.Where(v => v != null).ToList(); } catch { }
                        try { AppendLog($"Fallback voices found: {(initialVoiceObjs?.Count ?? 0)}"); } catch { }
                        try { ttsFallback?.Shutdown(); } catch { }
                    }
                    catch { }
                }
                if (initialVoiceObjs == null || initialVoiceObjs.Count == 0)
                {
                    try { AppendLog("No TTS voices available after polling."); } catch { }
                }
                if (initialVoiceObjs != null && initialVoiceObjs.Count > 0)
                {
                    var nameList = new List<string>();
                    // Collect primary language codes (e.g., "ja", "en") to collapse regional variants
                    var primarySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var v in initialVoiceObjs)
                    {
                        try
                        {
                            var name = v?.Name ?? string.Empty;
                            if (!string.IsNullOrEmpty(name) && !nameList.Contains(name)) nameList.Add(name);

                            try
                            {
                                var loc = v.Locale?.ToString() ?? string.Empty; // may be like "ja_JP" or "ja"
                                if (!string.IsNullOrEmpty(loc))
                                {
                                    var fmt = loc.Replace('_', '-');
                                    var primary = fmt.Split('-')[0];
                                    if (!string.IsNullOrEmpty(primary)) primarySet.Add(primary);
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }

                    if (nameList.Count > 0) voices = nameList;
                    if (primarySet.Count > 0)
                    {
                        // build languageCodes/labels from primary language codes (sorted)
                        var sortedPrimary = primarySet.OrderBy(s => s).ToList();
                        foreach (var code in sortedPrimary)
                        {
                            languageCodes.Add(code);
                            var label = code;
                            try
                            {
                                try { var culture = new System.Globalization.CultureInfo(code); label = culture.EnglishName; }
                                catch { label = code; }
                            }
                            catch { label = code; }
                            languageLabels.Add(label);
                        }
                        try
                        {
                            Android.Util.Log.Info("nokandro", $"Found languages: codes={string.Join(',', languageCodes)} labels={string.Join(',', languageLabels)}");
                            try { AppendLog($"Found languages: codes={string.Join(',', languageCodes)} labels={string.Join(',', languageLabels)}"); } catch { }
                        }
                        catch { }
                    }
                }

                if (voices.Count == 0) voices = new List<string>() { "default" };
            }
            catch
            {
                voices = new List<string>() { "default" };
                languageCodes = new List<string>() { "Any" };
                languageLabels = new List<string>() { "Any" };
            }
            finally
            {
                try { tts?.Shutdown(); } catch { }
            }

            // If requested, update language spinner adapter and available codes
            if (refreshLanguages)
            {
                // remember available codes for selection handler
                _availableLangCodes = languageCodes;

                // capture previous requested code (prefer last saved or current selection)
                string? prevCode = _lastSelectedLangCode;
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    if (string.IsNullOrEmpty(prevCode)) prevCode = prefs?.GetString(PREF_VOICE_LANG, null);
                }
                catch { }
                try { if (string.IsNullOrEmpty(prevCode)) prevCode = langSpinner.SelectedItem?.ToString(); } catch { }

                // set language spinner adapter using labels and restore selection on UI thread
                var langAdapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, languageLabels);
                langAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
                // restore previous selection by matching code; fall back to 0
                int sel = 0;
                try
                {
                    if (!string.IsNullOrEmpty(prevCode))
                    {
                        var idx = languageCodes.IndexOf(prevCode);
                        if (idx >= 0) sel = idx;
                        else
                        {
                            var lblIdx = languageLabels.IndexOf(prevCode);
                            if (lblIdx >= 0) sel = lblIdx;
                        }
                    }
                }
                catch { sel = 0; }

                RunOnUiThread(() =>
                {
                    try { langSpinner.Adapter = langAdapter; } catch { }
                    try { if (sel >= 0 && sel < languageLabels.Count) langSpinner.SetSelection(sel); } catch { }
                    try { _lastSelectedLangCode = _availableLangCodes.ElementAtOrDefault(langSpinner.SelectedItemPosition) ?? (_availableLangCodes.Count > 0 ? _availableLangCodes[0] : "Any"); } catch { _lastSelectedLangCode = null; }
                    try { langSpinner.Tag = string.Join("\u001F", _availableLangCodes); } catch { }
                });
            }
            else
            {
                // Keep existing _availableLangCodes and _lastSelectedLangCode; ensure selectedLang reflects current selection
                try { if (_availableLangCodes == null || _availableLangCodes.Count == 0) _availableLangCodes = new List<string>() { "Any" }; } catch { }
            }

            // Now filter voices by selected language code
            var selectedLang = _lastSelectedLangCode ?? "Any";
            try { Android.Util.Log.Info("nokandro", $"Filtering using selectedLang={selectedLang}"); } catch { }
            try { AppendLog($"Filtering using selectedLang={selectedLang}"); } catch { }
            List<string> finalVoices = new List<string>();
            if (selectedLang == "Any") finalVoices.AddRange(voices);
            else
            {
                // Normalize selected primary language (e.g., "ja-JP" -> "ja")
                var selectedPrimary = selectedLang.Split('-')[0].ToLowerInvariant();
                // prepare culture names for matching (English and native)
                string selectedEnglishName = string.Empty;
                string selectedNativeName = string.Empty;
                try
                {
                    var ci = new System.Globalization.CultureInfo(selectedPrimary);
                    selectedEnglishName = (ci.EnglishName ?? string.Empty).ToLowerInvariant();
                    selectedNativeName = (ci.NativeName ?? string.Empty).ToLowerInvariant();
                }
                catch { }

                // Re-query voices to get Locale info for filtering
                var set = new HashSet<string>();
                // Prefer the initial voice list we collected earlier (it succeeded to build language list)
                var voiceListToInspect = initialVoiceObjs;
                // If not available, try to use the original tts instance if still present
                if ((voiceListToInspect == null || voiceListToInspect.Count == 0) && tts != null)
                {
                    try { voiceListToInspect = tts.Voices?.Where(v => v != null).ToList(); } catch { }
                }
                // Final fallback: try creating a fresh TTS and polling briefly
                if (voiceListToInspect == null || voiceListToInspect.Count == 0)
                {
                    try
                    {
                        try { AppendLog("No initial voice list available — creating fallback TTS and polling..."); } catch { }
                        var ttsFb = new TextToSpeech(this, null);
                        var waitedFb = 0;
                        var timeoutFb = 4000;
                        while ((ttsFb == null || ttsFb.Voices == null || ttsFb.Voices.Count == 0) && waitedFb < timeoutFb)
                        {
                            await Task.Delay(200);
                            waitedFb += 200;
                        }
                        try { voiceListToInspect = ttsFb?.Voices?.Where(v => v != null).ToList(); } catch { }
                        try { ttsFb?.Shutdown(); } catch { }
                    }
                    catch { }
                }

                if (voiceListToInspect == null || voiceListToInspect.Count == 0)
                {
                    try { AppendLog("No voice list available for inspection"); } catch { }
                }
                else
                {
                    foreach (var v in voiceListToInspect)
                    {
                        try
                        {
                            var name = v?.Name ?? string.Empty;
                            var nameLower = (name ?? string.Empty).ToLowerInvariant();
                            var loc = v.Locale?.ToString() ?? string.Empty;
                            var fmt = loc.Replace('_', '-');
                            var voicePrimary = (fmt.Split('-')[0] ?? string.Empty).ToLowerInvariant();
                            try { AppendLog($"voice: name='{name}' locale='{fmt}'"); } catch { }
                            if (!string.IsNullOrEmpty(name))
                            {
                                try
                                {
                                    if (!string.IsNullOrEmpty(voicePrimary) && string.Equals(voicePrimary, selectedPrimary, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!set.Contains(name)) set.Add(name);
                                    }
                                    else if (!string.IsNullOrEmpty(fmt) && fmt.StartsWith(selectedPrimary, StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (!set.Contains(name)) set.Add(name);
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    if (set.Count > 0)
                    {
                        finalVoices.AddRange(set);
                        try { AppendLog($"Matching voice names: {string.Join(',', set)}"); } catch { }
                    }
                }

                try { Android.Util.Log.Info("nokandro", $"Filtered voices count={finalVoices.Count}"); } catch { }
                try { AppendLog($"Filtered voices count={finalVoices.Count}"); } catch { }
            }
            // Sort voice names for predictable UI order
            try
            {
                finalVoices = finalVoices.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch
            {
                try { finalVoices.Sort(StringComparer.OrdinalIgnoreCase); } catch { }
            }

            var adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, finalVoices);
            adapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            // Set adapters and selections on UI thread
            RunOnUiThread(() =>
            {
                try { followedSpinner.Adapter = adapter; } catch { }
                try { otherSpinner.Adapter = adapter; } catch { }
                try { Android.Util.Log.Info("nokandro", $"Adapters set: finalVoicesCount={finalVoices.Count}"); } catch { }
                try { AppendLog($"Adapters set: finalVoicesCount={finalVoices.Count}"); } catch { }
                // restore saved selections if present
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var savedFollowed = prefs?.GetString(PREF_VOICE_FOLLOWED, null);
                    var savedOther = prefs?.GetString(PREF_VOICE_OTHER, null);
                    if (!string.IsNullOrEmpty(savedFollowed))
                    {
                        var idx = finalVoices.IndexOf(savedFollowed);
                        if (idx >= 0) followedSpinner.SetSelection(idx);
                    }
                    if (!string.IsNullOrEmpty(savedOther))
                    {
                        var idx = finalVoices.IndexOf(savedOther);
                        if (idx >= 0) otherSpinner.SetSelection(idx);
                    }
                }
                catch { }
            });
            // Debug: if no voices and not Any, show toast
            try
            {
                if (selectedLang != "Any" && finalVoices.Count == 0)
                {
                    RunOnUiThread(() => { try { Toast.MakeText(this, "No voices for selected language", ToastLength.Short).Show(); } catch { } });
                    try { AppendLog("No voices for selected language"); } catch { }
                }
            }
            catch { }
            _langPopulating = false;
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

        private void AppendLog(string message)
        {
            try
            {
                var ts = DateTime.Now.ToString("HH:mm:ss");
                var line = $"[{ts}] {message}\n";
                lock (_logLock)
                {
                    _logBuffer.Add(line);
                    if (!_logFlushScheduled)
                    {
                        _logFlushScheduled = true;
                        try { _mainHandler?.PostDelayed(new Java.Lang.Runnable(() => FlushLog()), LOG_FLUSH_MS); } catch { /* fallback */ }
                    }
                }
            }
            catch { }
        }

        private void FlushLog()
        {
            string payload;
            lock (_logLock)
            {
                if (_logBuffer.Count == 0) { _logFlushScheduled = false; return; }
                payload = string.Concat(_logBuffer);
                _logBuffer.Clear();
                _logFlushScheduled = false;
            }

            try
            {
                // Update UI once with accumulated payload
                RunOnUiThread(() =>
                {
                    try
                    {
                        if (_logTextView != null)
                        {
                            var prev = _logTextView.Text ?? string.Empty;
                            _logTextView.Text = payload + prev;
                        }
                    }
                    catch { }
                });
            }
            catch { }
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

        private static readonly Regex UrlPattern = CreateUrlRegex();
        private static readonly Regex NpubNprofilePattern = CreateNpubNprofileRegex();
        private static readonly Regex NeventNotePattern = CreateNeventNoteRegex();
        private static readonly string[] evaluator = new string[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".heic", ".tiff", ".ico", ".apng" };
        private static readonly string[] evaluatorArray = new string[] { ".mp4", ".mov", ".webm", ".mkv", ".avi", ".flv", ".mpeg", ".mpg", ".3gp", ".ogg", ".ogv", ".m4v", ".ts", ".m2ts", ".wmv" };

        [GeneratedRegex("(https?://\\S+|www\\.\\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateUrlRegex();
        [GeneratedRegex("\\.(jpg|jpeg|png|gif|webp|bmp|svg|heic|tiff|ico|apng)(?:[?#]|$)", RegexOptions.IgnoreCase, "ja-JP")]
        private static partial Regex CreateImageExtensionRegex();
        [GeneratedRegex("\\.(mp4|mov|webm|mkv|avi|flv|mpeg|mpg|3gp|ogg|ogv|m4v|ts|m2ts|wmv)(?:[?#]|$)", RegexOptions.IgnoreCase, "ja-JP")]
        private static partial Regex CreateVideoExtensionRegex();
        [GeneratedRegex("\\bnostr:(?:npub1|nprofile1)\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateNpubNprofileRegex();
        [GeneratedRegex("\\bnostr:(?:nevent1|note1)\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateNeventNoteRegex();
        [GeneratedRegex("npub1[0-9a-zA-Z]+", RegexOptions.IgnoreCase, "ja-JP")]
        private static partial Regex CreateNpubPlainRegex();
        [GeneratedRegex("^[0-9a-fA-F]{64}$")]
        private static partial Regex CreateHex64Regex();
    }
}