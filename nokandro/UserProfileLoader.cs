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
            var filePath = System.IO.Path.Combine(cacheDir, $"{pubkeyHex}.png");

            if (System.IO.File.Exists(filePath))
            {
                try
                {
                    var diskBmp = BitmapFactory.DecodeFile(filePath);
                    if (diskBmp != null)
                    {
                        MemoryCache[pubkeyHex] = diskBmp;
                        ApplyAvatar(layout, pubkeyHex, diskBmp);
                        return;
                    }
                }
                catch { }
            }

            // 3. Fetch kind 0 metadata from relays in background
            Task.Run(async () =>
            {
                try
                {
                    var pictureUrl = await FetchPictureUrlAsync(pubkeyHex, preferredRelay).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(pictureUrl)) return;

                    var bitmap = await DownloadAndCropAvatarAsync(pictureUrl, filePath).ConfigureAwait(false);
                    if (bitmap != null)
                    {
                        MemoryCache[pubkeyHex] = bitmap;
                        layout.Post(() =>
                        {
                            if (string.Equals(layout.Tag?.ToString(), pubkeyHex, StringComparison.OrdinalIgnoreCase))
                            {
                                ApplyAvatar(layout, pubkeyHex, bitmap);
                            }
                        });
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
            try
            {
                var drawable = new BitmapDrawable(layout.Resources, bitmap);
                layout.StartIconDrawable = drawable;
                layout.SetStartIconTintList(null); // Clear tint to preserve avatar photo colors
            }
            catch { }
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

        private static async Task<Bitmap?> DownloadAndCropAvatarAsync(string pictureUrl, string diskCachePath)
        {
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(pictureUrl).ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;

                var rawBitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                if (rawBitmap == null) return null;

                var circular = CreateCircularBitmap(rawBitmap, 120);
                rawBitmap.Recycle();

                if (circular != null)
                {
                    try
                    {
                        using var fs = System.IO.File.OpenWrite(diskCachePath);
                        circular.Compress(Bitmap.CompressFormat.Png, 100, fs);
                    }
                    catch { }
                    return circular;
                }
            }
            catch { }
            return null;
        }

        private static Bitmap? CreateCircularBitmap(Bitmap source, int targetSize)
        {
            try
            {
                var output = Bitmap.CreateBitmap(targetSize, targetSize, Bitmap.Config.Argb8888!);
                using var canvas = new Canvas(output!);
                using var paint = new Paint { AntiAlias = true };

                var matrix = new Matrix();
                var scale = Math.Max((float)targetSize / source.Width, (float)targetSize / source.Height);
                var dx = (targetSize - source.Width * scale) * 0.5f;
                var dy = (targetSize - source.Height * scale) * 0.5f;
                matrix.SetScale(scale, scale);
                matrix.PostTranslate(dx, dy);

                using var shader = new BitmapShader(source, Shader.TileMode.Clamp!, Shader.TileMode.Clamp!);
                shader.SetLocalMatrix(matrix);
                paint.SetShader(shader);

                var radius = targetSize / 2f;
                canvas.DrawCircle(radius, radius, radius, paint);

                return output;
            }
            catch
            {
                return null;
            }
        }
    }
}
