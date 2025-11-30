using Android.Content;
using Android.Media;
using Android.OS;
using Android.Speech.Tts;
using Android.Util;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SysText = System.Text;

namespace nokandro
{
    // Lightweight application logger that omits Debug logs unless ENABLE_LOG is defined.
    public static class AppLog
    {
        [Conditional("ENABLE_LOG")]
        public static void D(string tag, string msg) => Log.Debug(tag, msg);

        public static void W(string tag, string msg) => Log.Warn(tag, msg);
        public static void E(string tag, string msg) => Log.Error(tag, msg);
    }

    [Service]
    public partial class NostrService : Service
    {
        // indicate service running state for Activity UI
        public static bool IsRunning { get; private set; } = false;
        private const string TAG = "NostrService";
        private const int NOTIF_ID = 1001;
        private ClientWebSocket? _ws;
        private string _relay = "wss://yabu.me";
        private string _npub = string.Empty;
        private bool _allowOthers = false;
        private readonly CancellationTokenSource _cts = new();
        private HashSet<string> _followed = []; // store hex pubkeys (lowercase, 64 chars)
        private Dictionary<string, string> _petnames = [];
        private HashSet<string> _muted = []; // store muted hex pubkeys (public mute lists)
        private AudioManager _audioManager = null!;
        private TextToSpeech? _tts;
        private int _truncateLen = 20;
        private string _truncateEllipsis = " ...";
        private float _speechRate = 1.0f;
        private bool _speakPetname = false;
        private BroadcastReceiver? _localReceiver;

        // Voice selection names received from UI
        private string? _voiceFollowedName;
        private string? _voiceOtherName;

        private const string BECH32_CHARSET = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
        private static readonly uint[] BECH32_GENERATOR = [0x3b6a57b2u, 0x26508e6du, 0x1ea119fau, 0x3d4233ddu, 0x2a1462b3u];
        private const string ACTION_MUTE_UPDATE = "nokandro.ACTION_MUTE_UPDATE";

        public override void OnCreate()
        {
            IsRunning = true;
            base.OnCreate();
            AppLog.D(TAG, "OnCreate");
            // Get AudioManager safely
            var am = GetSystemService(AudioService) as AudioManager;
            if (am == null)
            {
                AppLog.W(TAG, "AudioManager not available");
                // fallback to Application.Context
                am = Android.App.Application.Context.GetSystemService(AudioService) as AudioManager;
            }
            _audioManager = am ?? throw new InvalidOperationException("AudioManager unavailable");

            // initialize TTS
            _tts = new TextToSpeech(this, null);

            // register local receiver to accept speech rate updates from Activity
            try
            {
                _localReceiver = new LocalReceiver(this);
                var filter = new IntentFilter();
                filter.AddAction("nokandro.ACTION_SET_SPEECH_RATE");
                LocalBroadcast.RegisterReceiver(_localReceiver, filter);
            }
            catch { }

            _followed = [];
            _petnames = [];
            _muted = [];

            // default: read saved preference (default false for first run)
            try
            {
                var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                _speakPetname = prefs?.GetBoolean("pref_speak_petname", false) ?? false;
            }
            catch { _speakPetname = false; }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            AppLog.D(TAG, "OnStartCommand: action=" + (intent?.Action ?? "(null)"));
            if (intent != null && intent.Action == "STOP")
            {
                // Stop requested from notification action
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            if (intent != null)
            {
                _relay = intent.GetStringExtra("relay") ?? _relay;
                _npub = intent.GetStringExtra("npub") ?? _npub;
                _allowOthers = intent.GetBooleanExtra("allowOthers", false);
                _truncateLen = intent.GetIntExtra("truncateLen", _truncateLen);

                // read voice selections
                _voiceFollowedName = intent.GetStringExtra("voiceFollowed");
                _voiceOtherName = intent.GetStringExtra("voiceOther");
                // read speech rate if provided
                try { _speechRate = intent.GetFloatExtra("speechRate", _speechRate); } catch { }
                try { _speakPetname = intent.GetBooleanExtra("speakPetname", _speakPetname); } catch { }
                try { _truncateEllipsis = intent.GetStringExtra("truncateEllipsis") ?? _truncateEllipsis; } catch { }

                // register for runtime updates to truncate ellipsis from Activity while running
                try
                {
                    if (_localReceiver == null)
                    {
                        _localReceiver = new LocalReceiver(this);
                        var f2 = new IntentFilter();
                        f2.AddAction("nokandro.ACTION_SET_TRUNCATE_ELLIPSIS");
                        LocalBroadcast.RegisterReceiver(_localReceiver, f2);
                    }
                }
                catch { }
            }

            // Compute PendingIntent flags once and reuse
            PendingIntentFlags piFlags = PendingIntentFlags.UpdateCurrent;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                // For Android 12+ require explicit immutability unless mutability is needed
                piFlags |= PendingIntentFlags.Immutable;
            }

            // Notification APIs target Android O+; analyzer warns about platform compatibility.
            // Surround these blocks with pragmas to suppress CA1416/CA1422 analyzer warnings because runtime checks exist.
#pragma warning disable CA1416, CA1422
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var chanId = "nostr_tts_channel";
                var chan = new NotificationChannel(chanId, "Nostr TTS", NotificationImportance.Low);
                if (GetSystemService(NotificationService) is NotificationManager nm)
                {
                    nm.CreateNotificationChannel(chan);
                }
                else
                {
                    AppLog.W(TAG, "NotificationManager not available");
                }

                // PendingIntent to open app when tapping notification
                var mainIntent = new Intent(this, typeof(MainActivity));
                var mainPending = PendingIntent.GetActivity(this, 0, mainIntent, piFlags);

                // PendingIntent to stop service
                var stopIntent = new Intent(this, typeof(NostrService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 0, stopIntent, piFlags);

                var notifBuilder = new Notification.Builder(this, chanId)
                    .SetContentTitle("Nostr TTS")
                    .SetContentText("Listening...")
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                // Add a Stop action (use built-in alert icon)
                try
                {
                    notifBuilder.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending);
                }
                catch (Exception ex) { AppLog.W(TAG, "AddAction failed: " + ex.Message); }

                var notif = notifBuilder.Build();
                StartForeground(NOTIF_ID, notif);
                AppLog.D(TAG, "Started foreground notification");
            }
            else
            {
                // Pre-Oreo
                var mainIntent = new Intent(this, typeof(MainActivity));
                var mainPending = PendingIntent.GetActivity(this, 0, mainIntent, piFlags);
                var stopIntent = new Intent(this, typeof(NostrService));
                stopIntent.SetAction("STOP");
                var stopPending = PendingIntent.GetService(this, 0, stopIntent, piFlags);

                var notif = new Notification.Builder(this)
                    .SetContentTitle("Nostr TTS")
                    .SetContentText("Listening...")
                    .SetSmallIcon(Resource.Mipmap.ic_launcher)
                    .SetContentIntent(mainPending)
                    .SetOngoing(true);

                try { notif.AddAction(Android.Resource.Drawable.IcDialogAlert, "Stop", stopPending); } catch (Exception ex) { AppLog.W(TAG, "AddAction failed: " + ex.Message); }

                StartForeground(NOTIF_ID, notif.Build());
                AppLog.D(TAG, "Started foreground notification (pre-O)");
            }
#pragma warning restore CA1416, CA1422


            Task.Run(() => RunAsync(_cts.Token));
            AppLog.D(TAG, "Spawned RunAsync task");

            // notify UI that service started
            try
            {
                var b = new Intent("nokandro.ACTION_SERVICE_STARTED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            AppLog.D(TAG, "OnDestroy");
            IsRunning = false;
            try
            {
                var b = new Intent("nokandro.ACTION_SERVICE_STOPPED");
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch { }
            try { if (_localReceiver != null) LocalBroadcast.UnregisterReceiver(_localReceiver); } catch { }
            _cts.Cancel();
            try
            {
                _ws?.Abort();
                _ws?.Dispose();
            }
            catch (Exception ex) { AppLog.W(TAG, "Ws cleanup failed: " + ex.Message); }
            if (_tts != null)
            {
                _tts.Stop();
                _tts.Shutdown();
            }
            base.OnDestroy();
        }

        public override IBinder? OnBind(Intent? intent) => null;

        private async Task RunAsync(CancellationToken ct)
        {
            AppLog.D(TAG, $"RunAsync start npub={_npub} relay={_relay}");
            if (string.IsNullOrEmpty(_npub))
                return;

            try
            {
                _ws = new ClientWebSocket();
                AppLog.D(TAG, "Connecting websocket...");
                await _ws.ConnectAsync(new Uri(_relay), ct);
                AppLog.D(TAG, "Websocket connected: state=" + _ws.State);

                var subId = "sub1";

                // only request events occurring from now onwards (no past history)
                var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // ensure author filter uses hex pubkey (decode npub if necessary)
                var authorFilter = (_npub ?? string.Empty).Trim();
                try
                {
                    // If a plain npub is embedded anywhere in the supplied value, extract it
                    var m = PlainNpubRegex.Match(authorFilter);
                    if (!m.Success)
                    {
                        // try to find an npub token anywhere in the string
                        var m2 = PlainNpubRegex.Match(authorFilter);
                        if (m2.Success) m = m2;
                        else
                        {
                            // also try nostr:npub1... or nostr:nprofile1... styles
                            var m3 = NpubNprofileRegex().Match(authorFilter);
                            if (m3.Success)
                            {
                                // extract inner npub if present
                                var inner = PlainNpubRegex.Match(m3.Value);
                                if (inner.Success) m = inner;
                            }
                        }
                    }
                    if (m.Success) authorFilter = m.Value;

                    if (authorFilter.StartsWith("npub", StringComparison.OrdinalIgnoreCase))
                    {
                        var dec = DecodeBech32Npub(authorFilter);
                        if (!string.IsNullOrEmpty(dec)) authorFilter = dec;
                    }
                    else
                    {
                        var norm = NormalizeHexPubkey(authorFilter);
                        if (!string.IsNullOrEmpty(norm)) authorFilter = norm;
                    }
                }
                catch (Exception ex) { AppLog.W(TAG, "Author filter normalization failed: " + ex.Message); }

                // Request kind=3 (contact list) without 'since' to fetch existing follow list from relay
                // Request kind=1 events only from now onwards (live)
                // Construct JSON strings without anonymous types to avoid reflection-required serialization
                var reqFollowJson = $"[\"REQ\",\"{subId}\",{{\"kinds\":[3],\"authors\":[\"{authorFilter}\"]}}]";
                var reqMuteJson = $"[\"REQ\",\"{subId}m\",{{\"kinds\":[10000],\"authors\":[\"{authorFilter}\"]}}]";
                var reqEventsJson = $"[\"REQ\",\"{subId}ev\",{{\"kinds\":[1],\"since\":{since}}}]";

                AppLog.D(TAG, "Sending follow request: " + reqFollowJson);
                await SendTextAsync(reqFollowJson, ct);
                AppLog.D(TAG, "Sending mute request: " + reqMuteJson);
                await SendTextAsync(reqMuteJson, ct);
                AppLog.D(TAG, "Sending events request: " + reqEventsJson);
                await SendTextAsync(reqEventsJson, ct);

                var buffer = new ArraySegment<byte>(new byte[16 * 1024]);
                var sb = new System.Text.StringBuilder();

                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult? result = null;
                    do
                    {
                        result = await _ws.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            AppLog.D(TAG, "Websocket closed by server");
                            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, ct);
                            break;
                        }

                        var chunk = SysText.Encoding.UTF8.GetString(buffer.Array!, 0, result.Count);
                        sb.Append(chunk);
                    }
                    while (!result.EndOfMessage);

                    var message = sb.ToString();
                    var preview = message != null && message.Length > 0 ? message[..Math.Min(200, message.Length)] : "(empty)";
                    AppLog.D(TAG, "Received message: " + preview);
                    if (!string.IsNullOrEmpty(message))
                    {
                        HandleRawMessage(message);
                    }
                }
            }
            catch (System.OperationCanceledException) { AppLog.D(TAG, "RunAsync cancelled"); }
            catch (Exception ex) { AppLog.E(TAG, "RunAsync failed: " + ex); }
        }

        private async Task SendTextAsync(string text, CancellationToken ct)
        {
            if (_ws == null) { AppLog.W(TAG, "SendTextAsync called but _ws is null"); return; }
            var bytes = SysText.Encoding.UTF8.GetBytes(text);
            var seg = new ArraySegment<byte>(bytes);
            try
            {
                await _ws.SendAsync(seg, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "SendTextAsync failed: " + ex.Message);
            }
        }

        private void HandleRawMessage(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 2)
                {
                    var type = root[0].GetString();
                    if (type == "EVENT")
                    {
                        var eventObj = root[2];
                        if (eventObj.TryGetProperty("kind", out var kindEl) && kindEl.GetInt32() == 3)
                        {
                            AppLog.D(TAG, "Received kind=3 event");
                            _ = UpdateFollowedFromEvent(eventObj);
                        }
                        else if (eventObj.TryGetProperty("kind", out kindEl) && kindEl.GetInt32() == 10000)
                        {
                            AppLog.D(TAG, "Received kind=10000 event (mute)");
                            _ = UpdateMutedFromEvent(eventObj);
                        }
                        else if (eventObj.TryGetProperty("kind", out kindEl) && kindEl.GetInt32() == 1)
                        {
                            AppLog.D(TAG, "Received kind=1 event");
                            HandleNoteEvent(eventObj);
                        }
                    }
                }
            }
            catch (Exception ex) { AppLog.W(TAG, "HandleRawMessage failed: " + ex.Message); }
        }

        private async Task UpdateFollowedFromEvent(JsonElement eventObj)
        {
            var newSet = new HashSet<string>();

            // First, prefer explicit "tags" array if present
            if (eventObj.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() >= 2)
                    {
                        var tagType = tag[0].GetString();
                        if (tagType == "p")
                        {
                            var pub = tag[1].GetString();
                            if (string.IsNullOrEmpty(pub)) continue;

                            var hex = pub;
                            if (pub.StartsWith("npub"))
                            {
                                var decoded = DecodeBech32Npub(pub);
                                if (!string.IsNullOrEmpty(decoded)) hex = decoded;
                                else continue;
                            }

                            hex = NormalizeHexPubkey(hex);
                            if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                        }
                    }
                }
            }
            else
            {
                // Fallback: try parsing "content" as json tags or find npub tokens
                if (eventObj.TryGetProperty("content", out var contentEl))
                {
                    var content = contentEl.GetString() ?? string.Empty;
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
                                {
                                    var candidate = el[1].GetString();
                                    if (string.IsNullOrEmpty(candidate)) continue;
                                    var hex = candidate;
                                    if (candidate.StartsWith("npub"))
                                    {
                                        var decoded = DecodeBech32Npub(candidate);
                                        if (!string.IsNullOrEmpty(decoded)) hex = decoded; else continue;
                                    }
                                    hex = NormalizeHexPubkey(hex);
                                    if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                                }
                            }
                        }
                        else
                        {
                            // plain text fallback
                            var parts = content.Split(' ', ',', '\n', '\r');
                            foreach (var p in parts)
                            {
                                var t = p.Trim();
                                if (string.IsNullOrEmpty(t)) continue;
                                if (t.StartsWith("npub"))
                                {
                                    var decoded = DecodeBech32Npub(t);
                                    if (!string.IsNullOrEmpty(decoded)) newSet.Add(decoded);
                                }
                                else
                                {
                                    var hex = NormalizeHexPubkey(t);
                                    if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.W(TAG, "Parsing content fallback failed: " + ex.Message);
                        // basic scan
                        var parts = content.Split(' ', ',', '\n', '\r');
                        foreach (var p in parts)
                        {
                            var t = p.Trim();
                            if (string.IsNullOrEmpty(t)) continue;
                            if (t.StartsWith("npub"))
                            {
                                var decoded = DecodeBech32Npub(t);
                                if (!string.IsNullOrEmpty(decoded)) newSet.Add(decoded);
                            }
                            else
                            {
                                var hex = NormalizeHexPubkey(t);
                                if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                            }
                        }
                    }
                }
            }

            // Replace follow list atomically
            _followed = newSet;
            // Also extract petnames from tags into mapping (only when tag has 4 elements: ["p", "HEX", "relay", "petname"]).
            try
            {
                if (eventObj.TryGetProperty("tags", out var tags2) && tags2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tags2.EnumerateArray())
                    {
                        try
                        {
                            if (tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() >= 2)
                            {
                                var tagType = tag[0].GetString();
                                if (tagType == "p")
                                {
                                    var pub = tag[1].GetString();
                                    if (string.IsNullOrEmpty(pub)) continue;

                                    var hex = pub;
                                    if (pub.StartsWith("npub"))
                                    {
                                        var decoded = DecodeBech32Npub(pub);
                                        if (!string.IsNullOrEmpty(decoded)) hex = decoded; else continue;
                                    }
                                    hex = NormalizeHexPubkey(hex);
                                    if (string.IsNullOrEmpty(hex)) continue;

                                    // Only accept petname if tag array has 4 elements and index 3 is non-empty.
                                    string? pet = null;
                                    if (tag.GetArrayLength() >= 4)
                                    {
                                        pet = tag[3].GetString();
                                    }

                                    if (!string.IsNullOrEmpty(pet))
                                    {
                                        _petnames[hex] = pet!;
                                        AppLog.D(TAG, $"Stored petname for {hex}: {pet}");
                                    }
                                    else
                                    {
                                        // If no 4th element, ensure any previous mapping is removed so absence means no petname
                                        // Use Remove directly and check its return value instead of ContainsKey + Remove
                                        if (_petnames.Remove(hex))
                                        {
                                            AppLog.D(TAG, $"Removed petname for {hex} (no 4th tag element)");
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            AppLog.D(TAG, $"Follow list updated: count={_followed.Count}");

            // notify UI about follow list load
            try
            {
                var b = new Intent("nokandro.ACTION_FOLLOW_UPDATE");
                b.PutExtra("followLoaded", true);
                b.PutExtra("followCount", _followed.Count);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch (Exception ex) { AppLog.W(TAG, "LocalBroadcast failed: " + ex.Message); }
        }

        private async Task UpdateMutedFromEvent(JsonElement eventObj)
        {
            var newSet = new HashSet<string>();

            // Similar parsing to UpdateFollowedFromEvent: prefer tags, fallback to content
            if (eventObj.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsEl.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() >= 2)
                    {
                        var tagType = tag[0].GetString();
                        if (tagType == "p")
                        {
                            var pub = tag[1].GetString();
                            if (string.IsNullOrEmpty(pub)) continue;

                            var hex = pub;
                            if (pub.StartsWith("npub"))
                            {
                                var decoded = DecodeBech32Npub(pub);
                                if (!string.IsNullOrEmpty(decoded)) hex = decoded; else continue;
                            }

                            hex = NormalizeHexPubkey(hex);
                            if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                        }
                    }
                }
            }
            else
            {
                if (eventObj.TryGetProperty("content", out var contentEl))
                {
                    var content = contentEl.GetString() ?? string.Empty;
                    try
                    {
                        using var doc = JsonDocument.Parse(content);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in doc.RootElement.EnumerateArray())
                            {
                                if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
                                {
                                    var candidate = el[1].GetString();
                                    if (string.IsNullOrEmpty(candidate)) continue;
                                    var hex = candidate;
                                    if (candidate.StartsWith("npub"))
                                    {
                                        var decoded = DecodeBech32Npub(candidate);
                                        if (!string.IsNullOrEmpty(decoded)) hex = decoded; else continue;
                                    }
                                    hex = NormalizeHexPubkey(hex);
                                    if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                                }
                            }
                        }
                        else
                        {
                            var parts = content.Split(' ', ',', '\n', '\r');
                            foreach (var p in parts)
                            {
                                var t = p.Trim();
                                if (string.IsNullOrEmpty(t)) continue;
                                if (t.StartsWith("npub"))
                                {
                                    var decoded = DecodeBech32Npub(t);
                                    if (!string.IsNullOrEmpty(decoded)) newSet.Add(decoded);
                                }
                                else
                                {
                                    var hex = NormalizeHexPubkey(t);
                                    if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                                }
                            }
                        }
                    }
                    catch
                    {
                        var parts = content.Split(' ', ',', '\n', '\r');
                        foreach (var p in parts)
                        {
                            var t = p.Trim();
                            if (string.IsNullOrEmpty(t)) continue;
                            if (t.StartsWith("npub"))
                            {
                                var decoded = DecodeBech32Npub(t);
                                if (!string.IsNullOrEmpty(decoded)) newSet.Add(decoded);
                            }
                            else
                            {
                                var hex = NormalizeHexPubkey(t);
                                if (!string.IsNullOrEmpty(hex)) newSet.Add(hex);
                            }
                        }
                    }
                }
            }

            _muted = newSet;
            AppLog.D(TAG, $"Mute list updated: count={_muted.Count}");

            try
            {
                var b = new Intent(ACTION_MUTE_UPDATE);
                b.PutExtra("muteLoaded", true);
                b.PutExtra("muteCount", _muted.Count);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch (Exception ex) { AppLog.W(TAG, "LocalBroadcast failed: " + ex.Message); }
        }

        private static string? NormalizeHexPubkey(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.Trim();
            if (hex.StartsWith("0x")) hex = hex[2..];
            if (hex.Length == 64 && hex.All(c => Uri.IsHexDigit(c))) return hex.ToLowerInvariant();
            return null;
        }

        private void HandleNoteEvent(JsonElement eventObj)
        {
            if (!eventObj.TryGetProperty("pubkey", out var pubkeyEl)) return;
            var pubkey = pubkeyEl.GetString() ?? string.Empty;
            pubkey = pubkey.ToLowerInvariant();
            if (!eventObj.TryGetProperty("content", out var contentEl)) return;
            var content = contentEl.GetString() ?? string.Empty;

            // respect public mute list: if the author is muted, skip
            if (_muted.Contains(pubkey))
            {
                AppLog.D(TAG, $"Skipping muted author: {pubkey}");
                return;
            }

            var isFollowed = _followed.Contains(pubkey);
            if (!isFollowed && !_allowOthers) return;

            AppLog.D(TAG, $"HandleNoteEvent pubkey={pubkey} isFollowed={isFollowed}");

            var pitch = isFollowed ? 1.2f : 0.9f;
            var speechRate = _speechRate;

            // determine petname if known
            string? petname = null;
            try { if (_petnames != null && _petnames.TryGetValue(pubkey, out var pn)) petname = pn; } catch { }
            try { AppLog.D(TAG, $"Petname lookup for {pubkey}: {(petname ?? "(none)")}"); } catch { }

            // Broadcast latest content for UI
            try
            {
                var b = new Intent("nokandro.ACTION_LAST_CONTENT");
                b.PutExtra("content", content);
                b.PutExtra("isFollowed", isFollowed);
                if (!string.IsNullOrEmpty(petname)) b.PutExtra("petname", petname);
                LocalBroadcast.SendBroadcast(this, b);
            }
            catch (Exception ex) { AppLog.W(TAG, "LocalBroadcast failed: " + ex.Message); }

            SpeakText(content, pitch, speechRate, isFollowed, petname);
        }

        private void SpeakText(string text, float pitch, float rate, bool isFollowed, string? petname)
        {
            var tts = _tts;
            if (tts == null) { AppLog.W(TAG, "TTS is null"); return; }

            // replace URLs so they are not spoken verbatim
            text = ReplaceUrlsForSpeech(text);

            // prepend petname if preference enabled and petname provided
            try
            {
                if (!string.IsNullOrEmpty(petname))
                {
                    if (_speakPetname)
                    {
                        text = petname + ": " + text;
                        AppLog.D(TAG, "Prepended petname to speech: " + petname);
                    }
                }
            }
            catch { }

            // choose voice name
            string? selectedName = isFollowed ? _voiceFollowedName : _voiceOtherName;
            if (!string.IsNullOrEmpty(selectedName))
            {
                try
                {
                    var voice = tts.Voices?.FirstOrDefault(v => v.Name == selectedName);
                    if (voice != null)
                    {
                        tts.SetVoice(voice);
                    }
                }
                catch (Exception ex) { AppLog.W(TAG, "SetVoice failed: " + ex.Message); }
            }

            tts.SetPitch(pitch);
            tts.SetSpeechRate(rate);

            try
            {
                AppLog.D(TAG, "Speaking: " + (text.Length > 100 ? string.Concat(text.AsSpan(0, 100), "...") : text));
                // Attempt to pass a per-utterance volume parameter. Engines may ignore this key.
                try
                {
                    var bundle = new Bundle();

                    // default multiplier read from shared prefs (optional setting)
                    float volMult = 1.0f;
                    try
                    {
                        var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                        // GetFloat may not exist in some bindings; use GetString fallback if necessary
                        try { volMult = prefs?.GetFloat("pref_tts_volume", 1.0f) ?? 1.0f; } catch { }
                        // also support string-based stored value
                        try
                        {
                            if (volMult == 1.0f)
                            {
                                var s = prefs?.GetString("pref_tts_volume", null);
                                if (!string.IsNullOrEmpty(s) && float.TryParse(s, out var fv)) volMult = fv;
                            }
                        }
                        catch { }
                    }
                    catch { }

                    // If other media is active, apply a modest auto-boost so TTS is more audible.
                    try
                    {
                        if (_audioManager != null && _audioManager.IsMusicActive)
                        {
                            volMult = Math.Min(volMult * 1.6f, 2.0f);
                        }
                    }
                    catch { }

                    // put volume (engine-dependent)
                    try { bundle.PutFloat(TextToSpeech.Engine.KeyParamVolume, volMult); } catch { }

                    tts.Speak(text, QueueMode.Add, bundle, Guid.NewGuid().ToString());
                }
                catch (Exception ex)
                {
                    // If per-utterance params fail, fall back to basic speak
                    AppLog.W(TAG, "Speak with bundle failed: " + ex.Message);
                    tts.Speak(text, QueueMode.Add, null, Guid.NewGuid().ToString());
                }
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "Speak failed (new API): " + ex.Message);
                // The old Speak overload is marked obsolete; suppress the warning locally while still attempting fallback.
                try
                {
#pragma warning disable CS0618
                    tts.Speak(text, QueueMode.Add, null);
#pragma warning restore CS0618
                }
                catch (Exception ex2)
                {
                    AppLog.W(TAG, "Speak failed (old API): " + ex2.Message);
                }
            }
        }

        private string ReplaceUrlsForSpeech(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var rx = UrlRegex();
            // Replace URLs with context-aware placeholders: image -> [picture], video -> [movie], else -> [URL]
            var replaced = rx.Replace(input, new MatchEvaluator(match =>
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

                try
                {
                    if (ImageExtensionRegex.IsMatch(url)) return "[picture]";
                    if (VideoExtensionRegex.IsMatch(url)) return "[movie]";
                }
                catch { }

                return "[URL]";
            }));

            // also replace nostr npub/event/note references so TTS speaks placeholders matching UI
            try
            {
                replaced = NpubNprofileRegex().Replace(replaced, "[mention]");
                replaced = NeventNoteRegex().Replace(replaced, "[quote]");
            }
            catch { }

            if (replaced.Length > _truncateLen && _truncateLen > 0)
            {
                return string.Concat(replaced.AsSpan(0, _truncateLen), _truncateEllipsis);
            }
            return replaced;
        }

        // --- Bech32 helpers for npub <-> hex pubkey ---

        private static string? DecodeBech32Npub(string bech)
        {
            try
            {
                var (hrp, data) = Bech32Decode(bech);
                if (hrp == null || data == null) return null;
                if (hrp != "npub") return null;
                var bytes = ConvertBits(data, 5, 8, false);
                if (bytes == null) return null;
                if (bytes.Length != 32) return null;
                return BytesToHex(bytes);
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "DecodeBech32Npub failed: " + ex.Message);
                return null;
            }
        }

        private static string EncodeHexToNpub(string hex)
        {
            // input hex 64 chars
            var bytes = HexStringToBytes(hex);
            var data = ConvertBits(bytes, 8, 5, true) ?? throw new ArgumentException("Invalid hex");
            var combined = Bech32Encode("npub", data);
            return combined;
        }

        private static (string? hrp, byte[]? data) Bech32Decode(string bech)
        {
            if (string.IsNullOrEmpty(bech)) return (null, null);
            bech = bech.ToLowerInvariant();
            var pos = bech.LastIndexOf('1');
            if (pos < 1 || pos + 7 > bech.Length) return (null, null); // need at least 6 checksum chars
            var hrp = bech[..pos];
            var dataPart = bech[(pos + 1)..];
            var data = new byte[dataPart.Length];
            for (int i = 0; i < dataPart.Length; i++)
            {
                var idx = BECH32_CHARSET.IndexOf(dataPart[i]);
                if (idx == -1) return (null, null);
                data[i] = (byte)idx;
            }

            // verify checksum
            var hrpExpanded = HrpExpand(hrp);
            var values = new List<uint>();
            foreach (var v in hrpExpanded) values.Add(v);
            foreach (var d in data) values.Add(d);
            if (Bech32Polymod([.. values]) != 1u) return (null, null);

            // strip checksum (last 6)
            var payload = new byte[data.Length - 6];
            Array.Copy(data, 0, payload, 0, payload.Length);
            return (hrp, payload);
        }

        private static string Bech32Encode(string hrp, byte[] data)
        {
            var combined = new List<byte>(data);
            var checksum = CreateChecksum(hrp, data);
            combined.AddRange(checksum);
            var sb = new System.Text.StringBuilder();
            sb.Append(hrp);
            sb.Append('1');
            foreach (var b in combined)
            {
                sb.Append(BECH32_CHARSET[b]);
            }
            return sb.ToString();
        }

        private static byte[] CreateChecksum(string hrp, byte[] data)
        {
            var hrpExp = HrpExpand(hrp);
            var values = new List<uint>();
            foreach (var v in hrpExp) values.Add(v);
            foreach (var d in data) values.Add(d);
            for (int i = 0; i < 6; i++) values.Add(0);
            var polymod = Bech32Polymod([.. values]) ^ 1u;
            var checksum = new byte[6];
            for (int i = 0; i < 6; i++) checksum[i] = (byte)((polymod >> (5 * (5 - i))) & 0x1fu);
            return checksum;
        }

        private static uint[] HrpExpand(string hrp)
        {
            var exp = new List<uint>();
            foreach (var c in hrp) exp.Add((uint)(c >> 5));
            exp.Add(0);
            foreach (var c in hrp) exp.Add((uint)(c & 31));
            return [.. exp];
        }

        private static uint Bech32Polymod(uint[] values)
        {
            uint chk = 1;
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffffu) << 5) ^ v;
                for (int i = 0; i < 5; i++)
                {
                    if (((top >> i) & 1) == 1) chk ^= BECH32_GENERATOR[i];
                }
            }
            return chk;
        }

        private static byte[]? ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            int acc = 0;
            int bits = 0;
            var maxv = (1 << toBits) - 1;
            var result = new List<byte>();
            foreach (var value in data)
            {
                if ((value >> fromBits) != 0) return null;
                acc = (acc << fromBits) | value;
                bits += fromBits;
                while (bits >= toBits)
                {
                    bits -= toBits;
                    result.Add((byte)((acc >> bits) & maxv));
                }
            }
            if (pad)
            {
                if (bits > 0)
                {
                    result.Add((byte)((acc << (toBits - bits)) & maxv));
                }
            }
            else
            {
                if (bits >= fromBits) return null;
                if (((acc << (toBits - bits)) & maxv) != 0) return null;
            }
            return [.. result];
        }

        private static byte[]? ConvertBits(IEnumerable<byte> data, int fromBits, int toBits, bool pad)
        {
            return ConvertBits([.. data], fromBits, toBits, pad);
        }

        private static byte[] HexStringToBytes(string hex)
        {
            var clean = hex.Trim();
            if (clean.StartsWith("0x")) clean = clean[2..];
            var bytes = new byte[clean.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        private class LocalReceiver(NostrService service) : BroadcastReceiver
        {
            private readonly NostrService _service = service;

            public override void OnReceive(Context? context, Intent? intent)
            {
                try
                {
                    if (intent == null) return;
                    if ("nokandro.ACTION_SET_SPEECH_RATE".Equals(intent.Action))
                    {
                        var rate = intent.GetFloatExtra("speechRate", 1.0f);
                        _service._speechRate = rate;
                        _service._tts?.SetSpeechRate(rate);
                        AppLog.D(TAG, "Speech rate updated: " + rate);
                        return;
                    }
                    if ("nokandro.ACTION_SET_TRUNCATE_ELLIPSIS".Equals(intent.Action))
                    {
                        var ell = intent.GetStringExtra("truncateEllipsis") ?? string.Empty;
                        if (!string.IsNullOrEmpty(ell)) _service._truncateEllipsis = ell;
                        AppLog.D(TAG, "Truncate ellipsis updated: " + ell);
                        return;
                    }
                }
                catch (Exception ex) { AppLog.W(TAG, "LocalReceiver.OnReceive error: " + ex.Message); }
            }
        }


        // Fallback compiled regexes for extensions used in speech replacement
        private static readonly Regex ImageExtensionRegex = CreateImageExtensionRegex();
        private static readonly Regex VideoExtensionRegex = CreateVideoExtensionRegex();
        private static readonly Regex PlainNpubRegex = CreatePlainNpubRegex();
        private static readonly string[] evaluator = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".heic", ".tiff", ".ico", ".apng"];
        private static readonly string[] evaluatorArray = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".flv", ".mpeg", ".mpg", ".3gp", ".ogg", ".ogv", ".m4v", ".ts", ".m2ts", ".wmv"];

        [GeneratedRegex("(https?://\\S+|www\\.\\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex UrlRegex();
        [GeneratedRegex("\\.(jpg|jpeg|png|gif|webp|bmp|svg|heic|tiff|ico|apng)(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateImageExtensionRegex();
        [GeneratedRegex("\\.(mp4|mov|webm|mkv|avi|flv|mpeg|mpg|3gp|ogg|ogv|m4v|ts|m2ts|wmv)(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreateVideoExtensionRegex();
        [GeneratedRegex("\\bnostr:(?:npub1|nprofile1)\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex NpubNprofileRegex();
        [GeneratedRegex("\\bnostr:(?:nevent1|note1)\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex NeventNoteRegex();
        [GeneratedRegex("npub1[0-9a-zA-Z]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
        private static partial Regex CreatePlainNpubRegex();
    }
}
