using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Dialog;

namespace nokandro
{
    public sealed class AppUpdateInfo
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string ReleaseUrl { get; set; } = "https://github.com/betonetojp/nokandro/releases/latest";
        public string? ApkDownloadUrl { get; set; }
        public long ApkSize { get; set; }
    }

    /// <summary>
    /// Checks for updates via GitHub Releases API and performs in-app downloading and installation.
    /// </summary>
    public static class AppUpdater
    {
        public static AppUpdateInfo? LatestUpdateInfo { get; private set; }

        public static async Task<AppUpdateInfo?> CheckForUpdateAsync(string currentVer)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.TryParseAdd("nokandro");
                client.Timeout = TimeSpan.FromSeconds(10);

                var json = await client.GetStringAsync("https://api.github.com/repos/betonetojp/nokandro/releases/latest").ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("tag_name", out var tagEl))
                    return null;

                var latestTag = tagEl.GetString() ?? "";
                if (string.IsNullOrEmpty(latestTag))
                    return null;

                var info = new AppUpdateInfo
                {
                    CurrentVersion = currentVer,
                    LatestVersion = latestTag,
                    HasUpdate = IsNewer(currentVer, latestTag)
                };

                if (root.TryGetProperty("html_url", out var htmlEl))
                    info.ReleaseUrl = htmlEl.GetString() ?? info.ReleaseUrl;

                if (root.TryGetProperty("body", out var bodyEl))
                    info.ReleaseNotes = bodyEl.GetString() ?? "";

                // Look for .apk asset (prioritize Signed.apk)
                if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    string? fallbackApkUrl = null;
                    long fallbackSize = 0;

                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                        var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                        if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            if (name.Contains("Signed", StringComparison.OrdinalIgnoreCase))
                            {
                                info.ApkDownloadUrl = downloadUrl;
                                info.ApkSize = size;
                                break;
                            }
                            else if (fallbackApkUrl == null)
                            {
                                fallbackApkUrl = downloadUrl;
                                fallbackSize = size;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(info.ApkDownloadUrl) && !string.IsNullOrEmpty(fallbackApkUrl))
                    {
                        info.ApkDownloadUrl = fallbackApkUrl;
                        info.ApkSize = fallbackSize;
                    }
                }

                LatestUpdateInfo = info;
                return info;
            }
            catch
            {
                return null;
            }
        }

        public static void ShowUpdateDialog(Activity activity, AppUpdateInfo info)
        {
            activity.RunOnUiThread(() =>
            {
                try
                {
                    var builder = DialogHelper.CreateBuilder(activity);
                    builder.SetTitle($"🚀 Update Available: {info.LatestVersion}");

                    var notes = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                        ? "A new release is available with improvements and bug fixes."
                        : info.ReleaseNotes.Trim();

                    // Limit long release notes to reasonable length
                    if (notes.Length > 600)
                        notes = notes[..597] + "...";

                    var sizeText = info.ApkSize > 0 ? $"\nSize: {info.ApkSize / 1024.0 / 1024.0:F1} MB" : "";
                    builder.SetMessage($"Current: {info.CurrentVersion}\nLatest: {info.LatestVersion}{sizeText}\n\n{notes}");

                    if (!string.IsNullOrEmpty(info.ApkDownloadUrl))
                    {
                        builder.SetPositiveButton("Update Now", (s, e) =>
                        {
                            StartDownloadAndInstall(activity, info);
                        });
                    }

                    builder.SetNeutralButton("View on GitHub", (s, e) =>
                    {
                        try
                        {
                            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(info.ReleaseUrl));
                            activity.StartActivity(intent);
                        }
                        catch { }
                    });

                    builder.SetNegativeButton("Later", (s, e) => { });

                    var dialog = builder.Create();
                    dialog.Show();
                    DialogHelper.ApplyThemeColors(dialog, activity);
                }
                catch { }
            });
        }

        public static void StartDownloadAndInstall(Activity activity, AppUpdateInfo info)
        {
            if (string.IsNullOrEmpty(info.ApkDownloadUrl))
            {
                // Fallback to browser if no direct APK URL found
                try
                {
                    var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(info.ReleaseUrl));
                    activity.StartActivity(intent);
                }
                catch { }
                return;
            }

            // Check Unknown App Sources permission on Android 8.0+
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                if (activity.PackageManager?.CanRequestPackageInstalls() == false)
                {
                    var reqDialog = DialogHelper.CreateBuilder(activity)
                        .SetTitle("Permission Required")
                        .SetMessage("To install updates directly, nokandro needs permission to install unknown apps.\n\nPlease allow it in the Settings screen that opens next, then tap Update again.")
                        .SetPositiveButton("Open Settings", (s, e) =>
                        {
                            try
                            {
                                var intent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + activity.PackageName));
                                activity.StartActivity(intent);
                            }
                            catch { }
                        })
                        .SetNegativeButton("Cancel", (s, e) => { })
                        .Create();
                    reqDialog.Show();
                    DialogHelper.ApplyThemeColors(reqDialog, activity);
                    return;
                }
            }

            // Build download progress UI
            var density = activity.Resources?.DisplayMetrics?.Density ?? 1.0f;
            var layout = new LinearLayout(activity)
            {
                Orientation = Orientation.Vertical
            };
            var pad = (int)(20 * density);
            layout.SetPadding(pad, (int)(12 * density), pad, (int)(12 * density));

            var progressBar = new ProgressBar(activity, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
            {
                Indeterminate = false,
                Max = 100,
                Progress = 0
            };
            progressBar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = (int)(8 * density),
                BottomMargin = (int)(12 * density)
            };

            var statusTv = new TextView(activity)
            {
                Text = "Starting download..."
            };
            try { statusTv.SetTextColor(DialogHelper.OnSurfaceColor(activity)); } catch { }

            layout.AddView(statusTv);
            layout.AddView(progressBar);

            var cts = new CancellationTokenSource();

            var progressDialog = DialogHelper.CreateBuilder(activity)
                .SetTitle($"Downloading {info.LatestVersion}")
                .SetView(layout)
                .SetNegativeButton("Cancel", (s, e) =>
                {
                    cts.Cancel();
                })
                .SetCancelable(false)
                .Create();

            progressDialog.Show();
            DialogHelper.ApplyThemeColors(progressDialog, activity);

            Task.Run(async () =>
            {
                try
                {
                    var updatesDir = System.IO.Path.Combine(activity.CacheDir?.AbsolutePath ?? "", "updates");
                    System.IO.Directory.CreateDirectory(updatesDir);
                    var apkPath = System.IO.Path.Combine(updatesDir, "nokandro-update.apk");

                    if (System.IO.File.Exists(apkPath))
                    {
                        try { System.IO.File.Delete(apkPath); } catch { }
                    }

                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromMinutes(5);
                    using var response = await client.GetAsync(info.ApkDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var totalBytes = response.Content.Headers.ContentLength ?? info.ApkSize;
                    var totalMb = totalBytes > 0 ? totalBytes / 1024.0 / 1024.0 : 0;

                    using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    using var fileStream = System.IO.File.Create(apkPath);

                    var buffer = new byte[81920]; // 80 KB buffer
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cts.Token).ConfigureAwait(false);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            var progress = (int)((totalRead * 100) / totalBytes);
                            var currentMb = totalRead / 1024.0 / 1024.0;
                            activity.RunOnUiThread(() =>
                            {
                                progressBar.Progress = progress;
                                statusTv.Text = $"{currentMb:F1} MB / {totalMb:F1} MB ({progress}%)";
                            });
                        }
                        else
                        {
                            var currentMb = totalRead / 1024.0 / 1024.0;
                            activity.RunOnUiThread(() =>
                            {
                                statusTv.Text = $"{currentMb:F1} MB downloaded...";
                            });
                        }
                    }

                    await fileStream.FlushAsync(cts.Token).ConfigureAwait(false);
                    fileStream.Close();

                    // Download complete! Dismiss dialog and launch installer
                    activity.RunOnUiThread(() =>
                    {
                        try { progressDialog.Dismiss(); } catch { }
                        LaunchInstaller(activity, apkPath);
                    });
                }
                catch (System.OperationCanceledException)
                {
                    activity.RunOnUiThread(() =>
                    {
                        try { progressDialog.Dismiss(); } catch { }
                        Toast.MakeText(activity, "Download canceled", ToastLength.Short)?.Show();
                    });
                }
                catch (Exception ex)
                {
                    activity.RunOnUiThread(() =>
                    {
                        try { progressDialog.Dismiss(); } catch { }
                        var errDialog = DialogHelper.CreateBuilder(activity)
                            .SetTitle("Download Failed")
                            .SetMessage($"Could not download update: {ex.Message}\n\nWould you like to open the GitHub release in browser?")
                            .SetPositiveButton("Open Browser", (s, e) =>
                            {
                                try
                                {
                                    var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(info.ReleaseUrl));
                                    activity.StartActivity(intent);
                                }
                                catch { }
                            })
                            .SetNegativeButton("Cancel", (s, e) => { })
                            .Create();
                        errDialog.Show();
                        DialogHelper.ApplyThemeColors(errDialog, activity);
                    });
                }
            });
        }

        private static void LaunchInstaller(Activity activity, string apkPath)
        {
            try
            {
                var file = new Java.IO.File(apkPath);
                var apkUri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    activity,
                    activity.PackageName + ".fileprovider",
                    file);

                var installIntent = new Intent(Intent.ActionView);
                installIntent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
                installIntent.AddFlags(ActivityFlags.GrantReadUriPermission);
                installIntent.AddFlags(ActivityFlags.NewTask);
                activity.StartActivity(installIntent);
            }
            catch (Exception ex)
            {
                var errDialog = DialogHelper.CreateBuilder(activity)
                    .SetTitle("Install Error")
                    .SetMessage($"Could not launch package installer: {ex.Message}")
                    .SetPositiveButton("OK", (s, e) => { })
                    .Create();
                errDialog.Show();
                DialogHelper.ApplyThemeColors(errDialog, activity);
            }
        }

        public static bool IsNewer(string current, string latest)
        {
            var v1 = ParseVersion(current);
            var v2 = ParseVersion(latest);
            return v2 > v1;
        }

        private static Version ParseVersion(string v)
        {
            var s = v.TrimStart('v', 'V');
            if (Version.TryParse(s, out var ver)) return ver;
            return new Version(0, 0, 0);
        }
    }
}
