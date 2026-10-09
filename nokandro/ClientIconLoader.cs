using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace nokandro
{
    /// <summary>
    /// Loads, circular-crops, and caches site/app icons for NIP-46 NostrConnect clients.
    /// Supports direct image URLs, metadata icons, and Google Favicon service for domains.
    /// </summary>
    public static class ClientIconLoader
    {
        private static readonly ConcurrentDictionary<string, Bitmap> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient HttpClient;

        static ClientIconLoader()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            HttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            try
            {
                HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
            }
            catch { }
        }

        private static readonly Dictionary<string, string> KnownClientDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            { "nokakoi", "nokakoi.com" },
            { "lumilumi", "lumilumi.app" },
            { "nostter", "nostter.app" },
            { "ehagaki", "ehagaki.com" },
            { "streets", "streets.social" },
            { "coracle", "coracle.social" },
            { "amethyst", "amethyst.social" },
            { "snort", "snort.social" },
            { "primal", "primal.net" },
            { "damus", "damus.io" },
            { "lume", "lume.nu" },
            { "yana", "yana.nostr.band" },
            { "satellite", "satellite.earth" },
            { "habla", "habla.news" },
            { "zapstream", "zap.stream" },
            { "nostrnests", "nostrnests.com" },
            { "wherestr", "wherestr.social" },
            { "flockstr", "flockstr.com" },
            { "highlighter", "highlighter.com" },
            { "stemstr", "stemstr.app" },
            { "nostrudel", "nostrudel.ninja" },
            { "iris", "iris.to" },
            { "yaki", "yakihonne.com" },
            { "yakihonne", "yakihonne.com" },
            { "badgestr", "badgestr.net" },
            { "chorus", "chorus.page" }
        };

        /// <summary>
        /// Attempts to resolve an icon URL from a raw nostrconnect:// URI or client name.
        /// </summary>
        public static string? ResolveIconUrl(string? rawUri, string? clientName)
        {
            if (!string.IsNullOrEmpty(rawUri) && NostrConnectUri.TryParse(rawUri, out var parsed) && parsed != null)
            {
                // 1. Direct image parameter
                if (!string.IsNullOrEmpty(parsed.Image) && IsHttpUrl(parsed.Image))
                    return parsed.Image;

                // 2. Metadata JSON
                if (!string.IsNullOrEmpty(parsed.Metadata))
                {
                    var metaIcon = ExtractIconFromMetadata(parsed.Metadata);
                    if (!string.IsNullOrEmpty(metaIcon) && IsHttpUrl(metaIcon))
                        return metaIcon;

                    var metaUrl = ExtractUrlFromMetadata(parsed.Metadata);
                    if (!string.IsNullOrEmpty(metaUrl) && Uri.TryCreate(metaUrl, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
                        return $"https://www.google.com/s2/favicons?domain={u.Host}&sz=128";
                }

                // 3. Client URL parameter
                if (!string.IsNullOrEmpty(parsed.Url) && Uri.TryCreate(parsed.Url, UriKind.Absolute, out var parsedUri) && !string.IsNullOrEmpty(parsedUri.Host))
                    return $"https://www.google.com/s2/favicons?domain={parsedUri.Host}&sz=128";
            }

            // 4. Resolve by client name or domain
            if (!string.IsNullOrEmpty(clientName))
            {
                var cleanName = clientName.Trim();
                if (KnownClientDomains.TryGetValue(cleanName, out var knownDomain))
                    return $"https://www.google.com/s2/favicons?domain={knownDomain}&sz=128";

                // Substring match for known clients (e.g. "nokakoi test" -> "nokakoi", "Streets" -> "streets")
                var lower = cleanName.ToLowerInvariant();
                foreach (var kvp in KnownClientDomains)
                {
                    if (lower.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        return $"https://www.google.com/s2/favicons?domain={kvp.Value}&sz=128";
                }

                // Check if clientName looks like a domain name (e.g. "example.com" or "app.domain.org")
                if (cleanName.Contains('.') && !cleanName.Contains(' ') && !cleanName.Contains('/') && cleanName.Length >= 4)
                    return $"https://www.google.com/s2/favicons?domain={cleanName.ToLowerInvariant()}&sz=128";
            }

            return null;
        }

        private static bool IsHttpUrl(string url) =>
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static string? ExtractIconFromMetadata(string metadataJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                var root = doc.RootElement;

                // Check "icons" (can be array or string)
                if (root.TryGetProperty("icons", out var iconsProp))
                {
                    if (iconsProp.ValueKind == JsonValueKind.Array && iconsProp.GetArrayLength() > 0)
                    {
                        var first = iconsProp[0].GetString();
                        if (!string.IsNullOrEmpty(first)) return first;
                    }
                    else if (iconsProp.ValueKind == JsonValueKind.String)
                    {
                        var s = iconsProp.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

                // Check "icon", "image", "picture", "logo"
                string[] candidates = ["icon", "image", "picture", "logo"];
                foreach (var candidate in candidates)
                {
                    if (root.TryGetProperty(candidate, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? ExtractUrlFromMetadata(string metadataJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    return urlProp.GetString();
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Loads and applies the client icon asynchronously into an ImageView, falling back to initial TextView.
        /// </summary>
        public static void LoadIcon(Context context, ImageView imageView, TextView fallbackAvatar, string? rawUri, string? clientName)
        {
            var iconUrl = ResolveIconUrl(rawUri, clientName);
            if (string.IsNullOrEmpty(iconUrl))
            {
                imageView.Visibility = ViewStates.Gone;
                fallbackAvatar.Visibility = ViewStates.Visible;
                return;
            }

            imageView.Tag = iconUrl;

            // Check in-memory cache first
            if (MemoryCache.TryGetValue(iconUrl, out var cached) && cached != null && !cached.IsRecycled)
            {
                imageView.SetImageBitmap(cached);
                imageView.Visibility = ViewStates.Visible;
                fallbackAvatar.Visibility = ViewStates.Gone;
                return;
            }

            // Show fallback initially while loading
            imageView.Visibility = ViewStates.Gone;
            fallbackAvatar.Visibility = ViewStates.Visible;

            Task.Run(async () =>
            {
                var bitmap = await FetchCircularBitmapAsync(context, iconUrl).ConfigureAwait(false);
                if (bitmap != null)
                {
                    imageView.Post(() =>
                    {
                        if (string.Equals(imageView.Tag?.ToString(), iconUrl, StringComparison.Ordinal))
                        {
                            imageView.SetImageBitmap(bitmap);
                            imageView.Visibility = ViewStates.Visible;
                            fallbackAvatar.Visibility = ViewStates.Gone;
                        }
                    });
                }
            });
        }

        private static async Task<Bitmap?> FetchCircularBitmapAsync(Context context, string iconUrl)
        {
            try
            {
                // 1. Try disk cache
                var cacheDir = System.IO.Path.Combine(context.CacheDir?.AbsolutePath ?? "", "client_icons");
                System.IO.Directory.CreateDirectory(cacheDir);

                var hash = ComputeHash(iconUrl);
                var filePath = System.IO.Path.Combine(cacheDir, $"{hash}.png");

                if (System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var diskBmp = BitmapFactory.DecodeFile(filePath);
                        if (diskBmp != null)
                        {
                            MemoryCache[iconUrl] = diskBmp;
                            return diskBmp;
                        }
                    }
                    catch { }
                }

                // 2. Fetch from network
                using var response = await HttpClient.GetAsync(iconUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0) return null;

                var rawBitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                if (rawBitmap == null) return null;

                // Crop and round to circular bitmap (target 100x100 for high DPI crispness)
                var circular = CreateCircularBitmap(rawBitmap, 100);
                rawBitmap.Recycle();

                if (circular != null)
                {
                    // Save to disk cache
                    try
                    {
                        using var fs = System.IO.File.OpenWrite(filePath);
                        circular.Compress(Bitmap.CompressFormat.Png, 100, fs);
                    }
                    catch { }

                    MemoryCache[iconUrl] = circular;
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

                // Scale and center-crop the source into target size
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

        private static string ComputeHash(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
