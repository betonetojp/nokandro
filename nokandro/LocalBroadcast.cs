using Android.Content;

namespace nokandro
{
    // Simple in-process broadcast manager for the app process.
    public static class LocalBroadcast
    {
        private static readonly Lock _sync = new();
        private static readonly List<(BroadcastReceiver receiver, IntentFilter filter)> _receivers = [];

        public static void RegisterReceiver(BroadcastReceiver receiver, IntentFilter filter)
        {
            if (receiver == null || filter == null) return;
            lock (_sync)
            {
                _receivers.Add((receiver, filter));
            }
        }

        public static void UnregisterReceiver(BroadcastReceiver receiver)
        {
            if (receiver == null) return;
            lock (_sync)
            {
                _receivers.RemoveAll(t => t.receiver == receiver);
            }
        }

        public static void SendBroadcast(Context context, Intent intent)
        {
            if (intent == null) return;
            List<(BroadcastReceiver receiver, IntentFilter filter)> snapshot;
            lock (_sync)
            {
                snapshot = [.. _receivers];
            }

            foreach (var (receiver, filter) in snapshot)
            {
                try
                {
                    var action = intent.Action;
                    if (action == null || filter.HasAction(action))
                    {
                        receiver.OnReceive(context ?? Android.App.Application.Context, intent);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
