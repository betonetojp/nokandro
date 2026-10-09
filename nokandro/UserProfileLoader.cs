using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Google.Android.Material.TextField;

namespace nokandro
{
    /// <summary>
    /// Fetches kind 0 metadata (picture) for a Nostr public key,
    /// caches the avatar image, and applies it as circular icon to the npub TextInputLayout.
    /// </summary>
    public static class UserProfileLoader
    {
        private static readonly ConcurrentDictionary<string, Bitmap> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };

        private static readonly string[] DefaultRelays =
        [
            "wss://relay-jp.nostr.wirednet.jp",
            "wss://relay.damus.io",
            "wss://nos.lol",
            "wss://yabu.me"
        ];

        /// <summary>
        /// Loads the user's Nostr profile avatar asynchronously and applies it to the TextInputLayout.
        /// </summary>
        public static void LoadAvatar(Context context, TextInputLayout layout, string? npubOrHex, string? preferredRelay)
        {
            if (layout == null) return;

            var pubkeyHex = ResolvePubkeyHex(npubOrHex);
            if (string.IsNullOrEmpty(pubkeyHex))
            {
                layout.Tag = null;
                ResetToDefaultIcon(context, layout);
                return;
            }

            layout.Tag = pubkeyHex;

            // 1. Check in-memory cache
            if (MemoryCache.TryGetValue(pubkeyHex, out var cached) && cached != null && !cached.IsRecycled)
            {
                ApplyAvatar(layout, pubkeyHex, cached);
                return;
            }

            // 2. Check disk cache
            var cacheDir = System.IO.Path.Combine(context.CacheDir?.AbsolutePath ?? "", "profile_avatars");
            System.IO.Directory.CreateDirectory(cacheDir);
            var srcFilePath = System.IO.Path.Combine(cacheDir, $"{pubkeyHex}_src.png");
            var legacyFilePath = System.IO.Path.Combine(cacheDir, $"{pubkeyHex}.png");

            // Delete legacy oversize cache file if present so it gets re-downloaded/re-processed cleanly
            if (System.IO.File.Exists(legacyFilePath))
            {
                try { System.IO.File.Delete(legacyFilePath); } catch { }
            }

            if (System.IO.File.Exists(srcFilePath))
            {
                try
                {
                    var diskSrcBmp = BitmapFactory.DecodeFile(srcFilePath);
                    if (diskSrcBmp != null)
                    {
                        var avatarBmp = CreateCircularAvatar(diskSrcBmp, context);
                        diskSrcBmp.Recycle();
                        if (avatarBmp != null)
                        {
                            MemoryCache[pubkeyHex] = avatarBmp;
                            ApplyAvatar(layout, pubkeyHex, avatarBmp);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Warn("UserProfileLoader", $"Disk cache load failed: {ex}");
                }
            }

            // 3. Fetch kind 0 metadata from relays in background
            Task.Run(async () =>
            {
                try
                {
                    var pictureUrl = await FetchPictureUrlAsync(pubkeyHex, preferredRelay).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(pictureUrl)) return;

                    var bitmap = await DownloadAndProcessAvatarAsync(pictureUrl, srcFilePath, context).ConfigureAwait(false);
                    if (bitmap != null)
                    {
                        MemoryCache[pubkeyHex] = bitmap;
                        ApplyAvatar(layout, pubkeyHex, bitmap);
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Debug("UserProfileLoader", $"Failed to fetch avatar for {pubkeyHex}: {ex.Message}");
                }
            });
        }

        private static string? ResolvePubkeyHex(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var s = input.Trim();
            var hex = NostrKeyDecoder.NormalizeHexPubkey(s);
            if (!string.IsNullOrEmpty(hex)) return hex;

            if (s.StartsWith("npub1", StringComparison.OrdinalIgnoreCase))
            {
                return NostrKeyDecoder.DecodeBech32Npub(s);
            }

            return null;
        }

        private static void ApplyAvatar(TextInputLayout layout, string expectedPubkey, Bitmap bitmap)
        {
            layout.Post(() =>
            {
                try
                {
                    if (!string.Equals(layout.Tag?.ToString(), expectedPubkey, StringComparison.OrdinalIgnoreCase))
                        return;

                    var drawable = new BitmapDrawable(layout.Resources, bitmap);
                    layout.StartIconDrawable = drawable;
                    layout.SetStartIconTintList(null); // Clear tint to preserve avatar photo colors
                    Android.Util.Log.Info("UserProfileLoader", $"Applied avatar successfully to TextInputLayout (bitmap size: {bitmap.Width}x{bitmap.Height}, density: {bitmap.Density})");
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("UserProfileLoader", $"Failed to apply avatar: {ex}");
                }
            });
        }

        private static void ResetToDefaultIcon(Context context, TextInputLayout layout)
        {
            try
            {
                layout.SetStartIconDrawable(Resource.Drawable.ic_user);
                // Reapply theme tint
                var typedValue = new Android.Util.TypedValue();
                if (context.Theme?.ResolveAttribute(Resource.Attribute.colorOnSurfaceVariant, typedValue, true) == true)
                {
                    layout.SetStartIconTintList(Android.Content.Res.ColorStateList.ValueOf(new Android.Graphics.Color(typedValue.Data)));
                }
            }
            catch { }
        }

        private static async Task<string?> FetchPictureUrlAsync(string pubkeyHex, string? preferredRelay)
        {
            var relays = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredRelay) &&
                (preferredRelay.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) || preferredRelay.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)))
            {
                relays.Add(preferredRelay.Trim());
            }

            foreach (var r in DefaultRelays)
            {
                if (!relays.Contains(r, StringComparer.OrdinalIgnoreCase))
                    relays.Add(r);
            }

            foreach (var relayUrl in relays)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(relayUrl), cts.Token).ConfigureAwait(false);

                    Android.Util.Log.Info("UserProfileLoader", $"Fetching kind 0 for {pubkeyHex} from {relayUrl}");
                    var subId = "avatar" + Random.Shared.Next(1000, 9999);
                    var req = $"[\"REQ\",\"{subId}\",{{\"kinds\":[0],\"authors\":[\"{pubkeyHex}\"],\"limit\":1}}]";
                    var bytes = Encoding.UTF8.GetBytes(req);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);

                    var buffer = new byte[8192];
                    var ms = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        ms.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage)
                        {
                            var msg = Encoding.UTF8.GetString(ms.ToArray());
                            ms.SetLength(0);

                            if (msg.Contains("\"EOSE\"")) break;

                            if (msg.Contains("\"EVENT\""))
                            {
                                var picture = ParsePictureFromEvent(msg);
                                if (!string.IsNullOrEmpty(picture))
                                {
                                    Android.Util.Log.Info("UserProfileLoader", $"Found avatar picture URL: {picture}");
                                    return picture;
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Debug("UserProfileLoader", $"Relay {relayUrl} fetch error: {ex.Message}");
                }
            }

            return null;
        }

        private static string? ParsePictureFromEvent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 3)
                {
                    var ev = root[2];
                    if (ev.TryGetProperty("kind", out var kindProp) && kindProp.GetInt32() == 0 &&
                        ev.TryGetProperty("content", out var contentProp))
                    {
                        var content = contentProp.GetString();
                        if (!string.IsNullOrEmpty(content))
                        {
                            using var profileDoc = JsonDocument.Parse(content);
                            if (profileDoc.RootElement.TryGetProperty("picture", out var picProp) &&
                                picProp.ValueKind == JsonValueKind.String)
                            {
                                var url = picProp.GetString();
                                if (!string.IsNullOrEmpty(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                                {
                                    return url;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<Bitmap?> DownloadAndProcessAvatarAsync(string pictureUrl, string diskCachePath, Context context)
        {
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(pictureUrl).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;

                var rawBitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                if (rawBitmap == null) return null;

                // Save to disk cache for instant offline restore
                try
                {
                    using var fs = System.IO.File.OpenWrite(diskCachePath);
                    rawBitmap.Compress(Bitmap.CompressFormat.Png, 100, fs);
                }
                catch { }

                var avatar = CreateCircularAvatar(rawBitmap, context);
                rawBitmap.Recycle();
                return avatar;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("UserProfileLoader", $"DownloadAndProcessAvatarAsync error: {ex}");
            }
            return null;
        }

        private static Bitmap? CreateCircularAvatar(Bitmap source, Context context, float canvasDp = 48f, float avatarDp = 24f)
        {
            try
            {
                var metrics = context.Resources?.DisplayMetrics;
                var density = metrics?.Density ?? 2.0f;
                var densityDpi = (int)(metrics?.DensityDpi ?? Android.Util.DisplayMetricsDensity.Default);

                var canvasPx = Math.Max(48, (int)Math.Round(canvasDp * density));
                var avatarPx = Math.Max(24, (int)Math.Round(avatarDp * density));

                var output = Bitmap.CreateBitmap(canvasPx, canvasPx, Bitmap.Config.Argb8888!);
                if (output == null) return null;
                output.Density = densityDpi;

                using var canvas = new Canvas(output);
                using var paint = new Paint { AntiAlias = true, FilterBitmap = true };

                var offset = (canvasPx - avatarPx) * 0.5f;

                var matrix = new Matrix();
                var scale = Math.Max((float)avatarPx / source.Width, (float)avatarPx / source.Height);
                var dx = offset + (avatarPx - source.Width * scale) * 0.5f;
                var dy = offset + (avatarPx - source.Height * scale) * 0.5f;
                matrix.SetScale(scale, scale);
                matrix.PostTranslate(dx, dy);

                using var shader = new BitmapShader(source, Shader.TileMode.Clamp!, Shader.TileMode.Clamp!);
                shader.SetLocalMatrix(matrix);
                paint.SetShader(shader);

                var cx = canvasPx * 0.5f;
                var cy = canvasPx * 0.5f;
                var radius = avatarPx * 0.5f;
                canvas.DrawCircle(cx, cy, radius, paint);

                // Subtle border to outline the avatar against dark/light backgrounds
                var strokeWidth = Math.Max(1f, density * 0.75f);
                using var borderPaint = new Paint
                {
                    AntiAlias = true,
                    Color = new Color(128, 128, 128, 60),
                    StrokeWidth = strokeWidth
                };
                borderPaint.SetStyle(Paint.Style.Stroke);
                canvas.DrawCircle(cx, cy, radius - strokeWidth * 0.5f, borderPaint);

                return output;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("UserProfileLoader", $"CreateCircularAvatar error: {ex}");
                return null;
            }
        }
    }
}
