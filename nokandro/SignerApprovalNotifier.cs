using Android.App;
using Android.Content;
using Android.OS;
using AndroidUri = Android.Net.Uri;

namespace nokandro
{
    internal static class SignerApprovalNotifier
    {
        private const string ChannelId = "signer_approval_channel";
        private const string ChannelName = "Signer Requests";
        private const string PrefsName = "nokandro_prefs";
        private const string PrefNcClients = "pref_nc_clients";
        private const string PrefBunkerEnabled = "pref_bunker_enabled";

        private static void EnsureChannel(NotificationManager nm)
        {
#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var existing = nm.GetNotificationChannel(ChannelId);
                if (existing == null)
                {
                    var chan = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.High)
                    {
                        Description = "Connection and signing confirmation requests from Nostr clients"
                    };
                    nm.CreateNotificationChannel(chan);
                }
            }
#pragma warning restore CA1416
        }

        private static int GetNostrConnectNotificationId(string clientPubkey)
        {
            var normalized = (clientPubkey ?? "").Trim().ToLowerInvariant();
            return 3000 + (Math.Abs(normalized.GetHashCode()) % 10000);
        }

        private static int GetNip55NotificationId(string? callerPackage, string requestType)
        {
            var key = $"{callerPackage ?? "unknown"}:{requestType}";
            return 14000 + (Math.Abs(key.GetHashCode()) % 10000);
        }

        public static void NotifyNostrConnectRequest(Context context, NostrConnectUri parsed)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is not NotificationManager nm)
                    return;

                EnsureChannel(nm);

                var notifId = GetNostrConnectNotificationId(parsed.ClientPubkey);
                var shortPk = parsed.ClientPubkey.Length >= 12 ? $"{parsed.ClientPubkey[..12]}..." : parsed.ClientPubkey;
                var displayName = parsed.GetClientName();
                var relayPreview = parsed.Relays.Length > 0 ? parsed.Relays[0] : "";
                if (relayPreview.Length > 40)
                    relayPreview = relayPreview[..40] + "...";

                var clientLabel = !string.IsNullOrEmpty(displayName) ? displayName : shortPk;
                var contentText = !string.IsNullOrEmpty(relayPreview)
                    ? $"Connect {clientLabel} ({relayPreview}) — tap to review"
                    : $"Connect {clientLabel} — tap to review";

                var openIntent = new Intent(context, typeof(NostrConnectDeepLinkActivity));
                openIntent.SetAction(Intent.ActionView);
                openIntent.SetData(AndroidUri.Parse(parsed.RawUri));
                openIntent.PutExtra("nostrconnect_uri", parsed.RawUri);
                openIntent.PutExtra("from_notification", true);
                openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

                var piFlags = PendingIntentFlags.UpdateCurrent;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    piFlags |= PendingIntentFlags.Immutable;

                var pending = PendingIntent.GetActivity(context, notifId, openIntent, piFlags);

#pragma warning disable CA1416, CA1422
                var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                    ? new Notification.Builder(context, ChannelId)
                    : new Notification.Builder(context).SetPriority((int)NotificationPriority.High);

                builder.SetContentTitle("NostrConnect request")
                    .SetContentText(contentText)
                    .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                    .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                    .SetContentIntent(pending)
                    .SetAutoCancel(true);

                nm.Notify(notifId, builder.Build());
#pragma warning restore CA1416, CA1422
            }
            catch { }
        }

        public static void CancelNostrConnectNotification(Context context, string? clientPubkey)
        {
            if (string.IsNullOrEmpty(clientPubkey)) return;
            try
            {
                if (context.GetSystemService(Context.NotificationService) is NotificationManager nm)
                    nm.Cancel(GetNostrConnectNotificationId(clientPubkey));
            }
            catch { }
        }

        public static void NotifyNip55Request(Context context, Intent sourceIntent, string? callerPackage, string requestType)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is not NotificationManager nm)
                    return;

                EnsureChannel(nm);

                var notifId = GetNip55NotificationId(callerPackage, requestType);
                var appLabel = !string.IsNullOrEmpty(callerPackage) ? callerPackage : "Nostr client";
                var contentText = $"{appLabel} ({requestType}) — tap to review";

                var openIntent = new Intent(sourceIntent);
                openIntent.SetClass(context, typeof(NostrSignerActivity));
                if (!string.IsNullOrEmpty(callerPackage))
                    openIntent.PutExtra("caller_package", callerPackage);
                openIntent.PutExtra("from_notification", true);
                openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

                var piFlags = PendingIntentFlags.UpdateCurrent;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    piFlags |= PendingIntentFlags.Immutable;

                var pending = PendingIntent.GetActivity(context, notifId, openIntent, piFlags);

#pragma warning disable CA1416, CA1422
                var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                    ? new Notification.Builder(context, ChannelId)
                    : new Notification.Builder(context).SetPriority((int)NotificationPriority.High);

                builder.SetContentTitle("Nostr Signer request")
                    .SetContentText(contentText)
                    .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                    .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                    .SetContentIntent(pending)
                    .SetAutoCancel(true);

                nm.Notify(notifId, builder.Build());
#pragma warning restore CA1416, CA1422
            }
            catch { }
        }

        public static void CancelNip55Notification(Context context, string? callerPackage, string requestType)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is NotificationManager nm)
                    nm.Cancel(GetNip55NotificationId(callerPackage, requestType));
            }
            catch { }
        }

        public static void StartAndPersistNostrConnectSession(Context context, string connectUri, string nsecHex, bool preAuthenticated = false)
        {
            var intent = new Intent(context, typeof(BunkerService));
            intent.SetAction("START_NOSTRCONNECT");
            intent.PutExtra("connectUri", connectUri);
            intent.PutExtra("nsecHex", nsecHex);
            intent.PutExtra("preAuthenticated", preAuthenticated);

#pragma warning disable CA1416
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
#pragma warning restore CA1416

            PersistNostrConnectClient(context, connectUri);

            // Ensure NIP-46 Bunker enabled flag is set so NostrConnect UI in MainActivity is active
            try
            {
                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                if (prefs != null && !prefs.GetBoolean(PrefBunkerEnabled, false))
                {
                    prefs.Edit()?.PutBoolean(PrefBunkerEnabled, true)?.Apply();
                }
            }
            catch { }
        }

        public static void PersistNostrConnectClient(Context context, string uri)
        {
            try
            {
                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                var existing = prefs?.GetString(PrefNcClients, "") ?? "";
                var list = string.IsNullOrEmpty(existing)
                    ? new List<string>()
                    : new List<string>(existing.Split('\n', StringSplitOptions.RemoveEmptyEntries));

                if (NostrConnectUri.TryParse(uri, out var parsed) && parsed != null)
                {
                    list.RemoveAll(u =>
                    {
                        if (NostrConnectUri.TryParse(u, out var p) && p != null)
                            return p.ClientPubkey == parsed.ClientPubkey;
                        return false;
                    });

                    try
                    {
                        var store = new BunkerClientStore(context);
                        store.RecordPairing(parsed.ClientPubkey, parsed.GetClientName());
                    }
                    catch { }
                }

                list.Add(uri);
                var edit = prefs?.Edit();
                if (edit != null)
                {
                    edit.PutString(PrefNcClients, string.Join('\n', list));
                    edit.Apply();
                }
            }
            catch { }
        }
    }
}
