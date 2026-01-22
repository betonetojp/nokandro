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
                        var nsec = prefs.GetString("pref_nsec", "");
                        var allowOthers = prefs.GetBoolean("pref_allow_others", false);
                        var voiceFollowed = prefs.GetString("pref_voice_followed", null);
                        var voiceOther = prefs.GetString("pref_voice_other", null);
                        var speechRate = prefs.GetFloat("pref_speech_rate", 1.0f);
                        var musicStatus = prefs.GetBoolean("pref_music_status", false);
                        var enableTts = prefs.GetBoolean("pref_enable_tts", true);
                        var autoStop = prefs.GetBoolean("pref_auto_stop", true);
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
                        startIntent.PutExtra("speakPetname", speakPetname);
                        startIntent.PutExtra("truncateLen", truncateLen);
                        startIntent.PutExtra("truncateEllipsis", truncateEllipsis);

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
                bool isRunning = NostrService.IsRunning;
                if (isRunning)
                {
                    remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 255);
                    // Reset color filter to remove gray
                    remoteViews.SetInt(Resource.Id.widgetIcon, "setColorFilter", 0);
                    // Or if using standard API:
                    // Unfortunately setTint is not directly available on RemoteViews for ImageView via specific method easily across all API levels without compat issues.
                    // But setColorFilter(0) usually clears filter.
                    // Actually, to make it colorful, we just ensure it's normal.
                    // To be safe against state persistence issues, we might want to re-set the resource.
                    remoteViews.SetImageViewResource(Resource.Id.widgetIcon, Resource.Mipmap.ic_launcher);
                    // Clear color filter if possible, or just set alpha. 
                    // Using setInt with "setColorFilter" (int color) where 0 is transparent/none works on many versions but technically depends on method signature. 
                    // ImageView.setColorFilter(null) is not available via RemoteViews. ImageView.clearColorFilter() is void.
                    // However, SetImageViewResource usually resets basic state.
                }
                else
                {
                    // Stopped state -> Monochrome / Dimmed
                    remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 128); // Semi-transparent
                    // Apply gray tint?
                    // remoteViews.SetInt(Resource.Id.widgetIcon, "setColorFilter", Color.Gray.ToArgb()); 
                    // This tints the whole non-transparent area gray. Good enough for "monochrome".
                    // If icons are colorful, this makes them a flat gray shape.
                    
                    // Create grayscale bitmap for better look (preserves details but removes color)
                    try
                    {
                        var bmp = GetGrayscaleIcon(context);
                        if (bmp != null)
                             remoteViews.SetImageViewBitmap(Resource.Id.widgetIcon, bmp);
                        else
                             remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 100);
                    }
                    catch
                    {
                        remoteViews.SetInt(Resource.Id.widgetIcon, "setAlpha", 100);
                    }
                }

                appWidgetManager.UpdateAppWidget(widgetId, remoteViews);
            }
        }

        private Bitmap? GetGrayscaleIcon(Context context)
        {
            // Load original drawable
            var drawable = context.GetDrawable(Resource.Mipmap.ic_launcher);
            if (drawable == null) return null;
            
            // Convert to bitmap
            var width = drawable.IntrinsicWidth;
            var height = drawable.IntrinsicHeight;
            if (width <= 0) width = 96;
            if (height <= 0) height = 96;
            
            Bitmap bmp = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
            var canvas = new Canvas(bmp);
            drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
            drawable.Draw(canvas);

            // Create grayscale
            Bitmap bmpGray = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
            var c = new Canvas(bmpGray);
            var paint = new Paint();
            var cm = new ColorMatrix();
            cm.SetSaturation(0);
            var f = new ColorMatrixColorFilter(cm);
            paint.SetColorFilter(f);
            c.DrawBitmap(bmp, 0, 0, paint);
            
            return bmpGray;
        }
    }
}