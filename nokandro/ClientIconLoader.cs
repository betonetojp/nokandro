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
    /// Supports direct image URLs, PWA apple-touch-icon, and Google Favicon service with earth-icon filtering.
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
                Timeout = TimeSpan.FromSeconds(6)
            };
            try
            {
                HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 10; Mobile) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36");
            }
            catch { }
        }

        private static readonly Dictionary<string, string> KnownClientTargets = new(StringComparer.OrdinalIgnoreCase)
        {
            { "streets", "streets.eyemono.moe" },
            { "nokakoi", "nokakoi.com" },
            { "lumilumi", "lumilumi.app" },
            { "nostter", "nostter.app" },
            { "ehagaki", "https://lokuyow.github.io/ehagaki/" },
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
        /// Generates a prioritized list of candidate image URLs to fetch for the client.
        /// Supports domain-root and subpath-hosted PWAs (e.g., GitHub Pages).
        /// </summary>
        public static List<string> ResolveIconCandidates(string? rawUri, string? clientName)
        {
            var candidates = new List<string>();
            var targets = new List<string>();

            if (!string.IsNullOrEmpty(rawUri) && NostrConnectUri.TryParse(rawUri, out var parsed) && parsed != null)
            {
                // 1. Direct image parameter
                if (!string.IsNullOrEmpty(parsed.Image) && IsHttpUrl(parsed.Image))
                    candidates.Add(parsed.Image);

                // 2. Metadata JSON icons / images
                if (!string.IsNullOrEmpty(parsed.Metadata))
                {
                    var metaIcon = ExtractIconFromMetadata(parsed.Metadata);
                    if (!string.IsNullOrEmpty(metaIcon) && IsHttpUrl(metaIcon))
                        candidates.Add(metaIcon);

                    var metaUrl = ExtractUrlFromMetadata(parsed.Metadata);
                    if (!string.IsNullOrEmpty(metaUrl) && IsHttpUrl(metaUrl))
                        targets.Add(metaUrl);
                }

                // 3. Client URL parameter
                if (!string.IsNullOrEmpty(parsed.Url) && IsHttpUrl(parsed.Url))
                    targets.Add(parsed.Url);
            }

            // 4. Resolve domain/URL by client name
            if (!string.IsNullOrEmpty(clientName))
            {
                var cleanName = clientName.Trim();
                var lower = cleanName.ToLowerInvariant();

                // Direct or substring match in known clients
                foreach (var kvp in KnownClientTargets)
                {
                    if (lower.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(kvp.Value);
                        break;
                    }
                }

                // Direct HTTP(S) URL or domain format check (e.g. "foo.bar.org")
                if (IsHttpUrl(cleanName))
                {
                    targets.Add(cleanName);
                }
                else if (cleanName.Contains('.') && !cleanName.Contains(' ') && !cleanName.Contains('/') && cleanName.Length >= 4)
                {
                    targets.Add(lower);
                }
            }

            // Expand targets into candidate icon URLs (PWA apple-touch-icon, Google Favicon, and favicon.ico)
            foreach (var target in targets)
            {
                AddTargetCandidates(candidates, target);
            }

            // Return unique candidate URLs
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var c in candidates)
            {
                if (set.Add(c)) result.Add(c);
            }
            return result;
        }

        private static void AddTargetCandidates(List<string> candidates, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;

            target = target.Trim();

            if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1") return;

                var scheme = uri.Scheme.ToLowerInvariant();
                var portPart = uri.IsDefaultPort ? "" : $":{uri.Port}";

                // Subpath PWA handling (e.g. GitHub Pages /repo/)
                var path = uri.AbsolutePath;
                if (!string.IsNullOrEmpty(path) && path != "/")
                {
                    var baseDir = $"{scheme}://{host}{portPart}{path}";
                    if (!baseDir.EndsWith('/'))
                    {
                        var lastSegment = System.IO.Path.GetFileName(path);
                        if (!lastSegment.Contains('.'))
                        {
                            baseDir += "/";
                        }
                        else
                        {
                            var idx = baseDir.LastIndexOf('/');
                            if (idx >= 0)
                                baseDir = baseDir.Substring(0, idx + 1);
                        }
                    }

                    candidates.Add($"{baseDir}apple-touch-icon.png");
                    candidates.Add($"{baseDir}apple-touch-icon-precomposed.png");
                    candidates.Add($"{baseDir}favicon.ico");
                }

                // Domain-root candidates
                candidates.Add($"{scheme}://{host}{portPart}/apple-touch-icon.png");
                candidates.Add($"{scheme}://{host}{portPart}/apple-touch-icon-precomposed.png");
                candidates.Add($"https://www.google.com/s2/favicons?domain={host}&sz=128");
                candidates.Add($"{scheme}://{host}{portPart}/favicon.ico");
            }
            else
            {
                var domain = target.ToLowerInvariant().Trim('/');
                if (domain != "localhost" && domain != "127.0.0.1")
                {
                    candidates.Add($"https://{domain}/apple-touch-icon.png");
                    candidates.Add($"https://{domain}/apple-touch-icon-precomposed.png");
                    candidates.Add($"https://www.google.com/s2/favicons?domain={domain}&sz=128");
                    candidates.Add($"https://{domain}/favicon.ico");
                }
            }
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
            var candidates = ResolveIconCandidates(rawUri, clientName);
            var cacheKey = !string.IsNullOrEmpty(rawUri)
                ? ComputeHash(rawUri)
                : ComputeHash(clientName ?? Guid.NewGuid().ToString());

            imageView.Tag = cacheKey;

            // Check in-memory cache first
            if (MemoryCache.TryGetValue(cacheKey, out var cached) && cached != null && !cached.IsRecycled)
            {
                imageView.SetImageBitmap(cached);
                imageView.Visibility = ViewStates.Visible;
                fallbackAvatar.Visibility = ViewStates.Gone;
                return;
            }

            // Show fallback initially while loading
            imageView.Visibility = ViewStates.Gone;
            fallbackAvatar.Visibility = ViewStates.Visible;

            if (candidates.Count == 0) return;

            Task.Run(async () =>
            {
                var bitmap = await FetchCircularBitmapAsync(context, cacheKey, candidates).ConfigureAwait(false);
                if (bitmap != null)
                {
                    imageView.Post(() =>
                    {
                        if (string.Equals(imageView.Tag?.ToString(), cacheKey, StringComparison.Ordinal))
                        {
                            imageView.SetImageBitmap(bitmap);
                            imageView.Visibility = ViewStates.Visible;
                            fallbackAvatar.Visibility = ViewStates.Gone;
                        }
                    });
                }
            });
        }

        private static async Task<Bitmap?> FetchCircularBitmapAsync(Context context, string cacheKey, List<string> candidates)
        {
            try
            {
                // 1. Try disk cache
                var cacheDir = System.IO.Path.Combine(context.CacheDir?.AbsolutePath ?? "", "client_icons");
                System.IO.Directory.CreateDirectory(cacheDir);

                var filePath = System.IO.Path.Combine(cacheDir, $"{cacheKey}.png");

                if (System.IO.File.Exists(filePath))
                {
                    try
                    {
                        var diskBmp = BitmapFactory.DecodeFile(filePath);
                        if (diskBmp != null)
                        {
                            MemoryCache[cacheKey] = diskBmp;
                            return diskBmp;
                        }
                    }
                    catch { }
                }

                // 2. Fetch from candidate URLs in order
                foreach (var url in candidates)
                {
                    try
                    {
                        using var response = await HttpClient.GetAsync(url).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode) continue;

                        // Skip HTML fallback responses (common on SPAs when asset not found)
                        var mediaType = response.Content.Headers.ContentType?.MediaType;
                        if (!string.IsNullOrEmpty(mediaType) && mediaType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (bytes == null || bytes.Length == 0) continue;

                        var rawBitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                        if (rawBitmap == null) continue;

                        // Filter out Google Favicon's 16x16 default earth icon dummy
                        if (rawBitmap.Width <= 16 && rawBitmap.Height <= 16)
                        {
                            rawBitmap.Recycle();
                            continue;
                        }

                        // Valid icon found! Crop and round to circular bitmap
                        var circular = CreateCircularBitmap(rawBitmap, 100);
                        rawBitmap.Recycle();

                        if (circular != null)
                        {
                            try
                            {
                                using var fs = System.IO.File.OpenWrite(filePath);
                                circular.Compress(Bitmap.CompressFormat.Png, 100, fs);
                            }
                            catch { }

                            MemoryCache[cacheKey] = circular;
                            return circular;
                        }
                    }
                    catch { }
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
