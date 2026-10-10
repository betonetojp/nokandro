using Android.App;
using Android.Content;
using Android.Appwidget;
using Android.Widget;
using Android.Graphics;
using Android.OS;

namespace nokandro
{
    [BroadcastReceiver(Label = "Nostr Post", Exported = true)]
    [IntentFilter(new string[] { "android.appwidget.action.APPWIDGET_UPDATE", "nokandro.ACTION_TOGGLE_SERVICE", "nokandro.ACTION_UPDATE_WIDGET" })]
    [MetaData("android.appwidget.provider", Resource = "@xml/widget_info")]
    public class PostWidget : AppWidgetProvider
    {
        private const string ACTION_TOGGLE_SERVICE = "nokandro.ACTION_TOGGLE_SERVICE";
        private const string ACTION_UPDATE_WIDGET = "nokandro.ACTION_UPDATE_WIDGET";

        public override void OnUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds)
        {
            UpdateAllWidgets(context, appWidgetManager, appWidgetIds);
        }

        public override void OnReceive(Context context, Intent intent)
        {
            base.OnReceive(context, intent);

            if (ACTION_TOGGLE_SERVICE.Equals(intent.Action))
            {
                if (NostrService.IsRunning)
                {
                    // Stop service
                    var stopIntent = new Intent(context, typeof(NostrService));
                    stopIntent.SetAction("STOP");
                    context.StartService(stopIntent); // Use StartService to deliver STOP intent
                }
                else
                {
                    // Start service using saved prefs
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
                            Toast.MakeText(context, "Please configure in app first", ToastLength.Short).Show();
                            var appIntent = new Intent(context, typeof(MainActivity));
                            appIntent.SetFlags(ActivityFlags.NewTask);
                            context.StartActivity(appIntent);
                        }
                    }
                }
                
                var mgr = AppWidgetManager.GetInstance(context);
                var ids = mgr.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(PostWidget))));
                UpdateAllWidgets(context, mgr, ids);
            }
            else if (ACTION_UPDATE_WIDGET.Equals(intent.Action))
            {
                var mgr = AppWidgetManager.GetInstance(context);
                var ids = mgr.GetAppWidgetIds(new ComponentName(context, Java.Lang.Class.FromType(typeof(PostWidget))));
                UpdateAllWidgets(context, mgr, ids);
            }
        }

        private void UpdateAllWidgets(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds)
        {
            foreach (var widgetId in appWidgetIds)
            {
                var remoteViews = new RemoteViews(context.PackageName, Resource.Layout.widget_post);

                // Quick Post Launch (Label only)
                var intentPost = new Intent(context, typeof(QuickPostActivity));
                intentPost.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                var pendingIntentPost = PendingIntent.GetActivity(context, 0, intentPost, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.widgetLabel, pendingIntentPost);
                
                // Toggle Service (Icon)
                var intentToggle = new Intent(context, typeof(PostWidget));
                intentToggle.SetAction(ACTION_TOGGLE_SERVICE);
                var pendingIntentToggle = PendingIntent.GetBroadcast(context, 0, intentToggle, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                remoteViews.SetOnClickPendingIntent(Resource.Id.widgetIcon, pendingIntentToggle);

                // Update Icon Cleanliness
                bool isTtsActive = NostrService.IsRunning && NostrService.IsTtsEnabled;
                if (isTtsActive)
                {
                    remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 255);
                    remoteViews.SetImageViewResource(Resource.Id.widgetIcon, Resource.Mipmap.ic_launcher);
                }
                else
                {
                    // Stopped state or Muted -> Monochrome
                    remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 128);
                    var bmp = WidgetIconHelper.GetGrayscaleLauncherIcon(context);
                    if (bmp != null)
                        remoteViews.SetImageViewBitmap(Resource.Id.widgetIcon, bmp);
                    else
                        remoteViews.SetImageViewResource(Resource.Id.widgetIcon, Resource.Mipmap.ic_launcher);
                }

                appWidgetManager.UpdateAppWidget(widgetId, remoteViews);
            }
        }
    }
}