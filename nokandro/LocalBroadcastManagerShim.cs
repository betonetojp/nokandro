using System;
using System.Collections.Generic;
using Android.Content;
using Android.App;

namespace AndroidX.LocalBroadcastManager.Content
{
    // Minimal in-process LocalBroadcastManager shim to avoid adding AndroidX dependency.
    // Supports RegisterReceiver/UnregisterReceiver and SendBroadcast with simple action matching.
    public class LocalBroadcastManager
    {
        private static readonly object _sync = new object();
        private static LocalBroadcastManager? _instance;
        private readonly List<(BroadcastReceiver receiver, IntentFilter filter)> _receivers = new();

        private LocalBroadcastManager() { }

        public static LocalBroadcastManager GetInstance(Context context)
        {
            lock (_sync)
            {
                _instance ??= new LocalBroadcastManager();
                return _instance;
            }
        }

        public void RegisterReceiver(BroadcastReceiver receiver, IntentFilter filter)
        {
            if (receiver == null || filter == null) return;
            lock (_sync)
            {
                _receivers.Add((receiver, filter));
            }
        }

        public void UnregisterReceiver(BroadcastReceiver receiver)
        {
            if (receiver == null) return;
            lock (_sync)
            {
                _receivers.RemoveAll(t => t.receiver == receiver);
            }
        }

        public void SendBroadcast(Intent intent)
        {
            if (intent == null) return;
            List<(BroadcastReceiver receiver, IntentFilter filter)> snapshot;
            lock (_sync)
            {
                snapshot = new List<(BroadcastReceiver, IntentFilter)>(_receivers);
            }

            foreach (var (receiver, filter) in snapshot)
            {
                try
                {
                    var action = intent.Action;
                    if (action == null)
                    {
                        // If intent has no action, deliver to all
                        receiver.OnReceive(Application.Context, intent);
                    }
                    else if (filter.HasAction(action))
                    {
                        receiver.OnReceive(Application.Context, intent);
                    }
                }
                catch
                {
                    // ignore individual receiver errors
                }
            }
        }
    }
}
