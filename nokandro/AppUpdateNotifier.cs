using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;

namespace nokandro
{
    internal static class AppUpdateNotifier
    {
        public const string ExtraOpenUpdateDialog = "open_update_dialog";

        private const string ChannelId = "app_update_channel";
        private const string ChannelName = "App Updates";
        private const int NotificationId = 1003;
        private const int JobId = 2001;
        private const string PrefsName = "nokandro_prefs";
        private const string PrefLastNotifiedVersion = "pref_update_notified_version";
        private const string PrefLastCheckedAt = "pref_update_last_checked_at";
        private static readonly long PeriodicIntervalMs = (long)TimeSpan.FromHours(12).TotalMilliseconds;
        private static readonly long MinCheckCooldownSeconds = (long)TimeSpan.FromHours(6).TotalSeconds;

        public static void EnsureChannel(Context context)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is not NotificationManager nm)
                    return;

#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var existing = nm.GetNotificationChannel(ChannelId);
                    if (existing == null)
                    {
                        var chan = new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Default)
                        {
                            Description = "Notifications when a new version of nokandro is available"
                        };
                        nm.CreateNotificationChannel(chan);
                    }
                }
#pragma warning restore CA1416
            }
            catch { }
        }

        public static void SchedulePeriodicCheck(Context context)
        {
            try
            {
                if (context.GetSystemService(Context.JobSchedulerService) is not JobScheduler scheduler)
                    return;

                if (scheduler.GetPendingJob(JobId) != null)
                    return;

                var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(UpdateCheckJobService)));
                var builder = new JobInfo.Builder(JobId, component);
                builder.SetRequiredNetworkType(NetworkType.Any);
                builder.SetPeriodic(PeriodicIntervalMs);
                builder.SetPersisted(true);
                var jobInfo = builder.Build();
                if (jobInfo != null)
                {
                    scheduler.Schedule(jobInfo);
                }
            }
            catch (Exception ex)
            {
                AppLog.W("AppUpdateNotifier", "Failed to schedule update check job: " + ex.Message);
            }
        }

        public static string? GetCurrentVersionName(Context context)
        {
            try
            {
#pragma warning disable CA1422
                var pkg = context.PackageManager?.GetPackageInfo(context.PackageName ?? "", 0);
#pragma warning restore CA1422
                var ver = pkg?.VersionName;
                if (!string.IsNullOrWhiteSpace(ver))
                    return "v" + ver.TrimStart('v', 'V');
            }
            catch { }
            return null;
        }

        public static async Task CheckAndNotifyAsync(Context context, bool respectCooldown = true)
        {
            try
            {
                var currentVer = GetCurrentVersionName(context);
                if (string.IsNullOrEmpty(currentVer))
                    return;

                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (respectCooldown && prefs != null)
                {
                    var lastChecked = prefs.GetLong(PrefLastCheckedAt, 0);
                    if (lastChecked > 0 && (now - lastChecked) < MinCheckCooldownSeconds)
                        return;
                }

                prefs?.Edit()?.PutLong(PrefLastCheckedAt, now)?.Apply();

                var info = await AppUpdater.CheckForUpdateAsync(currentVer).ConfigureAwait(false);
                if (info == null)
                    return;

                if (info.HasUpdate)
                {
                    NotifyIfNewUpdate(context, info);
                }
                else
                {
                    CancelNotification(context);
                }
            }
            catch { }
        }

        public static bool NotifyIfNewUpdate(Context context, AppUpdateInfo info)
        {
            if (info == null || !info.HasUpdate || string.IsNullOrWhiteSpace(info.LatestVersion))
                return false;

            try
            {
#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    if (context.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
                        return false;
                }
#pragma warning restore CA1416

                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                var lastNotified = prefs?.GetString(PrefLastNotifiedVersion, null);
                if (!string.IsNullOrEmpty(lastNotified) &&
                    string.Equals(lastNotified, info.LatestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (context.GetSystemService(Context.NotificationService) is not NotificationManager nm)
                    return false;

                EnsureChannel(context);

                var openIntent = new Intent(context, typeof(MainActivity));
                openIntent.PutExtra(ExtraOpenUpdateDialog, true);
                openIntent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

                var piFlags = PendingIntentFlags.UpdateCurrent;
#pragma warning disable CA1416
                if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
                    piFlags |= PendingIntentFlags.Immutable;
#pragma warning restore CA1416

                var pending = PendingIntent.GetActivity(context, NotificationId, openIntent, piFlags);

#pragma warning disable CA1416, CA1422
                var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                    ? new Notification.Builder(context, ChannelId)
                    : new Notification.Builder(context);

                builder.SetContentTitle($"nokandro {info.LatestVersion} is available")
                    .SetContentText($"Current: {info.CurrentVersion} — Tap to view release notes and update")
                    .SetSmallIcon(Resource.Drawable.ic_stat_nokandro)
                    .SetColor(new Android.Graphics.Color(Android.Graphics.Color.ParseColor("#FF4081")))
                    .SetContentIntent(pending)
                    .SetAutoCancel(true);

                nm.Notify(NotificationId, builder.Build());
#pragma warning restore CA1416, CA1422

                prefs?.Edit()?.PutString(PrefLastNotifiedVersion, info.LatestVersion)?.Apply();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CancelNotification(Context context)
        {
            try
            {
                if (context.GetSystemService(Context.NotificationService) is NotificationManager nm)
                    nm.Cancel(NotificationId);
            }
            catch { }
        }
    }

    [Service(Name = "com.nokakoi.nokandro.UpdateCheckJobService", Permission = "android.permission.BIND_JOB_SERVICE", Exported = false)]
    public sealed class UpdateCheckJobService : JobService
    {
        private CancellationTokenSource? _cts;

        public override bool OnStartJob(JobParameters? @params)
        {
            try { _cts?.Cancel(); } catch { }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        await AppUpdateNotifier.CheckAndNotifyAsync(ApplicationContext ?? this, respectCooldown: true).ConfigureAwait(false);
                    }
                }
                catch { }
                finally
                {
                    try { JobFinished(@params, false); } catch { }
                }
            });

            return true;
        }

        public override bool OnStopJob(JobParameters? @params)
        {
            try { _cts?.Cancel(); } catch { }
            return false;
        }
    }
}
