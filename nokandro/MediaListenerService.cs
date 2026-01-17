using Android.App;
using Android.Content;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Service.Notification;
using Android.Util;
using System;
using System.Collections.Generic;
using MediaController = Android.Media.Session.MediaController;

namespace nokandro
{
    [Service(Label = "Nokandro Music Listener", Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE", Exported = true)]
    [IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
    public class MediaListenerService : NotificationListenerService
    {
        private const string TAG = "MediaListenerService";
        private MediaSessionManager? _sessionManager;
        private SessionCallback? _sessionCallback;
        private ComponentName? _componentName;
        private BroadcastReceiver? _requestReceiver;
        private readonly Dictionary<MediaController, MediaControllerCallback> _controllerCallbacks = [];

        public override void OnListenerConnected()
        {
            base.OnListenerConnected();
            AppLog.D(TAG, "OnListenerConnected");
            try
            {
                _sessionManager = (MediaSessionManager?)GetSystemService(Context.MediaSessionService);
                _componentName = new ComponentName(this, Java.Lang.Class.FromType(typeof(MediaListenerService)));
                _sessionCallback = new SessionCallback(this);

                if (_sessionManager != null && _componentName != null && _sessionCallback != null)
                {
                    _sessionManager.AddOnActiveSessionsChangedListener(_sessionCallback, _componentName);
                    var controllers = _sessionManager.GetActiveSessions(_componentName);
                    UpdateControllers(controllers);
                }

                _requestReceiver = new RequestReceiver(this);
                var filter = new IntentFilter("nokandro.ACTION_REQUEST_MEDIA_STATUS");
                LocalBroadcast.RegisterReceiver(_requestReceiver, filter);
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "Error in OnListenerConnected: " + ex.Message);
            }
        }

        public override void OnListenerDisconnected()
        {
            if (_sessionManager != null && _sessionCallback != null)
            {
                try { _sessionManager.RemoveOnActiveSessionsChangedListener(_sessionCallback); } catch { }
            }
            if (_requestReceiver != null)
            {
                try { LocalBroadcast.UnregisterReceiver(_requestReceiver); } catch { }
                _requestReceiver = null;
            }
            CleanupCallbacks();
            base.OnListenerDisconnected();
        }

        private void BroadcastCurrentState()
        {
            lock (_controllerCallbacks)
            {
                foreach (var cb in _controllerCallbacks.Values)
                {
                    try { cb.CheckAndBroadcast(); } catch { }
                }
            }
        }

        private class RequestReceiver : BroadcastReceiver
        {
            private readonly MediaListenerService _svc;
            public RequestReceiver(MediaListenerService svc) => _svc = svc;
            public override void OnReceive(Context? context, Intent? intent)
            {
                if (intent?.Action == "nokandro.ACTION_REQUEST_MEDIA_STATUS")
                {
                    _svc.BroadcastCurrentState();
                }
            }
        }

        private void CleanupCallbacks()
        {
            lock (_controllerCallbacks)
            {
                foreach (var kvp in _controllerCallbacks)
                {
                    try { kvp.Key.UnregisterCallback(kvp.Value); } catch { }
                }
                _controllerCallbacks.Clear();
            }
        }

        private void UpdateControllers(IList<MediaController>? controllers)
        {
            if (controllers == null) return;
            AppLog.D(TAG, $"UpdateControllers count={controllers.Count}");
            
            // For simplicity, clear and re-register
            CleanupCallbacks();

            lock (_controllerCallbacks)
            {
                foreach (var controller in controllers)
                {
                    if (controller == null) continue;
                    try
                    {
                        var cb = new MediaControllerCallback(this, controller);
                        controller.RegisterCallback(cb);
                        _controllerCallbacks[controller] = cb;
                        // Initial check
                        cb.CheckAndBroadcast();
                    }
                    catch (Exception ex)
                    {
                        AppLog.W(TAG, "Failed to register controller callback: " + ex.Message);
                    }
                }
            }
        }

        public void BroadcastInfo(string? artist, string? title, bool isPlaying)
        {
            // Simple validation
            if (string.IsNullOrEmpty(title)) return;

            AppLog.D(TAG, $"BroadcastInfo: {title} - {artist} (playing={isPlaying})");
            var intent = new Intent("nokandro.ACTION_MEDIA_STATUS");
            intent.PutExtra("artist", artist);
            intent.PutExtra("title", title);
            intent.PutExtra("playing", isPlaying);
            intent.SetPackage(PackageName);
            // Used to use system broadcast, but app uses custom in-process LocalBroadcast
            LocalBroadcast.SendBroadcast(this, intent);
        }

        private class SessionCallback : Java.Lang.Object, MediaSessionManager.IOnActiveSessionsChangedListener
        {
            private readonly MediaListenerService _svc;
            public SessionCallback(MediaListenerService svc) => _svc = svc;
            public void OnActiveSessionsChanged(IList<MediaController>? controllers) => _svc.UpdateControllers(controllers);
        }

        private class MediaControllerCallback : MediaController.Callback
        {
            private readonly MediaListenerService _svc;
            private readonly MediaController _controller;
            public MediaControllerCallback(MediaListenerService svc, MediaController controller)
            {
                _svc = svc;
                _controller = controller;
            }

            public void CheckAndBroadcast()
            {
                try
                {
                    var state = _controller.PlaybackState;
                    var meta = _controller.Metadata;
                    
                    var isPlaying = state != null && state.State == PlaybackStateCode.Playing;
                    // Only broadcast if playing? 
                    // If we want to clear status when paused, we should handle !isPlaying.
                    // But 'Status' in Nostr usually persists for a bit.
                    // For now, allow broadcasting paused state too, receiver decides logic.
                    
                    if (meta != null)
                    {
                        var artist = meta.GetString(MediaMetadata.MetadataKeyArtist);
                        var title = meta.GetString(MediaMetadata.MetadataKeyTitle);
                        _svc.BroadcastInfo(artist, title, isPlaying);
                    }
                }
                catch (Exception ex) 
                {
                    AppLog.W("MediaListenerService", "CheckAndBroadcast error: " + ex.Message);
                }
            }

            public override void OnMetadataChanged(MediaMetadata? metadata) => CheckAndBroadcast();
            public override void OnPlaybackStateChanged(PlaybackState? state) => CheckAndBroadcast();
        }
    }
}
