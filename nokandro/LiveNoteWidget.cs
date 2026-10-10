using System;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace nokandro
{
    [BroadcastReceiver(Label = "Nostr Live Note", Exported = true)]
    [IntentFilter(new string[] {
        "android.appwidget.action.APPWIDGET_UPDATE",
        "nokandro.ACTION_UPDATE_LIVE_NOTE",
        "nokandro.ACTION_REACT_STAR",
        "nokandro.ACTION_PREV_NOTE",
        "nokandro.ACTION_NEXT_NOTE",
        "nokandro.ACTION_TOGGLE_SERVICE"
    })]
    [MetaData("android.appwidget.provider", Resource = "@xml/widget_live_note_info")]
    public class LiveNoteWidget : AppWidgetProvider
    {
        public const string ACTION_UPDATE_LIVE_NOTE = "nokandro.ACTION_UPDATE_LIVE_NOTE";
        public const string ACTION_REACT_STAR = "nokandro.ACTION_REACT_STAR";
        public const string ACTION_PREV_NOTE = "nokandro.ACTION_PREV_NOTE";
        public const string ACTION_NEXT_NOTE = "nokandro.ACTION_NEXT_NOTE";
        public const string ACTION_TOGGLE_SERVICE = "nokandro.ACTION_TOGGLE_SERVICE";

        public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
        {
            if (context == null || appWidgetManager == null || appWidgetIds == null) return;
            UpdateAllWidgets(context, appWidgetManager, appWidgetIds);
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            base.OnReceive(context, intent);
            if (context == null || intent == null) return;

            var action = intent.Action;
            if (string.IsNullOrEmpty(action)) return;

            var mgr = AppWidgetManager.GetInstance(context);
            var ids = mgr?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(LiveNoteWidget))));
            if (mgr == null || ids == null || ids.Length == 0) return;

            if (ACTION_UPDATE_LIVE_NOTE.Equals(action) || Intent.ActionBootCompleted?.Equals(action) == true)
            {
                UpdateAllWidgets(context, mgr, ids);
            }
            else if (ACTION_PREV_NOTE.Equals(action))
            {
                LiveNoteStore.StepPrev(context);
                UpdateAllWidgets(context, mgr, ids);
            }
            else if (ACTION_NEXT_NOTE.Equals(action))
            {
                LiveNoteStore.StepNext(context);
                UpdateAllWidgets(context, mgr, ids);
            }
            else if (ACTION_REACT_STAR.Equals(action))
            {
                var eventId = intent.GetStringExtra("eventId");
                var authorPubkey = intent.GetStringExtra("authorPubkey");
                if (!string.IsNullOrEmpty(eventId) && !string.IsNullOrEmpty(authorPubkey))
                {
                    if (LiveNoteStore.IsReacted(context, eventId))
                    {
                        ShowToast(context, "Already reacted to this post");
                        return;
                    }

                    LiveNoteStore.MarkReacted(context, eventId);
                    UpdateAllWidgets(context, mgr, ids);

                    Task.Run(async () =>
                    {
                        await SendReactionAsync(context, eventId, authorPubkey);
                    });
                }
            }
            else if (ACTION_TOGGLE_SERVICE.Equals(action))
            {
                ToggleService(context);
                UpdateAllWidgets(context, mgr, ids);
            }
        }

        public static void UpdateAllWidgets(Context context)
        {
            try
            {
                var mgr = AppWidgetManager.GetInstance(context);
                var ids = mgr?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(LiveNoteWidget))));
                if (mgr != null && ids != null && ids.Length > 0)
                {
                    UpdateAllWidgets(context, mgr, ids);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LiveNoteWidget", $"UpdateAllWidgets failed: {ex.Message}");
            }
        }

        private static void UpdateAllWidgets(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds)
        {
            var density = context.Resources?.DisplayMetrics?.Density ?? 2.0f;
            var prefs = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
            var relayUrl = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");

            var currentPost = LiveNoteStore.GetCurrentPost(context);

            foreach (var widgetId in appWidgetIds)
            {
                var remoteViews = new RemoteViews(context.PackageName, Resource.Layout.widget_live_note);

                // 1. App branding & Service Toggle (Icon)
                var intentToggle = new Intent(context, typeof(LiveNoteWidget));
                intentToggle.SetAction(ACTION_TOGGLE_SERVICE);
                var pendingIntentToggle = PendingIntent.GetBroadcast(context, 10, intentToggle, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteAppIcon, pendingIntentToggle);

                // Title click opens Main App
                var intentApp = new Intent(context, typeof(MainActivity));
                intentApp.SetFlags(ActivityFlags.NewTask);
                var pendingIntentApp = PendingIntent.GetActivity(context, 11, intentApp, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteAppTitle, pendingIntentApp);

                // 2. Quick Post Launch
                var intentQuickPost = new Intent(context, typeof(QuickPostActivity));
                intentQuickPost.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                var pendingIntentQuickPost = PendingIntent.GetActivity(context, 12, intentQuickPost, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteQuickPostBtn, pendingIntentQuickPost);

                // 3. Navigation Controls (< / >) and Content Area Left/Right Touch Targets
                var intentPrev = new Intent(context, typeof(LiveNoteWidget));
                intentPrev.SetAction(ACTION_PREV_NOTE);
                var piPrev = PendingIntent.GetBroadcast(context, 13, intentPrev, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNotePrevBtn, piPrev);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteTouchPrev, piPrev);

                var intentNext = new Intent(context, typeof(LiveNoteWidget));
                intentNext.SetAction(ACTION_NEXT_NOTE);
                var piNext = PendingIntent.GetBroadcast(context, 14, intentNext, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteNextBtn, piNext);
                remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteTouchNext, piNext);

                if (currentPost != null)
                {
                    // Avatar & Petname Pill composite
                    var avatarBmp = LiveNoteAvatarHelper.GetOrCreateAvatar(context, currentPost.Pubkey, currentPost.Petname, density);
                    if (avatarBmp != null)
                    {
                        remoteViews.SetImageViewBitmap(Resource.Id.liveNoteAvatar, avatarBmp);
                    }

                    // Trigger avatar download in background if not yet cached
                    LiveNoteAvatarHelper.TriggerAvatarDownloadIfNeeded(context, currentPost.Pubkey, relayUrl, () =>
                    {
                        UpdateAllWidgets(context);
                    });

                    // Text & Navigation
                    var displayText = ShortenUrls(currentPost.Content);
                    remoteViews.SetTextViewText(Resource.Id.liveNoteText, displayText);

                    var count = LiveNoteStore.Count;
                    var idx = LiveNoteStore.CurrentIndex + 1;
                    remoteViews.SetTextViewText(Resource.Id.liveNoteNavIndex, $"{idx}/{count}");

                    // Relative timestamp
                    var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - currentPost.CreatedAt;
                    var timeStr = elapsed < 60 ? "Just now" : elapsed < 3600 ? $"{elapsed / 60}m ago" : $"{elapsed / 3600}h ago";
                    remoteViews.SetTextViewText(Resource.Id.liveNoteTime, timeStr);

                    // Star Reaction Button
                    remoteViews.SetViewVisibility(Resource.Id.liveNoteStarBtn, ViewStates.Visible);
                    if (currentPost.IsReacted)
                    {
                        remoteViews.SetTextViewText(Resource.Id.liveNoteStarBtn, "★ Reacted");
                        try
                        {
                            var goldColor = new Android.Graphics.Color(context.GetColor(Resource.Color.update_available));
                            remoteViews.SetTextColor(Resource.Id.liveNoteStarBtn, goldColor);
                        }
                        catch { }
                    }
                    else
                    {
                        remoteViews.SetTextViewText(Resource.Id.liveNoteStarBtn, "☆ React");
                        try
                        {
                            var defaultColor = new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface));
                            remoteViews.SetTextColor(Resource.Id.liveNoteStarBtn, defaultColor);
                        }
                        catch { }
                    }

                    var reactIntent = new Intent(context, typeof(LiveNoteWidget));
                    reactIntent.SetAction(ACTION_REACT_STAR);
                    reactIntent.PutExtra("eventId", currentPost.Id);
                    reactIntent.PutExtra("authorPubkey", currentPost.Pubkey);
                    var piReact = PendingIntent.GetBroadcast(context, 100 + widgetId, reactIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    remoteViews.SetOnClickPendingIntent(Resource.Id.liveNoteStarBtn, piReact);
                }
                else
                {
                    // Placeholder state
                    remoteViews.SetImageViewResource(Resource.Id.liveNoteAvatar, Resource.Mipmap.ic_launcher);
                    remoteViews.SetTextViewText(Resource.Id.liveNoteText, NostrService.IsRunning ? "Listening for posts on Nostr..." : "nokandro service is stopped");
                    remoteViews.SetTextViewText(Resource.Id.liveNoteNavIndex, "");
                    remoteViews.SetTextViewText(Resource.Id.liveNoteTime, "");
                    remoteViews.SetViewVisibility(Resource.Id.liveNoteStarBtn, ViewStates.Gone);
                }

                appWidgetManager.UpdateAppWidget(widgetId, remoteViews);
            }
        }

        private static void ToggleService(Context context)
        {
            if (NostrService.IsRunning)
            {
                var stopIntent = new Intent(context, typeof(NostrService));
                stopIntent.SetAction("STOP");
                context.StartService(stopIntent);
            }
            else
            {
                var startIntent = new Intent(context, typeof(NostrService));
                var prefs = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                if (prefs != null)
                {
                    var relay = prefs.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");
                    var npub = prefs.GetString("pref_npub", "");
                    var nsec = SecurePreferences.GetNsec(context);
                    var allowOthers = prefs.GetBoolean("pref_allow_others", false);
                    var voiceFollowed = prefs.GetString("pref_voice_followed", null);
                    var voiceOther = prefs.GetString("pref_voice_other", null);
                    var speechRate = prefs.GetFloat("pref_speech_rate", 1.0f);
                    var musicStatus = prefs.GetBoolean("pref_music_status", false);
                    var enableTts = prefs.GetBoolean("pref_enable_tts", true);
                    var autoStop = prefs.GetBoolean("pref_auto_stop", true);
                    var skipContentWarning = prefs.GetBoolean("pref_skip_cw", false);
                    var speakPetname = prefs.GetBoolean("pref_speak_petname", false);
                    var truncateLen = prefs.GetInt("pref_truncate_len", 50);
                    var truncateEllipsis = prefs.GetString("pref_truncate_ellipsis", " ...");

                    startIntent.PutExtra("relay", relay);
                    startIntent.PutExtra("npub", npub);
                    startIntent.PutExtra("nsec", nsec);
                    startIntent.PutExtra("allowOthers", allowOthers);
                    startIntent.PutExtra("voiceFollowed", voiceFollowed);
                    startIntent.PutExtra("voiceOther", voiceOther);
                    startIntent.PutExtra("speechRate", speechRate);
                    startIntent.PutExtra("enableMusicStatus", musicStatus);
                    startIntent.PutExtra("enableTts", enableTts);
                    startIntent.PutExtra("autoStop", autoStop);
                    startIntent.PutExtra("skipContentWarning", skipContentWarning);
                    startIntent.PutExtra("speakPetname", speakPetname);
                    startIntent.PutExtra("truncateLen", truncateLen);
                    startIntent.PutExtra("truncateEllipsis", truncateEllipsis);

                    var offTimerEnabled = prefs.GetBoolean("pref_off_timer_enabled", false);
                    var offTimerMinutes = prefs.GetInt("pref_off_timer_minutes", 60);
                    startIntent.PutExtra("offTimerMinutes", offTimerEnabled ? offTimerMinutes : 0);
                    startIntent.PutExtra("offTimerDisableOnEnd", prefs.GetBoolean("pref_off_timer_disable_on_end", false));

                    if (!string.IsNullOrEmpty(npub) || !string.IsNullOrEmpty(nsec))
                    {
                        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                            context.StartForegroundService(startIntent);
                        else
                            context.StartService(startIntent);
                    }
                    else
                    {
                        Toast.MakeText(context, "Please configure in app first", ToastLength.Short)?.Show();
                        var appIntent = new Intent(context, typeof(MainActivity));
                        appIntent.SetFlags(ActivityFlags.NewTask);
                        context.StartActivity(appIntent);
                    }
                }
            }
        }

        private static async Task SendReactionAsync(Context context, string eventId, string authorPubkey)
        {
            var nsec = SecurePreferences.GetNsec(context);
            if (string.IsNullOrEmpty(nsec))
            {
                ShowToast(context, "Please set nsec in nokandro first");
                return;
            }

            byte[]? privKey = null;
            try
            {
                var (hrp, data) = NostrKeyDecoder.Bech32Decode(nsec);
                if (hrp == "nsec" && data != null)
                {
                    privKey = NostrKeyDecoder.ConvertBits(data, 5, 8, false);
                }
            }
            catch { }

            if (privKey == null || privKey.Length != 32)
            {
                ShowToast(context, "Invalid nsec configuration");
                return;
            }

            var prefs = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
            var relay = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(relay ?? "wss://yabu.me"), cts.Token).ConfigureAwait(false);

                var pubkeyBytes = NostrCrypto.GetPublicKey(privKey);
                var pubkeyHex = BitConverter.ToString(pubkeyBytes).Replace("-", "").ToLowerInvariant();
                var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var contentJson = "\"+ \"".Trim(); // "+"
                contentJson = "\"+\"";
                var tagsJson = $"[[\"e\",\"{eventId}\"],[\"p\",\"{authorPubkey}\"],[\"k\",\"1\"]]";
                var rawEvent = $"[0,\"{pubkeyHex}\",{createdAt},7,{tagsJson},{contentJson}]";

                using var sha = SHA256.Create();
                var eventIdBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawEvent));
                var sigBytes = NostrCrypto.Sign(eventIdBytes, privKey);
                var idHex = BitConverter.ToString(eventIdBytes).Replace("-", "").ToLowerInvariant();
                var sigHex = BitConverter.ToString(sigBytes).Replace("-", "").ToLowerInvariant();

                var signedJson = $"{{\"kind\":7,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{pubkeyHex}\",\"id\":\"{idHex}\",\"sig\":\"{sigHex}\"}}";
                var msg = $"[\"EVENT\",{signedJson}]";

                await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);

                try
                {
                    var buffer = new byte[4096];
                    await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                }
                catch { }

                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", cts.Token).ConfigureAwait(false);

                ShowToast(context, "Reacted (+)");
            }
            catch (Exception ex)
            {
                ShowToast(context, "Reaction failed: " + ex.Message);
            }
        }

        private static void ShowToast(Context context, string message)
        {
            try
            {
                var mainHandler = new Handler(Looper.MainLooper!);
                mainHandler.Post(() =>
                {
                    Toast.MakeText(context, message, ToastLength.Short)?.Show();
                });
            }
            catch { }
        }

        private static string ShortenUrls(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return Regex.Replace(text, @"https?://[^\s]+", match =>
            {
                var url = match.Value;
                try
                {
                    var uri = new Uri(url);
                    return $"[{uri.Host}]";
                }
                catch
                {
                    return "[link]";
                }
            });
        }
    }
}
