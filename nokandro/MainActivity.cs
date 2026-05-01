using Android.Content;
using Android.OS;
using Android.Speech.Tts;
using Android.Views;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace nokandro
{
    [Activity(Label = "@string/app_name", MainLauncher = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop)]
    public partial class MainActivity : Activity
    {
        const string PREFS_NAME = "nokandro_prefs";
        const string PREF_VOICE_FOLLOWED = "pref_voice_followed";
        const string PREF_VOICE_OTHER = "pref_voice_other";
        const string PREF_VOICE_LANG = "pref_voice_lang";
        const string PREF_RELAY = "pref_relay";
        const string PREF_BUNKER_RELAY = "pref_bunker_relay";
        const string PREF_BUNKER_SECRET = "pref_bunker_secret";
        const string PREF_BUNKER_ENABLED = "pref_bunker_enabled";
        const string PREF_BUNKER_AUTHORIZED = "pref_bunker_authorized";
        const string PREF_NPUB = "pref_npub";
        const string PREF_NSEC = "pref_nsec";
        const string PREF_TRUNCATE_LEN = "pref_truncate_len";
        const string PREF_TRUNCATE_ELLIPSIS = "pref_truncate_ellipsis";
        const string PREF_ALLOW_OTHERS = "pref_allow_others";
        const string PREF_SPEAK_PETNAME = "pref_speak_petname";
        const string PREF_MUSIC_STATUS = "pref_music_status";
        const string PREF_ENABLE_TTS = "pref_enable_tts";
        const string PREF_AUTO_STOP = "pref_auto_stop";
        const string PREF_SKIP_CW = "pref_skip_cw";
        const string PREF_OFF_TIMER_ENABLED = "pref_off_timer_enabled";
        const string PREF_OFF_TIMER_MINUTES = "pref_off_timer_minutes";
        const string PREF_OFF_TIMER_END_TICKS = "pref_off_timer_end_ticks";
        // maximum length for displayed content before truncation
        private const int CONTENT_TRUNCATE_LENGTH = 50;

        private const string ACTION_LAST_CONTENT = "nokandro.ACTION_LAST_CONTENT";

        private TextView? _lastContentView;
        private TextView? _musicDebugText;
        private TextView? _offTimerCountdownView;
        private System.Threading.Timer? _countdownTimer;
        private BroadcastReceiver? _receiver;
        private BroadcastReceiver? _serviceStateReceiver;
        // keep references to EditText fields used elsewhere
        private EditText? _npubEdit_field;
        private EditText? _truncateEllipsisField;

        // Language spinner state to avoid resetting while user interacts
        private bool _langPopulating = false;
        private List<string> _availableLangCodes = [];
        private string? _lastSelectedLangCode = null;
        private string _lastMusicInfo = "Music: (waiting)"; // State holder for music info

        // NIP-46 Bunker
        private TextView? _bunkerStatusText;
        private BroadcastReceiver? _bunkerReceiver;

        // nostrconnect://
        private const string PREF_NC_CLIENTS = "pref_nc_clients";
        private const string PREF_NC_NAMES = "pref_nc_names";
        private TextView? _ncStatusText;
        private LinearLayout? _ncClientList;
        private BroadcastReceiver? _ncReceiver;
        private string[] _lastNcPubkeys = [];
        private string[] _lastNcUris = [];

        private void setControlEnabled(View? v, bool enabled)
        {
            if (v == null) return;
            try
            {
                v.Enabled = enabled;
                v.Alpha = enabled ? 1.0f : 0.45f;
                if (v is EditText et)
                {
                    try { et.Focusable = enabled; } catch { }
                    try { et.FocusableInTouchMode = enabled; } catch { }
                    et.Clickable = enabled;
                    try { et.SetTextColor(enabled ? Android.Graphics.Color.Black : Android.Graphics.Color.DarkGray); } catch { }
                }
            }
            catch { }
        }

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
                if (verTv != null)
                {
                    verTv.Clickable = true;
                    // Add underline to indicate link
                    verTv.PaintFlags |= Android.Graphics.PaintFlags.UnderlineText;
                    verTv.Click += (s, e) =>
                    {
                        try
                        {
                            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse("https://github.com/betonetojp/nokandro/releases/latest"));
                            StartActivity(intent);
                        }
                        catch { }
                    };
                    _ = CheckForUpdateAsync(verTv, versionName ?? "v0.0.0");
                }

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

            // --- Tab switching ---
            var tabMainBtn = FindViewById<Button>(Resource.Id.tabMainBtn);
            var tabBunkerBtn = FindViewById<Button>(Resource.Id.tabBunkerBtn);
            var tabMainContent = FindViewById<View>(Resource.Id.tabMainContent);
            var tabBunkerContent = FindViewById<View>(Resource.Id.tabBunkerContent);

            void SelectTab(bool mainSelected)
            {
                if (tabMainContent != null) tabMainContent.Visibility = mainSelected ? ViewStates.Visible : ViewStates.Gone;
                if (tabBunkerContent != null) tabBunkerContent.Visibility = mainSelected ? ViewStates.Gone : ViewStates.Visible;
                if (tabMainBtn != null)
                {
                    tabMainBtn.Alpha = mainSelected ? 1.0f : 0.5f;
                    tabMainBtn.PaintFlags = mainSelected
                        ? tabMainBtn.PaintFlags | Android.Graphics.PaintFlags.UnderlineText
                        : tabMainBtn.PaintFlags & ~Android.Graphics.PaintFlags.UnderlineText;
                }
                if (tabBunkerBtn != null)
                {
                    tabBunkerBtn.Alpha = mainSelected ? 0.5f : 1.0f;
                    tabBunkerBtn.PaintFlags = mainSelected
                        ? tabBunkerBtn.PaintFlags & ~Android.Graphics.PaintFlags.UnderlineText
                        : tabBunkerBtn.PaintFlags | Android.Graphics.PaintFlags.UnderlineText;
                }
            }

            // Default: Main tab selected
            SelectTab(true);

            // If launched from Bunker notification, switch to Bunker tab
            try
            {
                if (Intent?.GetStringExtra("openTab") == "bunker")
                    SelectTab(false);
            }
            catch { }

            if (tabMainBtn != null) tabMainBtn.Click += (s, e) => SelectTab(true);
            if (tabBunkerBtn != null) tabBunkerBtn.Click += (s, e) => SelectTab(false);

            // Find views
            var relayEdit = FindViewById<EditText>(Resource.Id.relayEdit);
            var npubEdit = FindViewById<EditText>(Resource.Id.npubEdit);
            var nsecEdit = FindViewById<EditText>(Resource.Id.nsecEdit);
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
            var fetchRelaysBtn = FindViewById<Button>(Resource.Id.fetchRelaysBtn);
            
            // New views
            var ttsSwitch = FindViewById<Switch>(Resource.Id.ttsSwitch);
            var ttsSettingsContainer = FindViewById<LinearLayout>(Resource.Id.ttsSettingsContainer); // Added container
            var musicSwitch = FindViewById<Switch>(Resource.Id.musicStatusSwitch);
            var autoStopSwitch = FindViewById<Switch>(Resource.Id.autoStopSwitch);
            var skipContentWarningSwitch = FindViewById<Switch>(Resource.Id.skipContentWarningSwitch);
            var testPostBtn = FindViewById<Button>(Resource.Id.testPostBtn);
            var grantBtn = FindViewById<Button>(Resource.Id.grantListenerBtn);
            var offTimerSwitch = FindViewById<Switch>(Resource.Id.offTimerSwitch);
            var offTimerMinutesEdit = FindViewById<EditText>(Resource.Id.offTimerMinutesEdit);
            
            _lastContentView = FindViewById<TextView>(Resource.Id.lastContentText);
            _musicDebugText = FindViewById<TextView>(Resource.Id.musicStatusDebugText);
            _offTimerCountdownView = FindViewById<TextView>(Resource.Id.offTimerCountdownText);
            var followStatusText = FindViewById<TextView>(Resource.Id.followStatusText);
            var muteStatusText = FindViewById<TextView>(Resource.Id.muteStatusText);
            var mutedWordsStatusText = FindViewById<TextView>(Resource.Id.mutedWordsStatusText);
            var truncateEdit = FindViewById<EditText>(Resource.Id.truncateEdit);
            // optional ellipsis field (may be absent in some layouts)
            var truncateEllipsis = FindViewById<EditText>(Resource.Id.truncateEllipsisEdit);
            _truncateEllipsisField = truncateEllipsis;
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
            var nsec = (EditText?)nsecEdit;
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
            // If ellipsis EditText exists, populate from prefs and wire saving on focus loss
            try
            {
                if (truncateEllipsis != null)
                {
                    try
                    {
                        var prefss = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var ell = prefss?.GetString(PREF_TRUNCATE_ELLIPSIS, " ...") ?? " ...";
                        truncateEllipsis.Text = ell;
                    }
                    catch { }
                    truncateEllipsis.FocusChange += (s, e) =>
                    {
                        if (!e.HasFocus)
                        {
                            try
                            {
                                var val = truncateEllipsis.Text ?? string.Empty;
                                var prefs2 = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                                var edit2 = prefs2?.Edit();
                                if (edit2 != null) { edit2.PutString(PREF_TRUNCATE_ELLIPSIS, val); edit2.Apply(); }
                            }
                            catch { }
                        }
                    };
                }
            }
            catch { }

            // helper to set Enabled + visual appearance
            void SetControlEnabled(View? v, bool enabled)
            {
                if (v == null) return;
                try
                {
                    v.Enabled = enabled;
                    v.Alpha = enabled ? 1.0f : 0.45f;
                    if (v is EditText et)
                    {
                        try { et.Focusable = enabled; } catch { }
                        try { et.FocusableInTouchMode = enabled; } catch { }
                        et.Clickable = enabled;
                        try { et.SetTextColor(enabled ? Android.Graphics.Color.Black : Android.Graphics.Color.DarkGray); } catch { }
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

            bool IsNsecValid()
            {
                if (nsec == null) return false;
                var t = nsec.Text?.Trim() ?? "";
                if (!t.StartsWith("nsec1")) return false;
                try
                {
                    var (hrp, data) = Bech32Decode(t);
                    return hrp == "nsec" && data != null;
                }
                catch { return false; }
            }

            // restore saved relay + npub + nsec
            string savedEllipsis = " ...";
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                relay.Text = prefs.GetString(PREF_RELAY, "wss://relay-jp.nostr.wirednet.jp/");
                npub.Text = prefs.GetString(PREF_NPUB, string.Empty);
                if (nsec != null) nsec.Text = prefs.GetString(PREF_NSEC, string.Empty);
                var savedLen = prefs.GetInt(PREF_TRUNCATE_LEN, CONTENT_TRUNCATE_LENGTH);
                truncate.Text = savedLen.ToString();
                
                allowOthers.Checked = prefs.GetBoolean(PREF_ALLOW_OTHERS, false);
                if (speakPetSwitch != null) speakPetSwitch.Checked = prefs.GetBoolean(PREF_SPEAK_PETNAME, false);
                if (musicSwitch != null) musicSwitch.Checked = prefs.GetBoolean(PREF_MUSIC_STATUS, false);
                if (autoStopSwitch != null) autoStopSwitch.Checked = prefs.GetBoolean(PREF_AUTO_STOP, true);
                if (_musicDebugText != null) _musicDebugText.Visibility = (musicSwitch != null && musicSwitch.Checked) ? ViewStates.Visible : ViewStates.Gone;
                if (ttsSwitch != null) ttsSwitch.Checked = prefs.GetBoolean(PREF_ENABLE_TTS, true);

                if (offTimerSwitch != null) offTimerSwitch.Checked = prefs.GetBoolean(PREF_OFF_TIMER_ENABLED, false);
                if (offTimerMinutesEdit != null)
                {
                    offTimerMinutesEdit.Text = prefs.GetInt(PREF_OFF_TIMER_MINUTES, 60).ToString();
                    offTimerMinutesEdit.Enabled = offTimerSwitch?.Checked ?? false;
                }
                
                // restore speech rate (0.50..1.50) -> progress (0..200)
                if (speechSeek != null)
                {
                   var savedRate = prefs.GetFloat("pref_speech_rate", 1.0f);
                   var p = (int)((savedRate - 0.5f) * 200f);
                   if (p < 0) p = 0; if (p > 200) p = 200;
                   speechSeek.Progress = p;
                   if (speechVal != null) speechVal.Text = string.Format("{0:0.00}x", savedRate);
                }

                // Initialize container visibility
                if (ttsSwitch != null)
                {
                    var isTtsEnabled = ttsSwitch.Checked;
                    if (ttsSettingsContainer != null) ttsSettingsContainer.Visibility = isTtsEnabled ? ViewStates.Visible : ViewStates.Gone;
                    if (autoStopSwitch != null) autoStopSwitch.Visibility = isTtsEnabled ? ViewStates.Visible : ViewStates.Gone;
                }
            }
            catch
            {
                try
                {
                    // Fallback: prompt for relay + npub to recover from broken state
                    relay.Text = "wss://relay-jp.nostr.wirednet.jp/";
                    npub.Text = string.Empty;
                    nsec.Text = string.Empty;
                    truncate.Text = CONTENT_TRUNCATE_LENGTH.ToString();
                }
                catch { }
            }
            // Debug: show saved language preference at startup
            try
            {
                var prefsDbg = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var savedLangDbg = prefsDbg?.GetString(PREF_VOICE_LANG, null) ?? "(none)";
                Android.Util.Log.Info("nokandro", $"OnCreate: saved PREF_VOICE_LANG={savedLangDbg}");
                try { Toast.MakeText(this, $"Saved lang: {savedLangDbg}", ToastLength.Short).Show(); } catch { }
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
            
            // Handle nsec changes
            if (nsec != null)
            {
                 nsec.TextChanged += (s, e) => 
                 {
                     try
                     {
                         // Update musicSwitch enabled state
                         SetControlEnabled(musicSwitch, !NostrService.IsRunning && IsNsecValid());

                         var t = nsec.Text?.Trim() ?? "";
                         if (t.StartsWith("nsec1"))
                         {
                             var (hrp, data) = Bech32Decode(t);
                             if (hrp == "nsec" && data != null)
                             {
                                 var priv = ConvertBits(data, 5, 8, false);
                                 if (priv != null && priv.Length == 32)
                                 {
                                     var pub = NostrCrypto.GetPublicKey(priv);
                                     var pub5 = ConvertBits(pub, 8, 5, true);
                                     var npubStr = Bech32Encode("npub", pub5);
                                     RunOnUiThread(() => npub.Text = npubStr);
                                     
                                     // Save nsec
                                     var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                                     var edit = prefs?.Edit();
                                     if(edit != null) {
                                         edit.PutString(PREF_NSEC, t);
                                         edit.Apply();
                                     }
                                 }
                             }
                         }
                     }
                     catch {}
                 };
            }

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

            // save speakPetname preference
            if (speakPetSwitch != null)
            {
                speakPetSwitch.CheckedChange += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            edit.PutBoolean(PREF_SPEAK_PETNAME, e.IsChecked);
                            edit.Apply();
                        }
                    }
                    catch { }
                };
            }
            
            // save TTS preference and toggle container visibility
            if (ttsSwitch != null)
            {
                ttsSwitch.CheckedChange += (s, e) =>
                {
                     try
                     {
                         var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                         var edit = prefs?.Edit();
                         if (edit != null)
                         {
                             edit.PutBoolean(PREF_ENABLE_TTS, e.IsChecked);
                             edit.Apply();
                         }
                         
                         if (ttsSettingsContainer != null)
                         {
                             ttsSettingsContainer.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
                         }
                         if (autoStopSwitch != null)
                         {
                             autoStopSwitch.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;
                         }
                     }
                     catch { }
                };
            }

            // save skipContentWarning preference
            if (skipContentWarningSwitch != null)
            {
                // load initial value
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    skipContentWarningSwitch.Checked = prefs?.GetBoolean(PREF_SKIP_CW, false) ?? false;
                }
                catch { }

                skipContentWarningSwitch.CheckedChange += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            edit.PutBoolean(PREF_SKIP_CW, e.IsChecked);
                            edit.Apply();
                        }
                    }
                    catch { }
                };
            }

            // music switch logic
            if (musicSwitch != null)
            {
                void CheckListenerPerm()
                {
                    try
                    {
                        if (musicSwitch.Checked)
                        {
                            var enabledListeners = Android.Provider.Settings.Secure.GetString(ContentResolver, "enabled_notification_listeners");
                            var hasPerm = !string.IsNullOrEmpty(enabledListeners) && enabledListeners.Contains(PackageName);
                            if (grantBtn != null) grantBtn.Visibility = hasPerm ? ViewStates.Gone : ViewStates.Visible;
                            // Show auth button if enabled
                            // if (testPostBtn != null) testPostBtn.Visibility = ViewStates.Visible;
                        }
                        else
                        {
                            if (grantBtn != null) grantBtn.Visibility = ViewStates.Gone;
                            // if (testPostBtn != null) testPostBtn.Visibility = ViewStates.Gone;
                        }
                    }
                    catch { }
                }

                musicSwitch.CheckedChange += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            edit.PutBoolean(PREF_MUSIC_STATUS, musicSwitch.Checked);
                            edit.Apply();
                        }
                        if (_musicDebugText != null) _musicDebugText.Visibility = musicSwitch.Checked ? ViewStates.Visible : ViewStates.Gone;
                        CheckListenerPerm();

                        // Notify running service
                        var intent = new Intent("nokandro.ACTION_SET_MUSIC_STATUS");
                        intent.PutExtra("enabled", musicSwitch.Checked);
                        LocalBroadcast.SendBroadcast(this, intent);
                    }
                    catch { }
                };
                
                // Initial check
                CheckListenerPerm();
            }

            // Amber logic removed

            // save autoStop preference
            if (autoStopSwitch != null)
            {
                autoStopSwitch.CheckedChange += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null)
                        {
                            edit.PutBoolean(PREF_AUTO_STOP, e.IsChecked);
                            edit.Apply();
                        }
                    }
                    catch { }
                };
            }

            // off-timer switch + minutes
            if (offTimerSwitch != null)
            {
                offTimerSwitch.CheckedChange += (s, e) =>
                {
                    try
                    {
                        SetControlEnabled(offTimerMinutesEdit, e.IsChecked);
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null) { edit.PutBoolean(PREF_OFF_TIMER_ENABLED, e.IsChecked); edit.Apply(); }
                    }
                    catch { }
                };
            }
            if (offTimerMinutesEdit != null)
            {
                offTimerMinutesEdit.FocusChange += (s, e) =>
                {
                    if (!e.HasFocus)
                    {
                        try
                        {
                            var val = int.TryParse(offTimerMinutesEdit.Text, out var v) ? v : 60;
                            if (val < 1) { val = 1; offTimerMinutesEdit.Text = val.ToString(); }
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var edit = prefs?.Edit();
                            if (edit != null) { edit.PutInt(PREF_OFF_TIMER_MINUTES, val); edit.Apply(); }
                        }
                        catch { }
                    }
                };
            }

            // Test Post Button logic
            if (testPostBtn != null)
            {
                testPostBtn.Click += (s, e) =>
                {
                   if (!NostrService.IsRunning)
                   {
                        Toast.MakeText(this, "Start service first", ToastLength.Short).Show();
                        return;
                   }
                   var intent = new Intent("nokandro.ACTION_TEST_POST");
                   LocalBroadcast.SendBroadcast(this, intent);
                };
            }

            if (grantBtn != null)
            {
                grantBtn.Click += (s, e) =>
                {
                    try
                    {
                        var intent = new Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
                        StartActivity(intent);
                    }
                    catch { try { Toast.MakeText(this, "Could not open settings", ToastLength.Short).Show(); } catch { } }
                };
            }

            // --- NIP-46 Bunker ---
            var bunkerSwitch = FindViewById<Switch>(Resource.Id.bunkerSwitch);
            var bunkerContainer = FindViewById<LinearLayout>(Resource.Id.bunkerContainer);
            var bunkerRelayEdit = FindViewById<EditText>(Resource.Id.bunkerRelayEdit);
            var bunkerUriText = FindViewById<TextView>(Resource.Id.bunkerUriText);
            var bunkerCopyBtn = FindViewById<Button>(Resource.Id.bunkerCopyBtn);
            var bunkerResetSecretBtn = FindViewById<Button>(Resource.Id.bunkerResetSecretBtn);
            _bunkerStatusText = FindViewById<TextView>(Resource.Id.bunkerStatusText);

            // restore bunker relay from prefs
            if (bunkerRelayEdit != null)
            {
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    bunkerRelayEdit.Text = prefs?.GetString(PREF_BUNKER_RELAY, "wss://ephemeral.snowflare.cc/") ?? "wss://ephemeral.snowflare.cc/";
                }
                catch { bunkerRelayEdit.Text = "wss://ephemeral.snowflare.cc/"; }

                bunkerRelayEdit.FocusChange += (s, e) =>
                {
                    if (!e.HasFocus)
                    {
                        try
                        {
                            var val = bunkerRelayEdit.Text ?? "wss://ephemeral.snowflare.cc/";
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var edit = prefs?.Edit();
                            if (edit != null) { edit.PutString(PREF_BUNKER_RELAY, val); edit.Apply(); }
                        }
                        catch { }
                    }
                };
            }

            // Register bunker service broadcasts
            _bunkerReceiver = new BroadcastReceiver();
            _bunkerReceiver.Receive += (ctx, intent) =>
            {
                if (intent.Action == "nokandro.ACTION_BUNKER_STARTED")
                {
                    var uri = intent.GetStringExtra("bunkerUri") ?? "";

                    // Persist the secret so the bunker URI survives restarts
                    try
                    {
                        var u = new Uri(uri);
                        var qs = System.Web.HttpUtility.ParseQueryString(u.Query);
                        var sec = qs["secret"];
                        if (!string.IsNullOrEmpty(sec))
                        {
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var edit = prefs?.Edit();
                            if (edit != null) { edit.PutString(PREF_BUNKER_SECRET, sec); edit.Apply(); }
                        }
                    }
                    catch { }

                    RunOnUiThread(() =>
                    {
                        if (bunkerUriText != null) bunkerUriText.Text = uri;
                        if (_bunkerStatusText != null) _bunkerStatusText.Text = "Bunker: starting...";
                    });
                    // Persist desired bunker enabled state
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null) { edit.PutBoolean(PREF_BUNKER_ENABLED, true); edit.Apply(); }
                    }
                    catch { }
                }
                else if (intent.Action == "nokandro.ACTION_BUNKER_LOG")
                {
                    var msg = intent.GetStringExtra("message") ?? "";
                    RunOnUiThread(() =>
                    {
                        if (_bunkerStatusText != null) _bunkerStatusText.Text = "Bunker: " + msg;
                    });
                }
                else if (intent.Action == "nokandro.ACTION_BUNKER_STOPPED")
                {
                    RunOnUiThread(() =>
                    {
                        if (_bunkerStatusText != null) _bunkerStatusText.Text = "Bunker: stopped";
                        if (bunkerUriText != null) bunkerUriText.Text = "";
                        // sync switch state
                        try { if (bunkerSwitch != null && bunkerSwitch.Checked) bunkerSwitch.Checked = false; } catch { }
                        // re-enable relay edit
                        try { if (bunkerRelayEdit != null) { bunkerRelayEdit.Enabled = true; bunkerRelayEdit.Alpha = 1.0f; } } catch { }
                    });
                    // Persist desired bunker disabled state
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null) { edit.PutBoolean(PREF_BUNKER_ENABLED, false); edit.Apply(); }
                    }
                    catch { }
                }
                else if (intent.Action == "nokandro.ACTION_BUNKER_CLIENT_AUTHORIZED")
                {
                    var pubkey = intent.GetStringExtra("clientPubkey") ?? "";
                    if (!string.IsNullOrEmpty(pubkey))
                    {
                        try
                        {
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var existing = prefs?.GetString(PREF_BUNKER_AUTHORIZED, "") ?? "";
                            var set = new HashSet<string>(existing.Split(',', StringSplitOptions.RemoveEmptyEntries));
                            if (set.Add(pubkey))
                            {
                                var edit = prefs?.Edit();
                                if (edit != null) { edit.PutString(PREF_BUNKER_AUTHORIZED, string.Join(",", set)); edit.Apply(); }
                            }
                        }
                        catch { }
                        try { RunOnUiThread(() => RefreshBunkerAuthorizedList()); } catch { }
                    }
                }
            };
            try
            {
                var bunkerFilter = new IntentFilter();
                bunkerFilter.AddAction("nokandro.ACTION_BUNKER_STARTED");
                bunkerFilter.AddAction("nokandro.ACTION_BUNKER_LOG");
                bunkerFilter.AddAction("nokandro.ACTION_BUNKER_STOPPED");
                bunkerFilter.AddAction("nokandro.ACTION_BUNKER_CLIENT_AUTHORIZED");
                LocalBroadcast.RegisterReceiver(_bunkerReceiver, bunkerFilter);
            }
            catch { }

            // Populate authorized clients list UI
            try { RefreshBunkerAuthorizedList(); } catch { }

            if (bunkerSwitch != null)
            {
                // Restore switch state from running service (use IsBunkerActive to distinguish from nostrconnect-only)
                if (BunkerService.IsBunkerActive) bunkerSwitch.Checked = true;
                // Disable relay edit while bunker is running
                try { if (bunkerRelayEdit != null) { bunkerRelayEdit.Enabled = !BunkerService.IsBunkerActive; bunkerRelayEdit.Alpha = BunkerService.IsBunkerActive ? 0.45f : 1.0f; } } catch { }

                bunkerSwitch.CheckedChange += (s, e) =>
                {
                    if (bunkerContainer != null)
                        bunkerContainer.Visibility = e.IsChecked ? ViewStates.Visible : ViewStates.Gone;

                    // Enable/disable relay edit based on bunker state
                    try { if (bunkerRelayEdit != null) { bunkerRelayEdit.Enabled = !e.IsChecked; bunkerRelayEdit.Alpha = e.IsChecked ? 0.45f : 1.0f; } } catch { }

                    // Persist user's bunker switch preference
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var edit = prefs?.Edit();
                        if (edit != null) { edit.PutBoolean(PREF_BUNKER_ENABLED, e.IsChecked); edit.Apply(); }
                    }
                    catch { }

                    if (e.IsChecked && !BunkerService.IsBunkerActive)
                    {
                        StartBunkerService(bunkerRelayEdit, nsec);
                    }
                    else if (!e.IsChecked && BunkerService.IsBunkerActive)
                    {
                        StopBunkerService();
                    }
                };
                // Restore user's desired bunker state (attempt start if user had enabled it)
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var want = prefs?.GetBoolean(PREF_BUNKER_ENABLED, false) ?? false;
                    if (want && !BunkerService.IsBunkerActive)
                    {
                        // Programmatically checking will trigger the CheckedChange handler and start the service
                        bunkerSwitch.Checked = true;
                    }
                }
                catch { }
            }

            if (bunkerCopyBtn != null)
            {
                bunkerCopyBtn.Click += (s, e) =>
                {
                    try
                    {
                        var uri = bunkerUriText?.Text;
                        if (string.IsNullOrEmpty(uri)) return;
                        var cm = (Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
                        if (cm != null) cm.PrimaryClip = Android.Content.ClipData.NewPlainText("bunker", uri);
                        Toast.MakeText(this, "Copied", ToastLength.Short)?.Show();
                    }
                    catch { }
                };
            }

            if (bunkerResetSecretBtn != null)
            {
                bunkerResetSecretBtn.Click += (s, e) =>
                {
                    try
                    {
                        var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var saved = prefs?.GetString(PREF_BUNKER_SECRET, null);
                        if (string.IsNullOrEmpty(saved))
                        {
                            Toast.MakeText(this, "No saved secret", ToastLength.Short)?.Show();
                            return;
                        }

                        var preview = saved.Length > 8 ? saved[..8] + "..." : saved;
                        var resetDialog = new Android.App.AlertDialog.Builder(this)
                             .SetTitle("Reset Bunker Secret")
                             .SetMessage($"Delete saved secret ({preview})?\nBunker will be stopped and a new URI will be generated on next start.")
                             .SetPositiveButton("Delete", (sender, args) =>
                             {
                                 try
                                 {
                                     var edit = prefs?.Edit();
                                     if (edit != null) { edit.Remove(PREF_BUNKER_SECRET); edit.Remove(PREF_BUNKER_AUTHORIZED); edit.Apply(); }
                                     // Stop bunker and uncheck switch so next start uses new secret
                                     if (BunkerService.IsRunning) StopBunkerService();
                                     try { if (bunkerSwitch != null && bunkerSwitch.Checked) bunkerSwitch.Checked = false; } catch { }
                                     Toast.MakeText(this, "Secret deleted", ToastLength.Short)?.Show();
                                 }
                                 catch { }
                             })
                             .SetNegativeButton("Cancel", (sender, args) => { })
                             .Create();

                        resetDialog.Show();
                        try
                        {
                            resetDialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(Android.Graphics.Color.ParseColor("#C2185B"));
                            resetDialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(Android.Graphics.Color.ParseColor("#6B7280"));
                        }
                        catch { }
                    }
                    catch { }
                };
            }

            // --- nostrconnect:// ---
            var ncUriEdit = FindViewById<EditText>(Resource.Id.ncUriEdit);
            var ncConnectBtn = FindViewById<Button>(Resource.Id.ncConnectBtn);
            _ncStatusText = FindViewById<TextView>(Resource.Id.ncStatusText);
            _ncClientList = FindViewById<LinearLayout>(Resource.Id.ncClientList);

            if (ncConnectBtn != null)
            {
                ncConnectBtn.Click += (s, e) =>
                {
                    var uriText = ncUriEdit?.Text?.Trim() ?? "";
                    if (!NostrConnectUri.TryParse(uriText, out var parsed) || parsed == null)
                    {
                        Toast.MakeText(this, "Invalid nostrconnect:// URI", ToastLength.Short)?.Show();
                        return;
                    }
                    StartNostrConnectSession(parsed.RawUri, nsec);
                    if (ncUriEdit != null) ncUriEdit.Text = "";
                };
            }

            // QR code scanning via Google Code Scanner
            var ncScanQrBtn = FindViewById<Button>(Resource.Id.ncScanQrBtn);
            if (ncScanQrBtn != null)
            {
                ncScanQrBtn.Click += (s, e) =>
                {
                    try
                    {
                        var options = new Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScannerOptions.Builder()
                            .SetBarcodeFormats(Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode.FormatQrCode)
                            .Build();
                        var scanner = Xamarin.Google.MLKit.Vision.CodeScanner.GmsBarcodeScanning.GetClient(this, options);
                        scanner.StartScan()
                            .AddOnSuccessListener(new QrScanSuccessListener(barcode =>
                            {
                                var rawValue = barcode.RawValue;
                                if (string.IsNullOrEmpty(rawValue)) return;
                                RunOnUiThread(() =>
                                {
                                    if (ncUriEdit != null) ncUriEdit.Text = rawValue;
                                    // Auto-connect if it's a valid nostrconnect:// URI
                                    if (NostrConnectUri.TryParse(rawValue, out var parsed) && parsed != null)
                                    {
                                        StartNostrConnectSession(parsed.RawUri, nsec);
                                        if (ncUriEdit != null) ncUriEdit.Text = "";
                                    }
                                    else
                                    {
                                        Toast.MakeText(this, "QR code is not a valid nostrconnect:// URI", ToastLength.Short)?.Show();
                                    }
                                });
                            }))
                            .AddOnFailureListener(new QrScanFailureListener(ex =>
                            {
                                RunOnUiThread(() =>
                                {
                                    // Ignore user cancellation; only show error for unexpected failures
                                    try
                                    {
                                        var msg = ex?.Message ?? "";
                                        if (msg.Contains("cancel", StringComparison.OrdinalIgnoreCase)) return;
                                        if (ex is Xamarin.Google.MLKit.Common.MlKitException) return;
                                    }
                                    catch { }
                                    Toast.MakeText(this, "Scan cancelled or failed", ToastLength.Short)?.Show();
                                });
                            }));
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, "Scanner error: " + ex.Message, ToastLength.Short)?.Show();
                    }
                };
            }

            // Register nostrconnect broadcast receiver
            _ncReceiver = new BroadcastReceiver();
            _ncReceiver.Receive += (ctx, intent) =>
            {
                if (intent.Action == "nokandro.ACTION_NC_LIST")
                {
                    var pubkeys = intent.GetStringArrayExtra("pubkeys") ?? [];
                    var uris = intent.GetStringArrayExtra("uris") ?? [];
                    RunOnUiThread(() => RefreshNostrConnectClientList(pubkeys, uris));
                }
                else if (intent.Action == "nokandro.ACTION_NC_LOG")
                {
                    var msg = intent.GetStringExtra("message") ?? "";
                    var pk = intent.GetStringExtra("clientPubkey") ?? "";
                    var label = pk.Length >= 12 ? pk[..12] + "..." : pk;
                    RunOnUiThread(() =>
                    {
                        if (_ncStatusText != null)
                            _ncStatusText.Text = $"[{label}] {msg}";
                    });
                }
            };
            try
            {
                var ncFilter = new IntentFilter();
                ncFilter.AddAction("nokandro.ACTION_NC_LIST");
                ncFilter.AddAction("nokandro.ACTION_NC_LOG");
                LocalBroadcast.RegisterReceiver(_ncReceiver, ncFilter);
            }
            catch { }

            // Restore persisted nostrconnect clients and show them in the list
            RestoreNostrConnectClients(nsec);

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

            if (fetchRelaysBtn != null)
            {
                fetchRelaysBtn.Click += async (s, e) => await FetchRelayListAsync(relay, npub);
            }

            // set initial start/stop button state from service flag and npub validity
            try
            {
                setControlEnabled(start, !NostrService.IsRunning && IsNpubValid(npub.Text));
                setControlEnabled(stop, NostrService.IsRunning);
                // disable all settings if service already running
                setControlEnabled(relay, !NostrService.IsRunning);
                try { setControlEnabled(fetchRelaysBtn, !NostrService.IsRunning); } catch { }
                setControlEnabled(npub, !NostrService.IsRunning);
                if (nsec != null) setControlEnabled(nsec, !NostrService.IsRunning);
                setControlEnabled(truncate, !NostrService.IsRunning);
                try { setControlEnabled(truncateEllipsis, !NostrService.IsRunning); } catch { }
                setControlEnabled(allowOthers, !NostrService.IsRunning);
                setControlEnabled(speakPetSwitch, !NostrService.IsRunning);
                setControlEnabled(voiceFollowed, !NostrService.IsRunning);
                setControlEnabled(voiceOther, !NostrService.IsRunning);
                setControlEnabled(refreshVoices, !NostrService.IsRunning);
                setControlEnabled(voiceLang, !NostrService.IsRunning);
                try { setControlEnabled(musicSwitch, !NostrService.IsRunning && IsNsecValid()); } catch { }
                try { setControlEnabled(ttsSwitch, !NostrService.IsRunning); } catch { }
                try { setControlEnabled(skipContentWarningSwitch, !NostrService.IsRunning); } catch { }
                try { setControlEnabled(autoStopSwitch, !NostrService.IsRunning); } catch { }
                try { setControlEnabled(offTimerSwitch, !NostrService.IsRunning); } catch { }
                try { setControlEnabled(offTimerMinutesEdit, !NostrService.IsRunning && (offTimerSwitch?.Checked ?? false)); } catch { }
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

                // Check for notification listener permission if Now Playing is enabled
                try
                {
                    if (musicSwitch != null && musicSwitch.Checked)
                    {
                        var enabledListeners = Android.Provider.Settings.Secure.GetString(ContentResolver, "enabled_notification_listeners");
                        var hasPerm = !string.IsNullOrEmpty(enabledListeners) && (enabledListeners?.Contains(PackageName) ?? false);
                        if (!hasPerm)
                        {
                            try
                            {
                                var dlg = new Android.App.AlertDialog.Builder(this)
                                    .SetTitle("Permission Required")
                                    .SetMessage("To use Now Playing, you must grant the Notification Listener permission.")
                                    .SetPositiveButton("Grant", (sender, args) =>
                                    {
                                        try
                                        {
                                            var intent = new Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS");
                                            StartActivity(intent);
                                        }
                                        catch { try { Toast.MakeText(this, "Could not open settings", ToastLength.Short).Show(); } catch { } }
                                    })
                                    .SetNegativeButton("Cancel", (sender, args) => { })
                                    .Show();
                                try
                                {
                                    dlg.GetButton((int)Android.Content.DialogButtonType.Positive)?.SetTextColor(Android.Graphics.Color.ParseColor("#C2185B"));
                                    dlg.GetButton((int)Android.Content.DialogButtonType.Negative)?.SetTextColor(Android.Graphics.Color.ParseColor("#6B7280"));
                                }
                                catch { }
                            }
                            catch { }
                            return;
                        }
                    }
                }
                catch { }

                var intent = new Intent(this, typeof(NostrService));
                var truncateLen = CONTENT_TRUNCATE_LENGTH;
                try { truncateLen = int.Parse(truncate.Text ?? string.Empty); } catch { }

                intent.PutExtra("relay", relay.Text ?? "wss://relay-jp.nostr.wirednet.jp/");
                intent.PutExtra("npub", npub.Text ?? string.Empty);
                if (nsec != null) intent.PutExtra("nsec", nsec.Text ?? string.Empty);

                intent.PutExtra("allowOthers", allowOthers.Checked);
                intent.PutExtra("enableTts", ttsSwitch?.Checked ?? true);
                intent.PutExtra("skipContentWarning", skipContentWarningSwitch?.Checked ?? false);
                intent.PutExtra("autoStop", autoStopSwitch?.Checked ?? true);
                intent.PutExtra("truncateLen", truncateLen);
                // include user-configured ellipsis for truncation
                try
                {
                    string ell = " ...";
                    try { ell = _truncateEllipsisField?.Text ?? GetSharedPreferences(PREFS_NAME, FileCreationMode.Private)?.GetString(PREF_TRUNCATE_ELLIPSIS, ell) ?? ell; } catch { ell = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private)?.GetString(PREF_TRUNCATE_ELLIPSIS, " ...") ?? " ..."; }
                    intent.PutExtra("truncateEllipsis", ell);
                }
                catch { intent.PutExtra("truncateEllipsis", " ..."); }
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
                // pass music status preference
                try { intent.PutExtra("enableMusicStatus", musicSwitch != null && musicSwitch.Checked); } catch { }
                // pass off-timer (0 = disabled)
                try
                {
                    var offEnabled = offTimerSwitch?.Checked ?? false;
                    var offMins = int.TryParse(offTimerMinutesEdit?.Text, out var v) ? v : 60;
                    if (offMins < 1) offMins = 1;
                    intent.PutExtra("offTimerMinutes", offEnabled ? offMins : 0);
                }
                catch { intent.PutExtra("offTimerMinutes", 0); }

                // Reset UI status to not loaded; service will broadcast updates when lists are loaded
                try { followStatus.Text = "Follow list: not loaded"; } catch { }
                try { muteStatus.Text = "Public mute: not loaded"; } catch { }
                try { lastContent.Text = "(no note yet)"; } catch { }

                // persist selections
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    edit.PutString(PREF_VOICE_FOLLOWED, followedVoice);
                    edit.PutString(PREF_VOICE_OTHER, otherVoice);
                    edit.PutString(PREF_RELAY, relay.Text ?? "wss://relay-jp.nostr.wirednet.jp/");
                    edit.PutString(PREF_NPUB, npub.Text ?? string.Empty);
                    edit.PutString(PREF_NSEC, nsec != null ? nsec.Text ?? string.Empty : string.Empty);
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
                // start countdown display if off-timer is active
                try
                {
                    var offEnabled2 = offTimerSwitch?.Checked ?? false;
                    var offMins2 = int.TryParse(offTimerMinutesEdit?.Text, out var ov) ? ov : 60;
                    if (offMins2 < 1) offMins2 = 1;
                    if (offEnabled2 && offMins2 > 0)
                    {
                        var endTime = DateTime.UtcNow.AddMinutes(offMins2);
                        SaveCountdownEndTime(endTime);
                        StartCountdown(endTime);
                    }
                    else
                    {
                        StopCountdown();
                        ClearCountdownEndTime();
                    }
                }
                catch { }
                // update UI state
                try { SetControlEnabled(start, false); SetControlEnabled(stop, true); } catch { }
                // disable inputs during run
                SetControlEnabled(relay, false);
                try { SetControlEnabled(fetchRelaysBtn, false); } catch { }
                SetControlEnabled(npub, false);
                if (nsec != null) SetControlEnabled(nsec, false);
                SetControlEnabled(truncate, false);
                try { SetControlEnabled(truncateEllipsis, false); } catch { }
                SetControlEnabled(allowOthers, false);
                SetControlEnabled(speakPetSwitch, false);
                SetControlEnabled(voiceFollowed, false);
                SetControlEnabled(voiceOther, false);
                SetControlEnabled(refreshVoices, false);
                SetControlEnabled(voiceLang, false);
                try { SetControlEnabled(musicSwitch, false); } catch { }
                try { SetControlEnabled(ttsSwitch, false); } catch { }
                try { SetControlEnabled(autoStopSwitch, false); } catch { }
                try { SetControlEnabled(offTimerSwitch, false); } catch { }
                try { SetControlEnabled(offTimerMinutesEdit, false); } catch { }
            };

            stop.Click += (s, e) =>
            {
                var intent = new Intent(this, typeof(NostrService));
                intent.SetAction("STOP");
                StartService(intent);
                try { SetControlEnabled(stop, false); } catch { }
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
                            try { SetControlEnabled(fetchRelaysBtn, false); } catch { }
                            try { SetControlEnabled(npub, false); } catch { }
                            if (nsec != null) try { SetControlEnabled(nsec, false); } catch { }
                            try { SetControlEnabled(truncate, false); } catch { }
                            try { SetControlEnabled(truncateEllipsis, false); } catch { }
                            try { SetControlEnabled(allowOthers, false); } catch { }
                            try { SetControlEnabled(speakPetSwitch, false); } catch { }
                            try { SetControlEnabled(voiceFollowed, false); } catch { }
                            try { SetControlEnabled(voiceOther, false); } catch { }
                            try { SetControlEnabled(ttsSwitch, false); } catch { }
                            try { SetControlEnabled(skipContentWarningSwitch, false); } catch { }
                            try { SetControlEnabled(autoStopSwitch, false); } catch { }
                            try { SetControlEnabled(offTimerSwitch, false); } catch { }
                            try { SetControlEnabled(offTimerMinutesEdit, false); } catch { }
                        });
                        // Start countdown if off-timer was set (e.g. via Tasker or Widget)
                        try
                        {
                            var rPrefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            var endTicks = rPrefs?.GetLong(PREF_OFF_TIMER_END_TICKS, 0L) ?? 0L;
                            if (endTicks > 0)
                            {
                                var endTime = new DateTime(endTicks, DateTimeKind.Utc);
                                if (endTime > DateTime.UtcNow)
                                    StartCountdown(endTime);
                            }
                        }
                        catch { }
                    }
                    else if (intent.Action == "nokandro.ACTION_SERVICE_STOPPED")
                    {
                        StopCountdown();
                        ClearCountdownEndTime();
                        RunOnUiThread(() =>
                        {
                            try { SetControlEnabled(start, !NostrService.IsRunning && IsNpubValid(npub.Text)); SetControlEnabled(stop, false); } catch { }
                            try { SetControlEnabled(relay, true); } catch { }
                            try { SetControlEnabled(fetchRelaysBtn, true); } catch { }
                            try { SetControlEnabled(npub, true); } catch { }
                            if (nsec != null) try { SetControlEnabled(nsec, true); } catch { }
                            try { SetControlEnabled(truncate, true); } catch { }
                            try { SetControlEnabled(truncateEllipsis, true); } catch { }
                            try { SetControlEnabled(allowOthers, true); } catch { }
                            try { SetControlEnabled(speakPetSwitch, true); } catch { }
                            try { SetControlEnabled(voiceFollowed, true); } catch { }
                            try { SetControlEnabled(voiceOther, true); } catch { }
                            try { SetControlEnabled(refreshVoices, true); } catch { }
                            try { SetControlEnabled(ttsSwitch, true); } catch { }
                            try { SetControlEnabled(skipContentWarningSwitch, true); } catch { }
                            try { SetControlEnabled(autoStopSwitch, true); } catch { }
                            try { SetControlEnabled(offTimerSwitch, true); } catch { }
                            try { SetControlEnabled(offTimerMinutesEdit, offTimerSwitch?.Checked ?? false); } catch { }
                            // speechSeek remains enabled
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
                if (intent.Action == "nokandro.ACTION_MEDIA_STATUS")
                {
                    var artist = intent.GetStringExtra("artist") ?? "";
                    var title = intent.GetStringExtra("title") ?? "";
                    var playing = intent.GetBooleanExtra("playing", false);
                    _lastMusicInfo = playing ? $"Music: {title} - {artist}" : "Music: (paused)";
                    RunOnUiThread(() => { if (_musicDebugText != null) _musicDebugText.Text = _lastMusicInfo; });
                    return;
                }

                if (intent.Action == "nokandro.ACTION_MUSIC_POST_STATUS")
                {
                    var status = intent.GetStringExtra("status") ?? "";
                    RunOnUiThread(() => 
                    { 
                        if (_musicDebugText != null) 
                        {
                            var current = _musicDebugText.Text ?? "";
                            if (!current.Contains("Status:")) current = _lastMusicInfo;
                            // If current already has Status line, replace it
                            var idx = current.IndexOf("\nStatus:");
                            if (idx >= 0) current = current.Substring(0, idx);
                            
                            _musicDebugText.Text = current + "\nStatus: " + status;
                        }
                    });
                    return;
                }

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
                    var text = loaded ? $"Muted user: loaded ({count})" : "Muted user: not loaded";
                    RunOnUiThread(() => { muteStatus.Text = text; });
                    return;
                }

                    if (intent.Action == "nokandro.ACTION_MUTED_WORDS_UPDATE")
                    {
                        var loaded = intent.GetBooleanExtra("mutedWordsLoaded", false);
                        var count = intent.GetIntExtra("mutedWordsCount", 0);
                        var text = loaded ? $"Muted words: loaded ({count})" : "Muted words: not loaded";
                        RunOnUiThread(() => { if (mutedWordsStatusText != null) mutedWordsStatusText.Text = text; });
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
            filter.AddAction("nokandro.ACTION_MUTED_WORDS_UPDATE");
            filter.AddAction("nokandro.ACTION_MEDIA_STATUS");
            filter.AddAction("nokandro.ACTION_MUSIC_POST_STATUS");
            LocalBroadcast.RegisterReceiver(_receiver, filter);
#pragma warning restore CS8600,CS8601,CS8602
        }

        protected override void OnPause()
        {
            base.OnPause();
            try { _countdownTimer?.Dispose(); _countdownTimer = null; } catch { }
            try
            {
                // Save current state to preferences to prevent data loss when Activity is recreated
                // (e.g. going to Settings to allow restricted notification listener permissions)
                var relay = FindViewById<EditText>(Resource.Id.relayEdit);
                var npub = FindViewById<EditText>(Resource.Id.npubEdit);
                var nsec = FindViewById<EditText>(Resource.Id.nsecEdit);

                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    if (relay != null) edit.PutString(PREF_RELAY, relay.Text ?? string.Empty);
                    if (npub != null) edit.PutString(PREF_NPUB, npub.Text ?? string.Empty);
                    if (nsec != null) edit.PutString(PREF_NSEC, nsec.Text ?? string.Empty);
                    edit.Apply();
                }
            }
            catch { }
        }

        protected override void OnResume()
        {
            base.OnResume();
            try
            {
                // Sync UI state
                var start = FindViewById<Button>(Resource.Id.startBtn);
                var stop = FindViewById<Button>(Resource.Id.stopBtn);
                var relay = FindViewById<EditText>(Resource.Id.relayEdit);
                var fetchRelaysBtn = FindViewById<Button>(Resource.Id.fetchRelaysBtn);
                var npub = FindViewById<EditText>(Resource.Id.npubEdit);
                var nsec = FindViewById<EditText>(Resource.Id.nsecEdit);
                var truncate = FindViewById<EditText>(Resource.Id.truncateEdit);
                var truncateEllipsis = FindViewById<EditText>(Resource.Id.truncateEllipsisEdit);
                var allowOthers = FindViewById<Switch>(Resource.Id.allowOthersSwitch);
                var speakPetSwitch = FindViewById<Switch>(Resources.GetIdentifier("speakPetnameSwitch", "id", PackageName));
                var voiceFollowed = FindViewById<Spinner>(Resource.Id.voiceFollowedSpinner);
                var voiceOther = FindViewById<Spinner>(Resource.Id.voiceOtherSpinner);
                var refreshVoices = FindViewById<Button>(Resource.Id.refreshVoicesBtn);
                var musicSwitch = FindViewById<Switch>(Resource.Id.musicStatusSwitch);
                var ttsSwitch = FindViewById<Switch>(Resource.Id.ttsSwitch);
                var skipContentWarningSwitch = FindViewById<Switch>(Resource.Id.skipContentWarningSwitch);
                var voiceLang = FindViewById<Spinner>(Resource.Id.voiceLangSpinner);
                var autoStopSwitch = FindViewById<Switch>(Resource.Id.autoStopSwitch);
                var offTimerSwitch = FindViewById<Switch>(Resource.Id.offTimerSwitch);
                var offTimerMinutesEdit = FindViewById<EditText>(Resource.Id.offTimerMinutesEdit);

            // Helper to set Enabled + visual appearance
            void SetControlEnabled(View? v, bool enabled)
            {
                if (v == null) return;
                try
                {
                    v.Enabled = enabled;
                    v.Alpha = enabled ? 1.0f : 0.45f;
                    if (v is EditText et)
                    {
                        try { et.Focusable = enabled; } catch { }
                        try { et.FocusableInTouchMode = enabled; } catch { }
                        et.Clickable = enabled;
                        try { et.SetTextColor(enabled ? Android.Graphics.Color.Black : Android.Graphics.Color.DarkGray); } catch { }
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

            bool IsNsecValid()
            {
                if (nsec == null) return false;
                var t = nsec.Text?.Trim() ?? "";
                if (!t.StartsWith("nsec1")) return false;
                try
                {
                    var (hrp, data) = Bech32Decode(t);
                    return hrp == "nsec" && data != null;
                }
                catch { return false; }
            }

            if (start != null && stop != null)
            {
                bool running = NostrService.IsRunning;
                try { SetControlEnabled(start, !running && IsNpubValid(npub?.Text)); } catch {}
                try { SetControlEnabled(stop, running); } catch {}
                try { SetControlEnabled(relay, !running); } catch { }
                try { SetControlEnabled(fetchRelaysBtn, !running); } catch { }
                try { SetControlEnabled(npub, !running); } catch { }
                if (nsec != null) try { SetControlEnabled(nsec, !running); } catch { }
                try { SetControlEnabled(truncate, !running); } catch { }
                try { SetControlEnabled(truncateEllipsis, !running); } catch { }
                try { SetControlEnabled(allowOthers, !running); } catch { }
                try { SetControlEnabled(speakPetSwitch, !running); } catch { }
                try { SetControlEnabled(voiceFollowed, !running); } catch { }
                try { SetControlEnabled(voiceOther, !running); } catch { }
                try { SetControlEnabled(refreshVoices, !running); } catch { }
                try { SetControlEnabled(voiceLang, !running); } catch { }
                try { SetControlEnabled(musicSwitch, !running && IsNsecValid()); } catch { }
                try { SetControlEnabled(ttsSwitch, !running); } catch { }
                try { SetControlEnabled(skipContentWarningSwitch, !running); } catch { }
                try { SetControlEnabled(autoStopSwitch, !running); } catch { }
                try { SetControlEnabled(offTimerSwitch, !running); } catch { }
                try { SetControlEnabled(offTimerMinutesEdit, !running && (offTimerSwitch?.Checked ?? false)); } catch { }
            }

                // Request current list status from service (to recover from activity restart)
                var listReq = new Intent("nokandro.ACTION_REQUEST_LIST_STATUS");
                LocalBroadcast.SendBroadcast(this, listReq);

                // Update UI based on current permissions and settings
                var grantBtn = FindViewById<Button>(Resource.Id.grantListenerBtn);
                var testPostBtn = FindViewById<Button>(Resource.Id.testPostBtn);

                if (musicSwitch != null)
                {
                    if (musicSwitch.Checked)
                    {
                        var enabledListeners = Android.Provider.Settings.Secure.GetString(ContentResolver, "enabled_notification_listeners");
                        var hasPerm = !string.IsNullOrEmpty(enabledListeners) && (enabledListeners?.Contains(PackageName) ?? false);
                        if (grantBtn != null) grantBtn.Visibility = hasPerm ? ViewStates.Gone : ViewStates.Visible;
                    }
                    else
                    {
                        if (grantBtn != null) grantBtn.Visibility = ViewStates.Gone;
                    }
                }
                // Resume countdown if service is running with an active off-timer
                if (NostrService.IsRunning)
                {
                    try
                    {
                        var rPrefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                        var endTicks = rPrefs?.GetLong(PREF_OFF_TIMER_END_TICKS, 0L) ?? 0L;
                        if (endTicks > 0)
                        {
                            var endTime = new DateTime(endTicks, DateTimeKind.Utc);
                            if (endTime > DateTime.UtcNow)
                                StartCountdown(endTime);
                        }
                    }
                    catch { }
                }

                // Sync bunker switch with service state
                try
                {
                    var bunkerSwitch = FindViewById<Switch>(Resource.Id.bunkerSwitch);
                    var bunkerContainer = FindViewById<LinearLayout>(Resource.Id.bunkerContainer);
                    var bunkerRelayEdit = FindViewById<EditText>(Resource.Id.bunkerRelayEdit);
                    if (bunkerSwitch != null)
                    {
                        var bunkerActive = BunkerService.IsBunkerActive;
                        if (bunkerSwitch.Checked != bunkerActive)
                            bunkerSwitch.Checked = bunkerActive;
                        if (bunkerContainer != null)
                            bunkerContainer.Visibility = bunkerActive ? ViewStates.Visible : ViewStates.Gone;
                        // Sync relay edit enabled state
                        if (bunkerRelayEdit != null) { bunkerRelayEdit.Enabled = !bunkerActive; bunkerRelayEdit.Alpha = bunkerActive ? 0.45f : 1.0f; }
                        // Restore bunker URI and status from running service
                        if (bunkerActive)
                        {
                            var bunkerUriText = FindViewById<TextView>(Resource.Id.bunkerUriText);
                            if (bunkerUriText != null && !string.IsNullOrEmpty(BunkerService.LastBunkerUri))
                                bunkerUriText.Text = BunkerService.LastBunkerUri;
                            if (_bunkerStatusText != null)
                                _bunkerStatusText.Text = "Bunker: running";
                        }
                    }
                }
                catch { }
            }
            catch { }
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            try
            {
                var openTab = intent?.GetStringExtra("openTab");
                if (openTab == "bunker" || openTab == "main")
                {
                    bool mainSelected = openTab == "main";
                    var tabMainBtn = FindViewById<Button>(Resource.Id.tabMainBtn);
                    var tabBunkerBtn = FindViewById<Button>(Resource.Id.tabBunkerBtn);
                    var tabMainContent = FindViewById<View>(Resource.Id.tabMainContent);
                    var tabBunkerContent = FindViewById<View>(Resource.Id.tabBunkerContent);

                    if (tabMainContent != null) tabMainContent.Visibility = mainSelected ? ViewStates.Visible : ViewStates.Gone;
                    if (tabBunkerContent != null) tabBunkerContent.Visibility = mainSelected ? ViewStates.Gone : ViewStates.Visible;
                    if (tabMainBtn != null)
                    {
                        tabMainBtn.Alpha = mainSelected ? 1.0f : 0.5f;
                        tabMainBtn.PaintFlags = mainSelected
                            ? tabMainBtn.PaintFlags | Android.Graphics.PaintFlags.UnderlineText
                            : tabMainBtn.PaintFlags & ~Android.Graphics.PaintFlags.UnderlineText;
                    }
                    if (tabBunkerBtn != null)
                    {
                        tabBunkerBtn.Alpha = mainSelected ? 0.5f : 1.0f;
                        tabBunkerBtn.PaintFlags = mainSelected
                            ? tabBunkerBtn.PaintFlags & ~Android.Graphics.PaintFlags.UnderlineText
                            : tabBunkerBtn.PaintFlags | Android.Graphics.PaintFlags.UnderlineText;
                    }
                }
            }
            catch { }
        }

        private void StartCountdown(DateTime endTimeUtc)
        {
            _countdownTimer?.Dispose();
            _countdownTimer = null;
            RunOnUiThread(() => { if (_offTimerCountdownView != null) _offTimerCountdownView.Visibility = ViewStates.Visible; });
            _countdownTimer = new System.Threading.Timer(_ =>
            {
                var remaining = endTimeUtc - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    _countdownTimer?.Dispose();
                    _countdownTimer = null;
                    RunOnUiThread(() => { if (_offTimerCountdownView != null) _offTimerCountdownView.Visibility = ViewStates.Gone; });
                    return;
                }
                var h = (int)remaining.TotalHours;
                var text = $"{h:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
                RunOnUiThread(() => { if (_offTimerCountdownView != null) _offTimerCountdownView.Text = text; });
            }, null, 0, 1000);
        }

        private void StopCountdown()
        {
            _countdownTimer?.Dispose();
            _countdownTimer = null;
            RunOnUiThread(() => { if (_offTimerCountdownView != null) _offTimerCountdownView.Visibility = ViewStates.Gone; });
        }

        private void SaveCountdownEndTime(DateTime endTimeUtc)
        {
            try
            {
                var p = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private)?.Edit();
                if (p != null) { p.PutLong(PREF_OFF_TIMER_END_TICKS, endTimeUtc.Ticks); p.Apply(); }
            }
            catch { }
        }

        private void ClearCountdownEndTime()
        {
            try
            {
                var p = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private)?.Edit();
                if (p != null) { p.Remove(PREF_OFF_TIMER_END_TICKS); p.Apply(); }
            }
            catch { }
        }

        private void StartBunkerService(EditText? bunkerRelayEdit, EditText? nsec)
        {
            try
            {
                var nsecText = nsec?.Text?.Trim() ?? "";
                if (!nsecText.StartsWith("nsec1"))
                {
                    Toast.MakeText(this, "nsec required for bunker", ToastLength.Short)?.Show();
                    return;
                }

                var (hrp, data) = Bech32Decode(nsecText);
                if (hrp != "nsec" || data == null)
                {
                    Toast.MakeText(this, "Invalid nsec", ToastLength.Short)?.Show();
                    return;
                }

                var privKey = ConvertBits(data, 5, 8, false);
                if (privKey == null || privKey.Length != 32)
                {
                    Toast.MakeText(this, "Invalid nsec", ToastLength.Short)?.Show();
                    return;
                }

                var relayUrl = bunkerRelayEdit?.Text?.Trim();
                if (string.IsNullOrEmpty(relayUrl)) relayUrl = "wss://ephemeral.snowflare.cc/";

                // Convert privKey to hex string for Intent transport
                var nsecHex = BitConverter.ToString(privKey).Replace("-", "").ToLowerInvariant();

                var intent = new Intent(this, typeof(BunkerService));
                intent.PutExtra("nsecHex", nsecHex);
                intent.PutExtra("relay", relayUrl);

                // Reuse persisted secret and authorized clients so the bunker survives restarts
                try
                {
                    var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                    var savedSecret = prefs?.GetString(PREF_BUNKER_SECRET, null);
                    if (!string.IsNullOrEmpty(savedSecret))
                        intent.PutExtra("secret", savedSecret);
                    var savedClients = prefs?.GetString(PREF_BUNKER_AUTHORIZED, null);
                    if (!string.IsNullOrEmpty(savedClients))
                        intent.PutExtra("authorizedClients", savedClients.Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
                catch { }

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    StartForegroundService(intent);
                else
                    StartService(intent);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, "Bunker error: " + ex.Message, ToastLength.Short)?.Show();
            }
        }

        private void StopBunkerService()
        {
            try
            {
                var intent = new Intent(this, typeof(BunkerService));
                intent.SetAction("STOP");
                StartService(intent);
            }
            catch { }
        }

        // Refresh the authorized clients list UI under the bunker container.
        private void RefreshBunkerAuthorizedList()
        {
            try
            {
                var bunkerContainer = FindViewById<LinearLayout>(Resource.Id.bunkerContainer);
                if (bunkerContainer == null) return;

                // Remove existing list if present
                Android.Views.View? existing = null;
                for (int i = 0; i < bunkerContainer.ChildCount; i++)
                {
                    var child = bunkerContainer.GetChildAt(i);
                    if (child != null && string.Equals(child.Tag?.ToString(), "bunkerAuthorizedList", StringComparison.Ordinal)) { existing = child; break; }
                }
                if (existing != null) bunkerContainer.RemoveView(existing);

                // Build new vertical list
                var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
                list.Tag = "bunkerAuthorizedList";

                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var saved = prefs?.GetString(PREF_BUNKER_AUTHORIZED, "") ?? "";
                var items = new HashSet<string>(saved.Split(',', StringSplitOptions.RemoveEmptyEntries));

                // Resolve known names from nostrconnect settings for display consistency
                var ncCustomNames = LoadNcClientNames();
                var ncMetadataNames = LoadNcMetadataNamesByPubkey();

                if (items.Count == 0)
                {
                    var emptyTv = new TextView(this);
                    emptyTv.Text = "No authorized clients";
                    emptyTv.SetPadding(8, 8, 8, 8);
                    list.AddView(emptyTv);
                }
                else
                {
                    foreach (var pk in items)
                    {
                        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                        row.SetPadding(4, 4, 4, 4);

                        var tv = new TextView(this);
                        var shortPk = $"{pk[..Math.Min(12, pk.Length)]}...";
                        string? displayName = null;
                        if (ncCustomNames.TryGetValue(pk, out var customName) && !string.IsNullOrEmpty(customName))
                            displayName = customName;
                        else if (ncMetadataNames.TryGetValue(pk, out var metadataName) && !string.IsNullOrEmpty(metadataName))
                            displayName = metadataName;
                        tv.Text = !string.IsNullOrEmpty(displayName) ? $"{displayName} ({shortPk})" : shortPk;
                        tv.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.0f);
                        tv.SetTextColor(Android.Graphics.Color.Black);
                        tv.PaintFlags |= Android.Graphics.PaintFlags.UnderlineText;
                        var clientPk = pk;
                        var currentName = displayName ?? "";
                        tv.Click += (s, e) => ShowEditNcClientNameDialog(clientPk, currentName);

                        var btn = new Button(this)
                        {
                            Text = "Remove"
                        };
                        btn.SetMinimumWidth(0);
                        btn.SetMinimumHeight(0);
                        btn.SetPadding(16, 0, 16, 0);
                        btn.Click += (s, e) =>
                        {
                            var name = GetNcClientDisplayName(clientPk);
                            var removeDialog = new Android.App.AlertDialog.Builder(this)
                                .SetTitle("Remove client")
                                .SetMessage($"Disconnect and remove {name}?")
                                .SetPositiveButton("Remove", (sender, args) =>
                                {
                                    RemoveNcClientName(clientPk);
                                    StopNostrConnectSession(clientPk);
                                })
                                .SetNegativeButton("Cancel", (sender, args) => { })
                                .Create();

                            removeDialog.Show();
                            try
                            {
                                removeDialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(Android.Graphics.Color.ParseColor("#C2185B"));
                                removeDialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(Android.Graphics.Color.ParseColor("#6B7280"));
                            }
                            catch { }
                        };

                        row.AddView(tv);
                        row.AddView(btn);
                        list.AddView(row);
                    }
                }

                bunkerContainer.AddView(list);
            }
            catch { }
        }

        private void StartNostrConnectSession(string connectUri, EditText? nsec, bool preAuthenticated = false)
        {
            try
            {
                var nsecText = nsec?.Text?.Trim() ?? "";
                if (!nsecText.StartsWith("nsec1"))
                {
                    Toast.MakeText(this, "nsec required for nostrconnect", ToastLength.Short)?.Show();
                    return;
                }

                var (hrp, data) = Bech32Decode(nsecText);
                if (hrp != "nsec" || data == null)
                {
                    Toast.MakeText(this, "Invalid nsec", ToastLength.Short)?.Show();
                    return;
                }

                var privKey = ConvertBits(data, 5, 8, false);
                if (privKey == null || privKey.Length != 32)
                {
                    Toast.MakeText(this, "Invalid nsec", ToastLength.Short)?.Show();
                    return;
                }

                var nsecHex = BitConverter.ToString(privKey).Replace("-", "").ToLowerInvariant();

                var intent = new Intent(this, typeof(BunkerService));
                intent.SetAction("START_NOSTRCONNECT");
                intent.PutExtra("connectUri", connectUri);
                intent.PutExtra("nsecHex", nsecHex);
                intent.PutExtra("preAuthenticated", preAuthenticated);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    StartForegroundService(intent);
                else
                    StartService(intent);

                // Persist the nostrconnect URI
                PersistNostrConnectClient(connectUri);
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, "NostrConnect error: " + ex.Message, ToastLength.Short)?.Show();
            }
        }

        private void StopNostrConnectSession(string clientPubkey)
        {
            try
            {
                var intent = new Intent(this, typeof(BunkerService));
                intent.SetAction("STOP_NOSTRCONNECT");
                intent.PutExtra("clientPubkey", clientPubkey);
                StartService(intent);

                // Remove from persisted list
                RemovePersistedNostrConnectClient(clientPubkey);
            }
            catch { }
        }

        private void PersistNostrConnectClient(string uri)
        {
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var existing = prefs?.GetString(PREF_NC_CLIENTS, "") ?? "";
                var list = string.IsNullOrEmpty(existing)
                    ? new List<string>()
                    : new List<string>(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries));

                // Parse to get client pubkey for dedup
                if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                {
                    list.RemoveAll(u =>
                    {
                        if (NostrConnectUri.TryParse(u, out var p) && p != null)
                            return p.ClientPubkey == parsed.ClientPubkey;
                        return false;
                    });
                }

                list.Add(uri);
                var edit = prefs?.Edit();
                if (edit != null) { edit.PutString(PREF_NC_CLIENTS, string.Join('\n', list)); edit.Apply(); }
            }
            catch { }
        }

        private void RemovePersistedNostrConnectClient(string clientPubkey)
        {
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var existing = prefs?.GetString(PREF_NC_CLIENTS, "") ?? "";
                if (string.IsNullOrEmpty(existing)) return;

                var list = new List<string>(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                list.RemoveAll(u =>
                {
                    if (NostrConnectUri.TryParse(u, out var p) && p != null)
                        return p.ClientPubkey == clientPubkey;
                    return false;
                });

                var edit = prefs?.Edit();
                if (edit != null) { edit.PutString(PREF_NC_CLIENTS, string.Join('\n', list)); edit.Apply(); }
            }
            catch { }
        }

        private void RestoreNostrConnectClients(EditText? nsec)
        {
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var existing = prefs?.GetString(PREF_NC_CLIENTS, "") ?? "";
                if (string.IsNullOrEmpty(existing)) return;

                var list = existing.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                // Build display data for immediate UI update
                var pubkeys = new List<string>();
                var uris = new List<string>();

                foreach (var uri in list)
                {
                    if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                    {
                        pubkeys.Add(parsed.ClientPubkey);
                        uris.Add(uri);

                        // Start session if not already running
                        var activePks = BunkerService.GetActiveClientPubkeys();
                        if (!activePks.Contains(parsed.ClientPubkey))
                        {
                            StartNostrConnectSession(uri, nsec, preAuthenticated: true);
                        }
                    }
                }

                RefreshNostrConnectClientList([.. pubkeys], [.. uris]);
            }
            catch { }
        }

        private void RefreshNostrConnectClientList(string[] pubkeys, string[] uris)
        {
            _lastNcPubkeys = pubkeys;
            _lastNcUris = uris;

            if (_ncClientList == null) return;
            _ncClientList.RemoveAllViews();

            if (pubkeys.Length == 0)
            {
                if (_ncStatusText != null) _ncStatusText.Text = "nostrconnect: no sessions";
                return;
            }

            if (_ncStatusText != null)
                _ncStatusText.Text = $"nostrconnect: {pubkeys.Length} session(s)";

            // Load custom names
            var customNames = LoadNcClientNames();

            // Resolve known names from nostrconnect settings for display consistency
            var ncCustomNames = LoadNcClientNames();
            var ncMetadataNames = LoadNcMetadataNamesByPubkey();

            for (int i = 0; i < pubkeys.Length; i++)
            {
                var pk = pubkeys[i];
                var uri = i < uris.Length ? uris[i] : "";

                // Resolve display name: custom name > metadata name > pubkey
                customNames.TryGetValue(pk, out var customName);
                string? metadataName = null;
                if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                    metadataName = parsed.GetClientName();

                var displayName = !string.IsNullOrEmpty(customName)
                    ? customName
                    : !string.IsNullOrEmpty(metadataName)
                        ? metadataName
                        : null;

                var labelText = !string.IsNullOrEmpty(displayName)
                    ? $"{displayName} ({pk[..Math.Min(12, pk.Length)]}...)"
                    : $"{pk[..Math.Min(12, pk.Length)]}...";

                var row = new LinearLayout(this)
                {
                    Orientation = Orientation.Horizontal
                };
                row.SetPadding(0, 4, 0, 4);

                var label = new TextView(this)
                {
                    Text = labelText
                };
                label.PaintFlags |= Android.Graphics.PaintFlags.UnderlineText;
                var lp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                lp.Gravity = GravityFlags.CenterVertical;
                label.LayoutParameters = lp;

                // Tap label to edit client name
                var clientPk = pk;
                var currentName = displayName ?? "";
                label.Click += (s, e) => ShowEditNcClientNameDialog(clientPk, currentName);

                var removeBtn = new Button(this)
                {
                    Text = "Remove"
                };
                removeBtn.SetMinimumWidth(0);
                removeBtn.SetMinimumHeight(0);
                removeBtn.SetPadding(16, 0, 16, 0);
                removeBtn.Click += (s, e) =>
                {
                    var name = GetNcClientDisplayName(clientPk);
                    var removeDialog = new Android.App.AlertDialog.Builder(this)
                        .SetTitle("Remove client")
                        .SetMessage($"Disconnect and remove {name}?")
                        .SetPositiveButton("Remove", (sender, args) =>
                        {
                            RemoveNcClientName(clientPk);
                            StopNostrConnectSession(clientPk);
                        })
                        .SetNegativeButton("Cancel", (sender, args) => { })
                        .Create();

                    removeDialog.Show();
                    try
                    {
                        removeDialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(Android.Graphics.Color.ParseColor("#C2185B"));
                        removeDialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(Android.Graphics.Color.ParseColor("#6B7280"));
                    }
                    catch { }
                };

                row.AddView(label);
                row.AddView(removeBtn);
                _ncClientList.AddView(row);
            }
        }

        private void ShowEditNcClientNameDialog(string clientPubkey, string currentName)
        {
            var input = new EditText(this)
            {
                Text = currentName,
                Hint = "Client name"
            };
            input.SetSingleLine(true);

            var editDialog = new Android.App.AlertDialog.Builder(this)
                 .SetTitle("Edit client name")
                 .SetMessage(clientPubkey[..Math.Min(12, clientPubkey.Length)] + "...")
                 .SetView(input)
                 .SetPositiveButton("Save", (sender, args) =>
                 {
                     var name = input.Text?.Trim() ?? "";
                     SaveNcClientName(clientPubkey, name);
                     RefreshNostrConnectClientList(_lastNcPubkeys, _lastNcUris);
                     RefreshBunkerAuthorizedList();
                 })
                 .SetNegativeButton("Cancel", (sender, args) => { })
                 .Create();

            editDialog.Show();
            try
            {
                editDialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(Android.Graphics.Color.ParseColor("#C2185B"));
                editDialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(Android.Graphics.Color.ParseColor("#6B7280"));
            }
            catch { }
        }

        private string GetNcClientDisplayName(string clientPubkey)
        {
            var names = LoadNcClientNames();
            if (names.TryGetValue(clientPubkey, out var custom) && !string.IsNullOrEmpty(custom))
                return $"{custom} ({clientPubkey[..Math.Min(12, clientPubkey.Length)]}...)";
            return $"{clientPubkey[..Math.Min(12, clientPubkey.Length)]}...";
        }

        private Dictionary<string, string> LoadNcMetadataNamesByPubkey()
        {
            var result = new Dictionary<string, string>();
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var existing = prefs?.GetString(PREF_NC_CLIENTS, "") ?? "";
                if (string.IsNullOrEmpty(existing)) return result;

                foreach (var uri in existing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                    {
                        var name = parsed.GetClientName();
                        if (!string.IsNullOrEmpty(name))
                            result[parsed.ClientPubkey] = name;
                    }
                }
            }
            catch { }
            return result;
        }

        private Dictionary<string, string> LoadNcClientNames()
        {
            var result = new Dictionary<string, string>();
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var raw = prefs?.GetString(PREF_NC_NAMES, "") ?? "";
                if (string.IsNullOrEmpty(raw)) return result;
                // Simple format: pubkey=name lines
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIdx = line.IndexOf('=');
                    if (eqIdx > 0)
                        result[line[..eqIdx]] = line[(eqIdx + 1)..];
                }
            }
            catch { }
            return result;
        }

        private void SaveNcClientName(string clientPubkey, string name)
        {
            try
            {
                var names = LoadNcClientNames();
                if (string.IsNullOrEmpty(name))
                    names.Remove(clientPubkey);
                else
                    names[clientPubkey] = name;
                PersistNcClientNames(names);
            }
            catch { }
        }

        private void RemoveNcClientName(string clientPubkey)
        {
            try
            {
                var names = LoadNcClientNames();
                if (names.Remove(clientPubkey))
                    PersistNcClientNames(names);
            }
            catch { }
        }

        private void PersistNcClientNames(Dictionary<string, string> names)
        {
            var sb = new StringBuilder();
            foreach (var kvp in names)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                    sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\n');
            }
            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
            var edit = prefs?.Edit();
            if (edit != null) { edit.PutString(PREF_NC_NAMES, sb.ToString()); edit.Apply(); }
        }

        private static (string? hrp, byte[]? data) Bech32Decode(string bech)
        {
            if (string.IsNullOrEmpty(bech)) return (null, null);
            bech = bech.ToLowerInvariant();
            var pos = bech.LastIndexOf('1');
            if (pos < 1 || pos + 7 > bech.Length) return (null, null); 
            var hrp = bech[..pos];
            var dataPart = bech[(pos + 1)..];
            var data = new byte[dataPart.Length];
            for (int i = 0; i < dataPart.Length; i++)
            {
                var idx = Bech32Chars.IndexOf(dataPart[i]);
                if (idx == -1) return (null, null);
                data[i] = (byte)idx;
            }

            // verify checksum
            var hrpExpanded = HrpExpand(hrp);
            var values = new List<byte>();
            values.AddRange(hrpExpanded);
            values.AddRange(data);
            if (Polymod([.. values]) != 1) return (null, null);

            var payload = new byte[data.Length - 6];
            Array.Copy(data, 0, payload, 0, payload.Length);
            return (hrp, payload);
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

            // read ellipsis string (default " ...")
            string ellipsis = " ...";
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                ellipsis = prefs?.GetString(PREF_TRUNCATE_ELLIPSIS, ellipsis) ?? ellipsis;
            }
            catch { }

            if (replaced.Length > len)
            {
                return string.Concat(replaced.AsSpan(0, len), ellipsis);
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
            }
            catch { }
            List<string> voices = ["default"];
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
                        var ttsFallback = new TextToSpeech(this, null);
                        try { initialVoiceObjs = ttsFallback?.Voices?.Where(v => v != null).ToList(); } catch { }
                        try { ttsFallback?.Shutdown(); } catch { }
                    }
                    catch { }
                }
                if (initialVoiceObjs == null || initialVoiceObjs.Count == 0)
                {
                    try { Android.Util.Log.Info("nokandro", "No TTS voices available after polling."); } catch { }
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
                        try { Android.Util.Log.Info("nokandro", $"Found languages: codes={string.Join(',', languageCodes)} labels={string.Join(',', languageLabels)}"); } catch { }
                    }
                }

                if (voices.Count == 0) voices = ["default"];
            }
            catch
            {
                voices = ["default"];
                languageCodes = ["Any"];
                languageLabels = ["Any"];
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
                try { if (_availableLangCodes == null || _availableLangCodes.Count == 0) _availableLangCodes = ["Any"]; } catch { }
            }

            // Now filter voices by selected language code
            var selectedLang = _lastSelectedLangCode ?? "Any";
            try { Android.Util.Log.Info("nokandro", $"Filtering using selectedLang={selectedLang}"); } catch { }
            List<string> finalVoices = [];
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
                    try { Android.Util.Log.Info("nokandro", "No voice list available for inspection"); } catch { }
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
                            if (!string.IsNullOrEmpty(name))
                            {
                                try
                                {
                                    if (!string.IsNullOrEmpty(voicePrimary) && string.Equals(voicePrimary, selectedPrimary, StringComparison.OrdinalIgnoreCase))
                                    {
                                        set.Add(name);
                                    }
                                    else if (!string.IsNullOrEmpty(fmt) && fmt.StartsWith(selectedPrimary, StringComparison.OrdinalIgnoreCase))
                                    {
                                        set.Add(name);
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
                        try { Android.Util.Log.Info("nokandro", $"Matching voice names: {string.Join(',', set)}"); } catch { }
                    }
                }

                try { Android.Util.Log.Info("nokandro", $"Filtered voices count={finalVoices.Count}"); } catch { }
            }
            // Sort voice names for predictable UI order
            try
            {
                finalVoices = [.. finalVoices.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];
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
                    try { Android.Util.Log.Info("nokandro", "No voices for selected language"); } catch { }
                }
            }
            catch { }
            _langPopulating = false;
        }

        private async Task FetchRelayListAsync(EditText relayEdit, EditText npubEdit)
        {
            var npub = npubEdit.Text?.Trim();
            if (string.IsNullOrEmpty(npub))
            {
                Toast.MakeText(this, "Enter npub first", ToastLength.Short).Show();
                return;
            }
            
            string? pubkeyHex = null;
            if (CreateHex64Regex().IsMatch(npub)) pubkeyHex = npub.ToLowerInvariant();
            else 
            {
                try
                {
                   var (hrp, data) = Bech32Decode(npub);
                   if (hrp == "npub" && data != null)
                   {
                       var bytes = ConvertBits(data, 5, 8, false);
                       if (bytes != null && bytes.Length == 32)
                       {
                           pubkeyHex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                       }
                   }
                }
                catch {}
            }
            
            if (string.IsNullOrEmpty(pubkeyHex))
            {
                Toast.MakeText(this, "Invalid npub", ToastLength.Short).Show();
                return;
            }

            var dialog = new Android.App.ProgressDialog(this);
            dialog.SetMessage("Fetching relay list...");
            dialog.SetCancelable(false);
            dialog.Show();

            var currentRelay = relayEdit.Text?.Trim();
            var relaysToTry = new List<string>();
            if (!string.IsNullOrEmpty(currentRelay)) relaysToTry.Add(currentRelay);
            relaysToTry.Add("wss://relay.damus.io");
            relaysToTry.Add("wss://nos.lol");
            relaysToTry.Add("wss://relay-jp.nostr.wirednet.jp/");
            var uniqueRelays = relaysToTry.Distinct().ToList();

            HashSet<string> foundRelays = new HashSet<string>();

            await Task.Run(async () =>
            {
                foreach (var relayUrl in uniqueRelays)
                {
                    if (string.IsNullOrEmpty(relayUrl) || (!relayUrl.StartsWith("wss://") && !relayUrl.StartsWith("ws://"))) continue;

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        using var ws = new ClientWebSocket();
                        await ws.ConnectAsync(new Uri(relayUrl), cts.Token);
                        
                        var subId = "relaylist" + new Random().Next(1000);
                        var req = $"[\"REQ\",\"{subId}\",{{\"kinds\":[10002],\"authors\":[\"{pubkeyHex}\"],\"limit\":1}}]";
                        var bytes = Encoding.UTF8.GetBytes(req);
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

                        var buffer = new byte[8192];
                        while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                        {
                             var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                             var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                             
                             if (msg.Contains("EOSE")) break; 
                             if (msg.Contains("EVENT"))
                             {
                                 try 
                                 {
                                     using var doc = JsonDocument.Parse(msg);
                                     var root = doc.RootElement;
                                     if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 3 && root[0].GetString() == "EVENT")
                                     {
                                         var ev = root[2];
                                         if (ev.TryGetProperty("kind", out var k) && k.GetInt32() == 10002)
                                         {
                                             if (ev.TryGetProperty("tags", out var tags))
                                             {
                                                 foreach (var tag in tags.EnumerateArray())
                                                 {
                                                     if (tag.GetArrayLength() >= 2 && tag[0].GetString() == "r")
                                                     {
                                                         var r = tag[1].GetString();
                                                         if (!string.IsNullOrEmpty(r)) foundRelays.Add(r);
                                                     }
                                                 }
                                             }
                                             break; 
                                         }
                                     }
                                 }
                                 catch {}
                             }
                        }
                        if (foundRelays.Count > 0) break;
                    }
                    catch (Exception ex) 
                    { 
                         Android.Util.Log.Warn("nokandro", $"Fetch relay list failed on {relayUrl}: {ex.Message}");
                    }
                }
            });
            
            
            try { RunOnUiThread(() => dialog.Dismiss()); } catch {}

            if (foundRelays.Count == 0)
            {
                RunOnUiThread(() => Toast.MakeText(this, "No relay list found (kind 10002)", ToastLength.Short).Show());
                return;
            }

            var list = foundRelays.OrderBy(x => x).ToArray();
            RunOnUiThread(() =>
            {
                new Android.App.AlertDialog.Builder(this)
                    .SetTitle("Select Relay")
                    .SetItems(list, (s, e) =>
                    {
                        var selected = list[e.Which];
                        relayEdit.Text = selected;
                        try
                        {
                            var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                            prefs?.Edit()?.PutString(PREF_RELAY, selected)?.Apply();
                        } catch {}
                    })
                    .Show();
            });
        }

        private async Task CheckForUpdateAsync(TextView verTv, string currentVer)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.UserAgent.TryParseAdd("nokandro");
                client.Timeout = TimeSpan.FromSeconds(10);
                var json = await client.GetStringAsync("https://api.github.com/repos/betonetojp/nokandro/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag_name", out var tagEl))
                {
                    var latestTag = tagEl.GetString(); 
                    if (!string.IsNullOrEmpty(latestTag) && IsNewer(currentVer, latestTag))
                    {
                         RunOnUiThread(() => 
                         {
                             try 
                             { 
                                 verTv.Text = $"{currentVer} -> {latestTag}";
                                 verTv.SetTextColor(Android.Graphics.Color.ParseColor("#4A001F"));
                                 verTv.SetTypeface(verTv.Typeface, Android.Graphics.TypefaceStyle.Bold);
                             } catch { }
                         });
                    }
                }
            }
            catch { }
        }

        private static bool IsNewer(string current, string latest)
        {
             var v1 = ParseVersion(current);
             var v2 = ParseVersion(latest);
             return v2 > v1;
        }

        private static Version ParseVersion(string v)
        {
            var s = v.TrimStart('v', 'V');
            if (Version.TryParse(s, out var ver)) return ver;
            return new Version(0,0,0);
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

        // Google Code Scanner listener helpers
        class QrScanSuccessListener : Java.Lang.Object, Android.Gms.Tasks.IOnSuccessListener
        {
            private readonly Action<Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode> _action;
            public QrScanSuccessListener(Action<Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode> action) => _action = action;
            public void OnSuccess(Java.Lang.Object? result)
            {
                if (result is Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode barcode) _action(barcode);
            }
        }

        class QrScanFailureListener : Java.Lang.Object, Android.Gms.Tasks.IOnFailureListener
        {
            private readonly Action<Java.Lang.Exception> _action;
            public QrScanFailureListener(Action<Java.Lang.Exception> action) => _action = action;
            public void OnFailure(Java.Lang.Exception e) => _action(e);
        }

        private static readonly Regex UrlPattern = CreateUrlRegex();
        private static readonly Regex NpubNprofilePattern = CreateNpubNprofileRegex();
        private static readonly Regex NeventNotePattern = CreateNeventNoteRegex();
        private static readonly string[] evaluator = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".heic", ".tiff", ".ico", ".apng"];
        private static readonly string[] evaluatorArray = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".flv", ".mpeg", ".mpg", ".3gp", ".ogg", ".ogv", ".m4v", ".ts", ".m2ts", ".wmv"];

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