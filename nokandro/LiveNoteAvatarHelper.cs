using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using IOPath = System.IO.Path;

namespace nokandro
{
    public static class LiveNoteAvatarHelper
    {
        private static readonly ConcurrentDictionary<string, Bitmap> CompositeCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Bitmap> SmallAvatarCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Android.Net.Uri> SmallAvatarUriCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte> InFlightDownloads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> FailedFetchBackoffUntil = new(StringComparer.OrdinalIgnoreCase);

        public static void ClearMemoryCache()
        {
            CompositeCache.Clear();
            SmallAvatarCache.Clear();
            SmallAvatarUriCache.Clear();
        }

        internal static void InvalidateMemoryCacheForPubkey(string pubkeyHex)
        {
            if (string.IsNullOrWhiteSpace(pubkeyHex)) return;
            var prefix = $"{pubkeyHex}_";
            foreach (var key in CompositeCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    CompositeCache.TryRemove(key, out _);
            }
            foreach (var key in SmallAvatarCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    SmallAvatarCache.TryRemove(key, out _);
            }
            foreach (var key in SmallAvatarUriCache.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    SmallAvatarUriCache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Renders (if needed) the 32dp circular avatar to a small PNG file under
        /// cache/profile_avatars/widget_small/ and returns a versioned ContentProvider URI.
        /// Each pubkey and avatar revision gets a distinct file name so Android's
        /// ImageView.setImageURI never serves a stale or recycled image.
        /// </summary>
        public static Android.Net.Uri? GetOrCreateSmallCircularAvatarUri(Context context, string pubkeyHex, string? authorLabel, float density)
        {
            try
            {
                UserProfileLoader.EnsureCacheVersion(context);

                var cacheRoot = context.CacheDir?.AbsolutePath;
                if (string.IsNullOrEmpty(cacheRoot)) return null;

                var avatarsDir = IOPath.Combine(cacheRoot, "profile_avatars");
                var smallDir = IOPath.Combine(avatarsDir, "widget_small");
                Directory.CreateDirectory(smallDir);

                var safePubkey = NormalizeSafeHex(pubkeyHex);
                var srcFilePath = !string.IsNullOrEmpty(pubkeyHex)
                    ? IOPath.Combine(avatarsDir, $"{pubkeyHex}_src.png")
                    : null;
                var hasSrc = !string.IsNullOrEmpty(srcFilePath) && File.Exists(srcFilePath);

                string fileName;
                string uriCacheKey;
                if (hasSrc)
                {
                    var mtimeTicks = File.GetLastWriteTimeUtc(srcFilePath!).Ticks;
                    fileName = $"p_{safePubkey}_{mtimeTicks:x}.png";
                    uriCacheKey = $"{pubkeyHex}_src_{mtimeTicks:x}";
                }
                else
                {
                    var initial = "?";
                    if (!string.IsNullOrWhiteSpace(authorLabel))
                        initial = authorLabel.Trim().Substring(0, 1).ToUpperInvariant();
                    else if (!string.IsNullOrWhiteSpace(pubkeyHex))
                        initial = pubkeyHex.Substring(0, 1).ToUpperInvariant();

                    var initialCode = ComputeShortHash($"{safePubkey}_{initial}");
                    fileName = $"f_{safePubkey}_{initialCode:x8}.png";
                    uriCacheKey = $"{pubkeyHex}_fallback_{initialCode:x8}";
                }

                var outPath = IOPath.Combine(smallDir, fileName);
                if (SmallAvatarUriCache.TryGetValue(uriCacheKey, out var cachedUri) && File.Exists(outPath))
                {
                    return cachedUri;
                }

                if (!File.Exists(outPath))
                {
                    Bitmap? rawSrc = null;
                    if (hasSrc)
                    {
                        try { rawSrc = BitmapFactory.DecodeFile(srcFilePath); } catch { }
                    }

                    using var bmp = RenderSmallCircular(rawSrc, authorLabel, pubkeyHex, density);
                    rawSrc?.Recycle();

                    var tmpPath = outPath + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        using (var fs = File.Create(tmpPath))
                        {
                            bmp.Compress(Bitmap.CompressFormat.Png!, 100, fs);
                        }
                        File.Move(tmpPath, outPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tmpPath))
                        {
                            try { File.Delete(tmpPath); } catch { }
                        }
                    }

                    // Clean up older rendered revisions for this pubkey to avoid unbounded cache growth
                    try
                    {
                        var stalePrefix = hasSrc ? $"p_{safePubkey}_" : $"f_{safePubkey}_";
                        foreach (var existing in Directory.EnumerateFiles(smallDir, $"{stalePrefix}*.png"))
                        {
                            if (!string.Equals(existing, outPath, StringComparison.OrdinalIgnoreCase))
                            {
                                try { File.Delete(existing); } catch { }
                            }
                        }
                    }
                    catch { }
                }

                var uri = Android.Net.Uri.Parse($"content://{WidgetAvatarProvider.Authority}/{fileName}");
                if (uri != null)
                {
                    SmallAvatarUriCache[uriCacheKey] = uri;
                }
                return uri;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LiveNoteAvatarHelper", $"GetOrCreateSmallCircularAvatarUri failed: {ex.Message}");
                return null;
            }
        }

        private static string NormalizeSafeHex(string? pubkeyHex)
        {
            if (string.IsNullOrEmpty(pubkeyHex)) return "anon";
            Span<char> buf = stackalloc char[Math.Min(pubkeyHex.Length, 64)];
            int len = 0;
            for (int i = 0; i < pubkeyHex.Length && len < buf.Length; i++)
            {
                var c = char.ToLowerInvariant(pubkeyHex[i]);
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                    buf[len++] = c;
            }
            return len > 0 ? new string(buf[..len]) : "anon";
        }

        private static uint ComputeShortHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var ch in value)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        public static Bitmap GetOrCreateSmallCircularAvatar(Context context, string pubkeyHex, string? petname, float density)
        {
            UserProfileLoader.EnsureCacheVersion(context);

            var cacheKey = $"{pubkeyHex}_{petname ?? ""}";
            if (SmallAvatarCache.TryGetValue(cacheKey, out var cached) && cached != null && !cached.IsRecycled)
            {
                return cached;
            }

            Bitmap? rawSrc = null;
            if (!string.IsNullOrEmpty(pubkeyHex))
            {
                var cacheDir = IOPath.Combine(context.CacheDir?.AbsolutePath ?? "", "profile_avatars");
                var srcFilePath = IOPath.Combine(cacheDir, $"{pubkeyHex}_src.png");
                if (File.Exists(srcFilePath))
                {
                    try
                    {
                        rawSrc = BitmapFactory.DecodeFile(srcFilePath);
                    }
                    catch { }
                }
            }

            var bmp = RenderSmallCircular(rawSrc, petname, pubkeyHex, density);
            rawSrc?.Recycle();

            SmallAvatarCache[cacheKey] = bmp;
            return bmp;
        }

        public static Bitmap GetOrCreateAvatar(Context context, string pubkeyHex, string? petname, float density)
        {
            UserProfileLoader.EnsureCacheVersion(context);

            var cacheKey = $"{pubkeyHex}_{petname ?? ""}";
            if (CompositeCache.TryGetValue(cacheKey, out var cached) && cached != null && !cached.IsRecycled)
            {
                return cached;
            }

            Bitmap? rawSrc = null;
            if (!string.IsNullOrEmpty(pubkeyHex))
            {
                var cacheDir = IOPath.Combine(context.CacheDir?.AbsolutePath ?? "", "profile_avatars");
                var srcFilePath = IOPath.Combine(cacheDir, $"{pubkeyHex}_src.png");
                if (File.Exists(srcFilePath))
                {
                    try
                    {
                        rawSrc = BitmapFactory.DecodeFile(srcFilePath);
                    }
                    catch { }
                }
            }

            var composite = RenderComposite(context, rawSrc, petname, pubkeyHex, density);
            rawSrc?.Recycle();

            CompositeCache[cacheKey] = composite;
            return composite;
        }

        public static void TriggerAvatarDownloadIfNeeded(Context context, string pubkeyHex, string? preferredRelay, Action onDownloaded)
        {
            if (string.IsNullOrEmpty(pubkeyHex)) return;
            UserProfileLoader.EnsureCacheVersion(context);

            var cacheDir = IOPath.Combine(context.CacheDir?.AbsolutePath ?? "", "profile_avatars");
            Directory.CreateDirectory(cacheDir);
            var srcFilePath = IOPath.Combine(cacheDir, $"{pubkeyHex}_src.png");

            var srcExists = File.Exists(srcFilePath);
            var prevMeta = UserProfileLoader.GetCachedProfileMeta(context, pubkeyHex);
            var isFresh = UserProfileLoader.IsProfileCacheFresh(context, pubkeyHex);
            if (isFresh && (srcExists || string.IsNullOrEmpty(prevMeta?.PictureUrl)))
                return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (FailedFetchBackoffUntil.TryGetValue(pubkeyHex, out var backoffUntil) && now < backoffUntil)
                return;

            if (!InFlightDownloads.TryAdd(pubkeyHex, 0)) return;
            var appCtx = context.ApplicationContext ?? context;

            Task.Run(async () =>
            {
                try
                {
                    var (found, picUrl, displayName) = await UserProfileLoader.FetchProfileAsync(pubkeyHex, preferredRelay).ConfigureAwait(false);
                    if (!found)
                    {
                        // Retry after 5 minutes on transient network failure
                        FailedFetchBackoffUntil[pubkeyHex] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300;
                        return;
                    }

                    FailedFetchBackoffUntil.TryRemove(pubkeyHex, out _);

                    var prevName = prevMeta?.DisplayName;
                    var prevPic = prevMeta?.PictureUrl;
                    var nameChanged = !string.Equals(prevName ?? "", displayName ?? "", StringComparison.Ordinal);
                    var picChanged = !string.Equals(prevPic ?? "", picUrl ?? "", StringComparison.Ordinal);
                    var currentSrcExists = File.Exists(srcFilePath);

                    UserProfileLoader.SaveProfileMeta(appCtx, pubkeyHex, displayName, picUrl);

                    var avatarUpdated = false;
                    if (!string.IsNullOrEmpty(picUrl) && (picChanged || !currentSrcExists))
                    {
                        var avatar = await UserProfileLoader.DownloadAndProcessAvatarAsync(picUrl, srcFilePath, appCtx).ConfigureAwait(false);
                        if (avatar != null)
                        {
                            avatar.Recycle();
                            InvalidateMemoryCacheForPubkey(pubkeyHex);
                            UserProfileLoader.InvalidateAvatarMemoryCache(pubkeyHex);
                            avatarUpdated = true;
                        }
                    }
                    else if (string.IsNullOrEmpty(picUrl) && picChanged && currentSrcExists)
                    {
                        try { File.Delete(srcFilePath); } catch { }
                        InvalidateMemoryCacheForPubkey(pubkeyHex);
                        UserProfileLoader.InvalidateAvatarMemoryCache(pubkeyHex);
                        avatarUpdated = true;
                    }
                    else if (nameChanged && !currentSrcExists)
                    {
                        InvalidateMemoryCacheForPubkey(pubkeyHex);
                    }

                    if (avatarUpdated || nameChanged)
                    {
                        onDownloaded?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Debug("LiveNoteAvatarHelper", $"Profile fetch failed: {ex.Message}");
                }
                finally
                {
                    InFlightDownloads.TryRemove(pubkeyHex, out _);
                }
            });
        }

        private static Bitmap RenderComposite(Context context, Bitmap? rawSrc, string? petname, string? pubkeyHex, float density)
        {
            var canvasSize = (int)(52 * density);
            var circleDiam = (int)(42 * density);
            var circleRadius = circleDiam / 2f;
            var circleCenterX = canvasSize / 2f;
            var circleCenterY = (int)(4 * density) + circleRadius;

            var result = Bitmap.CreateBitmap(canvasSize, canvasSize, Bitmap.Config.Argb8888!);
            var canvas = new Canvas(result);

            // 1. Draw circular avatar
            if (rawSrc != null && !rawSrc.IsRecycled)
            {
                var shader = new BitmapShader(rawSrc, Shader.TileMode.Clamp, Shader.TileMode.Clamp);
                var matrix = new Matrix();
                var scale = Math.Max((float)circleDiam / rawSrc.Width, (float)circleDiam / rawSrc.Height);
                var dx = circleCenterX - (rawSrc.Width * scale) / 2f;
                var dy = circleCenterY - (rawSrc.Height * scale) / 2f;
                matrix.SetScale(scale, scale);
                matrix.PostTranslate(dx, dy);
                shader.SetLocalMatrix(matrix);

                using var avatarPaint = new Paint(PaintFlags.AntiAlias);
                avatarPaint.SetShader(shader);
                canvas.DrawCircle(circleCenterX, circleCenterY, circleRadius, avatarPaint);

                // Border
                using var borderPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(60, 255, 255, 255),
                    StrokeWidth = 1f * density
                };
                borderPaint.SetStyle(Paint.Style.Stroke);
                canvas.DrawCircle(circleCenterX, circleCenterY, circleRadius - 0.5f * density, borderPaint);
            }
            else
            {
                // Fallback circular initial
                using var bgPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(255, 45, 55, 72) // Slate 700
                };
                bgPaint.SetStyle(Paint.Style.Fill);
                canvas.DrawCircle(circleCenterX, circleCenterY, circleRadius, bgPaint);

                using var borderPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(50, 255, 255, 255),
                    StrokeWidth = 1f * density
                };
                borderPaint.SetStyle(Paint.Style.Stroke);
                canvas.DrawCircle(circleCenterX, circleCenterY, circleRadius - 0.5f * density, borderPaint);

                // Initial character
                var initial = "?";
                if (!string.IsNullOrWhiteSpace(petname))
                    initial = petname.Substring(0, 1).ToUpperInvariant();
                else if (!string.IsNullOrWhiteSpace(pubkeyHex))
                    initial = pubkeyHex.Substring(0, 1).ToUpperInvariant();

                using var textPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.White,
                    TextSize = 16f * density,
                    TextAlign = Paint.Align.Center
                };
                textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
                var fontMetrics = textPaint.GetFontMetrics();
                var textBaseline = circleCenterY - (fontMetrics.Descent + fontMetrics.Ascent) / 2f;
                canvas.DrawText(initial, circleCenterX, textBaseline, textPaint);
            }

            // 2. Draw Petname Pill Badge if present
            if (!string.IsNullOrWhiteSpace(petname))
            {
                var pillHeight = (int)(15 * density);
                var maxPillWidth = (int)(48 * density);
                var pillTop = (int)(4 * density) + circleDiam - (int)(11 * density); // overlap bottom 11dp of avatar
                var pillBottom = pillTop + pillHeight;

                using var textPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.White,
                    TextSize = 9.5f * density,
                    TextAlign = Paint.Align.Center
                };
                textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));

                // Truncate if exceeds max width
                var displayPetname = petname;
                var textWidth = textPaint.MeasureText(displayPetname);
                var maxTextWidth = maxPillWidth - (8 * density);
                if (textWidth > maxTextWidth)
                {
                    var count = textPaint.BreakText(displayPetname, true, maxTextWidth - (6 * density), null);
                    if (count > 0 && count < displayPetname.Length)
                    {
                        displayPetname = displayPetname.Substring(0, count) + "…";
                        textWidth = textPaint.MeasureText(displayPetname);
                    }
                }

                var pillWidth = Math.Min(maxPillWidth, textWidth + (8 * density));
                var pillLeft = circleCenterX - (pillWidth / 2f);
                var pillRight = pillLeft + pillWidth;
                var pillRect = new RectF(pillLeft, pillTop, pillRight, pillBottom);

                // Pill background
                using var pillBgPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(230, 16, 20, 28) // Deep translucent dark
                };
                pillBgPaint.SetStyle(Paint.Style.Fill);
                canvas.DrawRoundRect(pillRect, 7.5f * density, 7.5f * density, pillBgPaint);

                // Pill border
                using var pillStrokePaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(90, 255, 255, 255),
                    StrokeWidth = 0.75f * density
                };
                pillStrokePaint.SetStyle(Paint.Style.Stroke);
                canvas.DrawRoundRect(pillRect, 7.5f * density, 7.5f * density, pillStrokePaint);

                // Draw text
                var fontMetrics = textPaint.GetFontMetrics();
                var textBaseline = pillRect.CenterY() - (fontMetrics.Descent + fontMetrics.Ascent) / 2f;
                canvas.DrawText(displayPetname, pillRect.CenterX(), textBaseline, textPaint);
            }

            return result;
        }

        private static Bitmap RenderSmallCircular(Bitmap? rawSrc, string? petname, string? pubkeyHex, float density)
        {
            var clampedDensity = Math.Clamp(density, 1f, 3f);
            var size = (int)(32 * clampedDensity);
            var radius = size / 2f;
            var result = Bitmap.CreateBitmap(size, size, Bitmap.Config.Argb8888!)!;
            using var canvas = new Canvas(result);

            if (rawSrc != null && !rawSrc.IsRecycled)
            {
                using var shader = new BitmapShader(rawSrc, Shader.TileMode.Clamp!, Shader.TileMode.Clamp!);
                using var matrix = new Matrix();
                var scale = Math.Max((float)size / rawSrc.Width, (float)size / rawSrc.Height);
                var dx = radius - (rawSrc.Width * scale) / 2f;
                var dy = radius - (rawSrc.Height * scale) / 2f;
                matrix.SetScale(scale, scale);
                matrix.PostTranslate(dx, dy);
                shader.SetLocalMatrix(matrix);

                using var avatarPaint = new Paint(PaintFlags.AntiAlias);
                avatarPaint.SetShader(shader);
                canvas.DrawCircle(radius, radius, radius, avatarPaint);

                using var borderPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(60, 255, 255, 255),
                    StrokeWidth = 1f * clampedDensity
                };
                borderPaint.SetStyle(Paint.Style.Stroke);
                canvas.DrawCircle(radius, radius, radius - 0.5f * clampedDensity, borderPaint);
            }
            else
            {
                using var bgPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.Argb(255, 45, 55, 72)
                };
                bgPaint.SetStyle(Paint.Style.Fill);
                canvas.DrawCircle(radius, radius, radius, bgPaint);

                var initial = "?";
                if (!string.IsNullOrWhiteSpace(petname))
                    initial = petname.Substring(0, 1).ToUpperInvariant();
                else if (!string.IsNullOrWhiteSpace(pubkeyHex))
                    initial = pubkeyHex.Substring(0, 1).ToUpperInvariant();

                using var textPaint = new Paint(PaintFlags.AntiAlias)
                {
                    Color = Color.White,
                    TextSize = 13f * clampedDensity,
                    TextAlign = Paint.Align.Center
                };
                textPaint.SetTypeface(Typeface.Create(Typeface.Default, TypefaceStyle.Bold));
                var fm = textPaint.GetFontMetrics();
                if (fm != null)
                {
                    var baseline = radius - (fm.Descent + fm.Ascent) / 2f;
                    canvas.DrawText(initial, radius, baseline, textPaint);
                }
            }

            return result;
        }
    }
}
