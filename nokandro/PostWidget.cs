using Android.App;
using Android.Content;
using Android.Appwidget;
using Android.Widget;

namespace nokandro
{
    [BroadcastReceiver(Label = "Nostr Post", Exported = true)]
    [IntentFilter(new string[] { "android.appwidget.action.APPWIDGET_UPDATE" })]
    [MetaData("android.appwidget.provider", Resource = "@xml/widget_info")]
    public class PostWidget : AppWidgetProvider
    {
        public override void OnUpdate(Context context, AppWidgetManager appWidgetManager, int[] appWidgetIds)
        {
            foreach (var widgetId in appWidgetIds)
            {
                var remoteViews = new RemoteViews(context.PackageName, Resource.Layout.widget_post);

                // Intent to launch QuickPostActivity
                var intent = new Intent(context, typeof(QuickPostActivity));
                intent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                var pendingIntent = PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

                // Attach to root layout
                remoteViews.SetOnClickPendingIntent(Resource.Id.widgetRoot, pendingIntent);

                appWidgetManager.UpdateAppWidget(widgetId, remoteViews);
            }
        }
    }
}