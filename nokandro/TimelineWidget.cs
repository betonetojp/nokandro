using System;
using System.Collections.Generic;
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
    [BroadcastReceiver(Label = "Nostr Timeline", Exported = true)]
    [IntentFilter(new string[] {
        "android.appwidget.action.APPWIDGET_UPDATE",
        "nokandro.ACTION_UPDATE_TIMELINE",
        "nokandro.ACTION_TIMELINE_ITEM_CLICK",
        "nokandro.ACTION_TIMELINE_TOGGLE_TTS",
        "nokandro.ACTION_TIMELINE_TOGGLE_OFF_TIMER",
        "nokandro.ACTION_TIMELINE_TOGGLE_SERVICE"
    })]
    [MetaData("android.appwidget.provider", Resource = "@xml/widget_timeline_info")]
    public class TimelineWidget : AppWidgetProvider
    {
        public const string ACTION_UPDATE_TIMELINE = "nokandro.ACTION_UPDATE_TIMELINE";
        public const string ACTION_ITEM_CLICK = "nokandro.ACTION_TIMELINE_ITEM_CLICK";
        public const string ACTION_TOGGLE_TTS = "nokandro.ACTION_TIMELINE_TOGGLE_TTS";
        public const string ACTION_TOGGLE_OFF_TIMER = "nokandro.ACTION_TIMELINE_TOGGLE_OFF_TIMER";
        public const string ACTION_TOGGLE_SERVICE = "nokandro.ACTION_TIMELINE_TOGGLE_SERVICE";

        private static int _avatarRefreshScheduled;

        public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
        {
            if (context == null || appWidgetManager == null || appWidgetIds == null) return;
            UpdateAllWidgets(context, appWidgetManager, appWidgetIds, notifyDataChanged: true);
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            base.OnReceive(context, intent);
            if (context == null || intent == null) return;

            var action = intent.Action;
            if (string.IsNullOrEmpty(action)) return;

            var mgr = AppWidgetManager.GetInstance(context);
            var ids = mgr?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(TimelineWidget))));
            if (mgr == null || ids == null || ids.Length == 0) return;

            if (ACTION_UPDATE_TIMELINE.Equals(action) || Intent.ActionBootCompleted?.Equals(action) == true)
            {
                UpdateAllWidgets(context, mgr, ids, notifyDataChanged: true);
            }
            else if (ACTION_ITEM_CLICK.Equals(action))
            {
                var itemAction = intent.GetStringExtra("itemAction") ?? "open";
                var eventId = intent.GetStringExtra("eventId");
                var authorPubkey = intent.GetStringExtra("authorPubkey");

                if (itemAction == "react")
                {
                    if (!string.IsNullOrEmpty(eventId) && !string.IsNullOrEmpty(authorPubkey))
                    {
                        if (LiveNoteStore.IsReacted(context, eventId))
                        {
                            LiveNoteWidget.ShowToast(context, "Already reacted to this post");
                            return;
                        }

                        LiveNoteStore.MarkReacted(context, eventId);
                        UpdateAllWidgets(context, mgr, ids, notifyDataChanged: true);
                        LiveNoteWidget.UpdateAllWidgets(context);

                        Task.Run(async () =>
                        {
                            await LiveNoteWidget.SendReactionAsync(context, eventId, authorPubkey).ConfigureAwait(false);
                        });
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(eventId))
                    {
                        var prefs = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                        var defaultRelay = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");
                        var relay = intent.GetStringExtra("relay");
                        var effectiveRelay = !string.IsNullOrWhiteSpace(relay) ? relay : defaultRelay;
                        var kind = intent.GetIntExtra("kind", 1);
                        if (kind <= 0) kind = 1;

                        var bech32Ref = NostrKeyDecoder.EncodeNevent(eventId, effectiveRelay, authorPubkey, kind)
                            ?? NostrKeyDecoder.EncodeHexToNote(eventId);
                        if (!string.IsNullOrEmpty(bech32Ref))
                        {
                            try
                            {
                                var openUrl = $"https://nokakoi.com/app/{bech32Ref}";
                                var openIntent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(openUrl));
                                openIntent.SetFlags(ActivityFlags.NewTask);
                                context.StartActivity(openIntent);
                            }
                            catch { }
                        }
                    }
                }
            }
            else if (ACTION_TOGGLE_TTS.Equals(action))
            {
                if (NostrService.IsRunning)
                {
                    var nextTts = !NostrService.IsTtsEnabled;
                    var ttsIntent = new Intent(context, typeof(NostrService));
                    ttsIntent.SetAction(NostrService.ACTION_TOGGLE_TTS);
                    context.StartService(ttsIntent);
                    UpdateAllWidgets(context, mgr, ids, notifyDataChanged: false, overrideTts: nextTts);
                }
                else
                {
                    var p = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                    var cur = p?.GetBoolean("pref_enable_tts", true) ?? true;
                    p?.Edit()?.PutBoolean("pref_enable_tts", !cur)?.Apply();
                    UpdateAllWidgets(context, mgr, ids, notifyDataChanged: false);
                    LiveNoteWidget.UpdateAllWidgets(context);
                }
            }
            else if (ACTION_TOGGLE_OFF_TIMER.Equals(action))
            {
                if (NostrService.IsRunning)
                {
                    var nextOffTimer = !NostrService.IsOffTimerActive;
                    var timerIntent = new Intent(context, typeof(NostrService));
                    timerIntent.SetAction(NostrService.ACTION_TOGGLE_OFF_TIMER);
                    context.StartService(timerIntent);
                    UpdateAllWidgets(context, mgr, ids, notifyDataChanged: false, overrideOffTimer: nextOffTimer);
                }
                else
                {
                    var p = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                    var cur = p?.GetBoolean("pref_off_timer_enabled", false) ?? false;
                    p?.Edit()?.PutBoolean("pref_off_timer_enabled", !cur)?.Apply();
                    UpdateAllWidgets(context, mgr, ids, notifyDataChanged: false);
                    LiveNoteWidget.UpdateAllWidgets(context);
                }
            }
            else if (ACTION_TOGGLE_SERVICE.Equals(action))
            {
                var nextRunning = !NostrService.IsRunning;
                LiveNoteWidget.ToggleService(context);
                UpdateAllWidgets(context, mgr, ids, notifyDataChanged: true, overrideRunning: nextRunning);
                LiveNoteWidget.UpdateAllWidgets(context);
            }
        }

        public static void UpdateAllWidgets(Context context, bool notifyDataChanged = true)
        {
            try
            {
                var mgr = AppWidgetManager.GetInstance(context);
                var ids = mgr?.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(TimelineWidget))));
                if (mgr != null && ids != null && ids.Length > 0)
                {
                    UpdateAllWidgets(context, mgr, ids, notifyDataChanged);
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("TimelineWidget", $"UpdateAllWidgets failed: {ex.Message}");
            }
        }

        internal static void ScheduleDebouncedDataRefresh(Context context)
        {
            if (Interlocked.Exchange(ref _avatarRefreshScheduled, 1) != 0)
                return;

            var appCtx = context.ApplicationContext ?? context;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(700).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _avatarRefreshScheduled, 0);
                }

                try
                {
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    {
                        UpdateAllWidgets(appCtx, notifyDataChanged: true);
                    }
                    else
                    {
                        var mgr = AppWidgetManager.GetInstance(appCtx);
                        var ids = mgr?.GetAppWidgetIds(new ComponentName(appCtx, Java.Lang.Class.FromType(typeof(TimelineWidget))));
                        if (mgr != null && ids != null && ids.Length > 0)
                        {
#pragma warning disable CA1422
                            mgr.NotifyAppWidgetViewDataChanged(ids, Resource.Id.timelineListView);
#pragma warning restore CA1422
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("TimelineWidget", $"ScheduleDebouncedDataRefresh failed: {ex.Message}");
                }
            });
        }

        private static void UpdateAllWidgets(
            Context context,
            AppWidgetManager appWidgetManager,
            int[] appWidgetIds,
            bool notifyDataChanged,
            bool? overrideRunning = null,
            bool? overrideTts = null,
            bool? overrideOffTimer = null)
        {
            var prefs = context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
            var defaultRelay = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");
            var density = context.Resources?.DisplayMetrics?.Density ?? 2.0f;

            bool isRunning = overrideRunning ?? NostrService.IsRunning;

            bool isTts = overrideTts ?? (isRunning && !overrideRunning.HasValue
                ? NostrService.IsTtsEnabled
                : (prefs?.GetBoolean("pref_enable_tts", true) ?? true));

            bool isOffTimer = overrideOffTimer ?? (isRunning && !overrideRunning.HasValue
                ? NostrService.IsOffTimerActive
                : (prefs?.GetBoolean("pref_off_timer_enabled", false) ?? false));

            var immutableFlags = PendingIntentFlags.UpdateCurrent;
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                immutableFlags |= PendingIntentFlags.Immutable;
#pragma warning restore CA1416

            var templateFlags = PendingIntentFlags.UpdateCurrent;
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                templateFlags |= PendingIntentFlags.Mutable;
#pragma warning restore CA1416

            // On Android 12+ (API 31+), build RemoteCollectionItems directly so header and list
            // update atomically in a single UpdateAppWidget call (avoids Android 16's legacy
            // RemoteViewsService -> RemoteCollectionItems conversion glitches and stale bitmaps).
            RemoteViews.RemoteCollectionItems? collectionItems = null;
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            {
                collectionItems = BuildRemoteCollectionItems(context, defaultRelay, isRunning, density);
            }
#pragma warning restore CA1416

            foreach (var widgetId in appWidgetIds)
            {
                var remoteViews = new RemoteViews(context.PackageName, Resource.Layout.widget_timeline);

                // 1. Service Toggle (Icon)
                var intentToggle = new Intent(context, typeof(TimelineWidget));
                intentToggle.SetAction(ACTION_TOGGLE_SERVICE);
                var piToggle = PendingIntent.GetBroadcast(context, 310, intentToggle, immutableFlags);
                remoteViews.SetOnClickPendingIntent(Resource.Id.timelineAppIcon, piToggle);

                if (isRunning)
                {
                    remoteViews.SetImageViewResource(Resource.Id.timelineAppIcon, Resource.Mipmap.ic_launcher);
                }
                else
                {
                    var grayIcon = WidgetIconHelper.GetGrayscaleLauncherIcon(context);
                    if (grayIcon != null)
                        remoteViews.SetImageViewBitmap(Resource.Id.timelineAppIcon, grayIcon);
                    else
                        remoteViews.SetImageViewResource(Resource.Id.timelineAppIcon, Resource.Mipmap.ic_launcher);
                }

                // 2. Title opens MainActivity
                var intentApp = new Intent(context, typeof(MainActivity));
                intentApp.SetFlags(ActivityFlags.NewTask);
                var piApp = PendingIntent.GetActivity(context, 311, intentApp, immutableFlags);
                remoteViews.SetOnClickPendingIntent(Resource.Id.timelineAppTitle, piApp);

                // 3. TTS & Timer chips (visible when running)
                if (isRunning)
                {
                    remoteViews.SetViewVisibility(Resource.Id.timelineTtsBtn, ViewStates.Visible);
                    remoteViews.SetTextViewText(Resource.Id.timelineTtsBtn, isTts ? "TTS" : "Muted");
                    try
                    {
                        var ttsColor = isTts
                            ? new Android.Graphics.Color(context.GetColor(Resource.Color.primary))
                            : new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface_variant));
                        remoteViews.SetTextColor(Resource.Id.timelineTtsBtn, ttsColor);
                    }
                    catch { }

                    var intentTts = new Intent(context, typeof(TimelineWidget));
                    intentTts.SetAction(ACTION_TOGGLE_TTS);
                    var piTts = PendingIntent.GetBroadcast(context, 312, intentTts, immutableFlags);
                    remoteViews.SetOnClickPendingIntent(Resource.Id.timelineTtsBtn, piTts);

                    remoteViews.SetViewVisibility(Resource.Id.timelineTimerBtn, ViewStates.Visible);
                    remoteViews.SetTextViewText(Resource.Id.timelineTimerBtn, "Timer");
                    try
                    {
                        var timerColor = isOffTimer
                            ? new Android.Graphics.Color(context.GetColor(Resource.Color.primary))
                            : new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface_variant));
                        remoteViews.SetTextColor(Resource.Id.timelineTimerBtn, timerColor);
                    }
                    catch { }

                    var intentTimer = new Intent(context, typeof(TimelineWidget));
                    intentTimer.SetAction(ACTION_TOGGLE_OFF_TIMER);
                    var piTimer = PendingIntent.GetBroadcast(context, 313, intentTimer, immutableFlags);
                    remoteViews.SetOnClickPendingIntent(Resource.Id.timelineTimerBtn, piTimer);
                }
                else
                {
                    remoteViews.SetViewVisibility(Resource.Id.timelineTtsBtn, ViewStates.Gone);
                    remoteViews.SetViewVisibility(Resource.Id.timelineTimerBtn, ViewStates.Gone);
                }

                // 4. Quick Post button
                var intentQuickPost = new Intent(context, typeof(QuickPostActivity));
                intentQuickPost.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                var piQuickPost = PendingIntent.GetActivity(context, 314, intentQuickPost, immutableFlags);
                remoteViews.SetOnClickPendingIntent(Resource.Id.timelineQuickPostBtn, piQuickPost);

                // 5. Empty state text
                remoteViews.SetTextViewText(
                    Resource.Id.timelineEmptyView,
                    isRunning ? "Listening for posts on Nostr..." : "nokandro service is stopped");

                // 6. ListView RemoteAdapter & PendingIntentTemplate
#pragma warning disable CA1416, CA1422
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S && collectionItems != null)
                {
                    remoteViews.SetRemoteAdapter(Resource.Id.timelineListView, collectionItems);
                }
                else
                {
                    var svcIntent = new Intent(context, typeof(TimelineWidgetService));
                    svcIntent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
                    svcIntent.SetData(Android.Net.Uri.Parse(svcIntent.ToUri(IntentUriType.Scheme)));
                    remoteViews.SetRemoteAdapter(Resource.Id.timelineListView, svcIntent);
                }
#pragma warning restore CA1416, CA1422
                remoteViews.SetEmptyView(Resource.Id.timelineListView, Resource.Id.timelineEmptyView);

                var itemTemplateIntent = new Intent(context, typeof(TimelineWidget));
                itemTemplateIntent.SetAction(ACTION_ITEM_CLICK);
                itemTemplateIntent.PutExtra(AppWidgetManager.ExtraAppwidgetId, widgetId);
                var itemTemplatePi = PendingIntent.GetBroadcast(context, 400 + widgetId, itemTemplateIntent, templateFlags);
                remoteViews.SetPendingIntentTemplate(Resource.Id.timelineListView, itemTemplatePi);

                appWidgetManager.UpdateAppWidget(widgetId, remoteViews);
            }

            if (notifyDataChanged && Build.VERSION.SdkInt < BuildVersionCodes.S)
            {
#pragma warning disable CA1422
                appWidgetManager.NotifyAppWidgetViewDataChanged(appWidgetIds, Resource.Id.timelineListView);
#pragma warning restore CA1422
            }
        }

#pragma warning disable CA1416
        private static RemoteViews.RemoteCollectionItems BuildRemoteCollectionItems(
            Context context,
            string? defaultRelay,
            bool isServiceRunning,
            float density)
        {
            var items = LiveNoteStore.GetSnapshot(context, 50);
            var builder = new RemoteViews.RemoteCollectionItems.Builder();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var itemRv = BuildItemRemoteView(context, item, defaultRelay, isServiceRunning, density);
                builder.AddItem(ComputeStableItemId(item.Id, i), itemRv);
            }
            builder.SetHasStableIds(true);
            builder.SetViewTypeCount(1);
            return builder.Build();
        }
#pragma warning restore CA1416

        internal static long ComputeStableItemId(string? eventId, int fallbackPosition)
        {
            if (!string.IsNullOrEmpty(eventId))
            {
                if (eventId.Length >= 16 &&
                    ulong.TryParse(eventId.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber, null, out var parsed))
                {
                    return unchecked((long)parsed);
                }
                unchecked
                {
                    ulong hash = 14695981039346656037UL;
                    foreach (var ch in eventId)
                    {
                        hash ^= ch;
                        hash *= 1099511628211UL;
                    }
                    return (long)hash;
                }
            }
            return fallbackPosition;
        }

        internal static RemoteViews BuildItemRemoteView(
            Context context,
            LiveNoteItem item,
            string? defaultRelay,
            bool isServiceRunning,
            float density)
        {
            var rv = new RemoteViews(context.PackageName, Resource.Layout.widget_timeline_item);

            // Author display name: petname -> kind:0 display_name/name -> shortened pubkey
            var cachedDisplayName = UserProfileLoader.GetCachedDisplayName(context, item.Pubkey);
            var authorLabel = !string.IsNullOrWhiteSpace(item.Petname)
                ? item.Petname
                : !string.IsNullOrWhiteSpace(cachedDisplayName)
                    ? cachedDisplayName
                    : (!string.IsNullOrEmpty(item.Pubkey) && item.Pubkey.Length >= 12 ? item.Pubkey[..12] + "…" : "Nostr");
            rv.SetTextViewText(Resource.Id.timelineItemAuthor, authorLabel);

            // Avatar via versioned ContentProvider URI (tiny IPC payload, prevents recycled/stale bitmap drift)
            try
            {
                var avatarUri = LiveNoteAvatarHelper.GetOrCreateSmallCircularAvatarUri(context, item.Pubkey, authorLabel, density);
                if (avatarUri != null)
                {
                    rv.SetImageViewUri(Resource.Id.timelineItemAvatar, avatarUri);
                }
                else
                {
                    rv.SetImageViewResource(Resource.Id.timelineItemAvatar, Resource.Mipmap.ic_launcher);
                }
            }
            catch
            {
                rv.SetImageViewResource(Resource.Id.timelineItemAvatar, Resource.Mipmap.ic_launcher);
            }

            // Trigger background kind:0 profile/avatar refresh if missing or TTL expired
            var effectiveRelay = !string.IsNullOrWhiteSpace(item.Relay) ? item.Relay : defaultRelay;
            LiveNoteAvatarHelper.TriggerAvatarDownloadIfNeeded(context, item.Pubkey, effectiveRelay, () =>
            {
                ScheduleDebouncedDataRefresh(context);
            });

            // Relative time
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - item.CreatedAt;
            var timeStr = elapsed < 60
                ? "Just now"
                : elapsed < 3600
                    ? $"{elapsed / 60}m"
                    : elapsed < 86400
                        ? $"{elapsed / 3600}h"
                        : $"{elapsed / 86400}d";
            rv.SetTextViewText(Resource.Id.timelineItemTime, timeStr);

            // Body text
            var displayText = LiveNoteWidget.ShortenUrls(item.Content);
            rv.SetTextViewText(Resource.Id.timelineItemText, displayText);

            try
            {
                var isFollowed = item.IsFollowed ?? !string.IsNullOrWhiteSpace(item.Petname);
                var primaryText = isServiceRunning
                    ? new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface))
                    : new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface_variant));
                var secondaryText = new Android.Graphics.Color(context.GetColor(Resource.Color.on_surface_variant));
                var unfollowedAuthorColor = isServiceRunning
                    ? secondaryText
                    : new Android.Graphics.Color(secondaryText.R, secondaryText.G, secondaryText.B, (byte)155);
                var authorColor = isFollowed ? primaryText : unfollowedAuthorColor;

                rv.SetTextColor(Resource.Id.timelineItemAuthor, authorColor);
                rv.SetTextColor(Resource.Id.timelineItemText, primaryText);
                rv.SetTextColor(Resource.Id.timelineItemTime, secondaryText);

                if (item.IsReacted)
                {
                    rv.SetTextViewText(Resource.Id.timelineItemStarBtn, "★");
                    rv.SetTextColor(Resource.Id.timelineItemStarBtn, new Android.Graphics.Color(context.GetColor(Resource.Color.update_available)));
                }
                else
                {
                    rv.SetTextViewText(Resource.Id.timelineItemStarBtn, "☆");
                    rv.SetTextColor(Resource.Id.timelineItemStarBtn, secondaryText);
                }
            }
            catch { }

            // Row tap -> open in nokakoi
            var openFillIn = new Intent();
            openFillIn.PutExtra("itemAction", "open");
            openFillIn.PutExtra("eventId", item.Id);
            openFillIn.PutExtra("authorPubkey", item.Pubkey);
            openFillIn.PutExtra("relay", effectiveRelay ?? "");
            openFillIn.PutExtra("kind", item.Kind > 0 ? item.Kind : 1);
            rv.SetOnClickFillInIntent(Resource.Id.timelineItemRow, openFillIn);

            // Star tap -> react (+)
            var reactFillIn = new Intent();
            reactFillIn.PutExtra("itemAction", "react");
            reactFillIn.PutExtra("eventId", item.Id);
            reactFillIn.PutExtra("authorPubkey", item.Pubkey);
            rv.SetOnClickFillInIntent(Resource.Id.timelineItemStarBtn, reactFillIn);

            return rv;
        }
    }

    // Fallback RemoteViewsService for Android 7-11 (API 24-30)
    [Service(Name = "com.nokakoi.nokandro.TimelineWidgetService", Permission = "android.permission.BIND_REMOTEVIEWS", Exported = false)]
    public sealed class TimelineWidgetService : RemoteViewsService
    {
        public override IRemoteViewsFactory OnGetViewFactory(Intent? intent)
        {
            return new TimelineRemoteViewsFactory(ApplicationContext ?? this);
        }
    }

    internal sealed class TimelineRemoteViewsFactory : Java.Lang.Object, RemoteViewsService.IRemoteViewsFactory
    {
        private readonly Context _context;
        private volatile List<LiveNoteItem> _items = new();
        private string? _defaultRelay;
        private bool _isServiceRunning;
        private float _density = 2.0f;

        public TimelineRemoteViewsFactory(Context context)
        {
            _context = context;
        }

        public void OnCreate()
        {
            _density = _context.Resources?.DisplayMetrics?.Density ?? 2.0f;
        }

        public void OnDataSetChanged()
        {
            var identity = Binder.ClearCallingIdentity();
            try
            {
                _density = _context.Resources?.DisplayMetrics?.Density ?? 2.0f;
                var prefs = _context.GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                _defaultRelay = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");
                _isServiceRunning = NostrService.IsRunning;
                _items = LiveNoteStore.GetSnapshot(_context, 50);
            }
            catch
            {
                _items = new List<LiveNoteItem>();
            }
            finally
            {
                Binder.RestoreCallingIdentity(identity);
            }
        }

        public void OnDestroy()
        {
            _items = new List<LiveNoteItem>();
        }

        public int Count => _items.Count;

        public RemoteViews GetViewAt(int position)
        {
            var snapshot = _items;
            if (position < 0 || position >= snapshot.Count)
                return new RemoteViews(_context.PackageName, Resource.Layout.widget_timeline_item);

            return TimelineWidget.BuildItemRemoteView(_context, snapshot[position], _defaultRelay, _isServiceRunning, _density);
        }

        public RemoteViews? LoadingView => null;

        public int ViewTypeCount => 1;

        public long GetItemId(int position)
        {
            var snapshot = _items;
            if (position < 0 || position >= snapshot.Count) return position;
            return TimelineWidget.ComputeStableItemId(snapshot[position].Id, position);
        }

        public bool HasStableIds => true;
    }
}
