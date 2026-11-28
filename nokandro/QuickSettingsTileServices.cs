using System;
using Android.App;
using Android.Content;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Service.QuickSettings;

namespace nokandro
{
    internal static class QuickSettingsTileHelper
    {
        private const string TAG = "QuickSettingsTile";

        internal static void RunOnMainThread(Action action)
        {
            try
            {
                if (Looper.MyLooper() == Looper.MainLooper)
                {
                    action();
                }
                else
                {
                    var mainLooper = Looper.MainLooper;
                    if (mainLooper != null)
                        new Handler(mainLooper).Post(action);
                }
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "RunOnMainThread failed: " + ex.Message);
            }
        }

        public static void RequestTileUpdates(Context context, bool refreshActive = true)
        {
            if (refreshActive)
            {
                NostrServiceTileService.RefreshIfListening();
                TtsTileService.RefreshIfListening();
            }
            try
            {
                var appCtx = context.ApplicationContext ?? context;
#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                {
                    TileService.RequestListeningState(appCtx, new ComponentName(appCtx, Java.Lang.Class.FromType(typeof(NostrServiceTileService))));
                    TileService.RequestListeningState(appCtx, new ComponentName(appCtx, Java.Lang.Class.FromType(typeof(TtsTileService))));
                }
#pragma warning restore CA1416
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "RequestTileUpdates failed: " + ex.Message);
            }
        }
    }

    [Service(
        Name = "com.nokakoi.nokandro.NostrServiceTileService",
        Label = "nokandro",
        Icon = "@drawable/ic_stat_nokandro",
        Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
        Exported = true)]
    [IntentFilter([ActionQsTile])]
    public sealed class NostrServiceTileService : TileService
    {
        private const string TAG = "NostrServiceTile";
        private static NostrServiceTileService? _activeInstance;

        internal static void RefreshIfListening(bool? pendingRunning = null)
        {
            QuickSettingsTileHelper.RunOnMainThread(() =>
            {
                try { _activeInstance?.UpdateTile(pendingRunning); }
                catch (Exception ex) { AppLog.W(TAG, "RefreshIfListening failed: " + ex.Message); }
            });
        }

        public override void OnStartListening()
        {
            base.OnStartListening();
            _activeInstance = this;
            UpdateTile();
        }

        public override void OnStopListening()
        {
            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;
            base.OnStopListening();
        }

        public override void OnClick()
        {
            base.OnClick();
            try
            {
                var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                var npub = prefs?.GetString("pref_npub", "");
                var nsec = SecurePreferences.GetNsec(this);

                if (!NostrService.IsRunning && string.IsNullOrEmpty(npub) && string.IsNullOrEmpty(nsec))
                {
                    OpenAppFromTile();
                    return;
                }

                var nextRunning = !NostrService.IsRunning;
                LiveNoteWidget.ToggleService(this);
                UpdateTile(pendingRunning: nextRunning);
                TtsTileService.RefreshIfListening(pendingRunning: nextRunning);
                // NostrService.OnStartCommand / OnDestroy will call RequestTileUpdates once state settles.
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "OnClick failed: " + ex.Message);
            }
        }

        private void OpenAppFromTile()
        {
            try
            {
                var appIntent = new Intent(this, typeof(MainActivity));
                appIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
#pragma warning disable CA1416, CA1422
                if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
                {
                    var pi = PendingIntent.GetActivity(this, 2050, appIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                    if (pi != null) StartActivityAndCollapse(pi);
                }
                else
                {
                    StartActivityAndCollapse(appIntent);
                }
#pragma warning restore CA1416, CA1422
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "OpenAppFromTile failed: " + ex.Message);
            }
        }

        private void UpdateTile(bool? pendingRunning = null)
        {
            var tile = QsTile;
            if (tile == null) return;

            try
            {
                var isRunning = pendingRunning ?? NostrService.IsRunning;

                tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_stat_nokandro);
                tile.Label = "nokandro";
                tile.State = isRunning ? TileState.Active : TileState.Inactive;

#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    tile.Subtitle = isRunning ? "Running" : "Stopped";
                }
#pragma warning restore CA1416

                tile.UpdateTile();
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "UpdateTile failed: " + ex.Message);
            }
        }
    }

    [Service(
        Name = "com.nokakoi.nokandro.TtsTileService",
        Label = "nokandro TTS",
        Icon = "@drawable/ic_stat_nokandro",
        Permission = "android.permission.BIND_QUICK_SETTINGS_TILE",
        Exported = true)]
    [IntentFilter([ActionQsTile])]
    public sealed class TtsTileService : TileService
    {
        private const string TAG = "TtsTile";
        private static TtsTileService? _activeInstance;

        internal static void RefreshIfListening(bool? pendingTts = null, bool? pendingRunning = null)
        {
            QuickSettingsTileHelper.RunOnMainThread(() =>
            {
                try { _activeInstance?.UpdateTile(pendingTts, pendingRunning); }
                catch (Exception ex) { AppLog.W(TAG, "RefreshIfListening failed: " + ex.Message); }
            });
        }

        public override void OnStartListening()
        {
            base.OnStartListening();
            _activeInstance = this;
            UpdateTile();
        }

        public override void OnStopListening()
        {
            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;
            base.OnStopListening();
        }

        public override void OnClick()
        {
            base.OnClick();
            try
            {
                if (NostrService.IsRunning)
                {
                    var nextTts = !NostrService.IsTtsEnabled;
                    var ttsIntent = new Intent(this, typeof(NostrService));
                    ttsIntent.SetAction(NostrService.ACTION_TOGGLE_TTS);
                    StartService(ttsIntent);
                    UpdateTile(pendingTts: nextTts);
                    // NostrService.OnStartCommand(ACTION_TOGGLE_TTS) will call RequestTileUpdates once toggled.
                }
                else
                {
                    var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                    var cur = prefs?.GetBoolean("pref_enable_tts", true) ?? true;
                    var nextTts = !cur;
                    prefs?.Edit()?.PutBoolean("pref_enable_tts", nextTts)?.Apply();
                    UpdateTile(pendingTts: nextTts);
                    LiveNoteWidget.UpdateAllWidgets(this);
                    TimelineWidget.UpdateAllWidgets(this, notifyDataChanged: false);
                }
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "OnClick failed: " + ex.Message);
            }
        }

        private void UpdateTile(bool? pendingTts = null, bool? pendingRunning = null)
        {
            var tile = QsTile;
            if (tile == null) return;

            try
            {
                var isRunning = pendingRunning ?? NostrService.IsRunning;
                var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
                var isTts = pendingTts ?? (isRunning && !pendingRunning.HasValue
                    ? NostrService.IsTtsEnabled
                    : (prefs?.GetBoolean("pref_enable_tts", true) ?? true));

                tile.Icon = Icon.CreateWithResource(this, Resource.Drawable.ic_stat_nokandro);
                tile.Label = "nokandro TTS";
                tile.State = isTts ? TileState.Active : TileState.Inactive;

#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                {
                    tile.Subtitle = isRunning
                        ? (isTts ? "Speaking" : "Muted")
                        : (isTts ? "Stopped (TTS)" : "Stopped (Muted)");
                }
#pragma warning restore CA1416

                tile.UpdateTile();
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "UpdateTile failed: " + ex.Message);
            }
        }
    }
}
