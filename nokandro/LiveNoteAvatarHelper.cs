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

        public static Bitmap GetOrCreateAvatar(Context context, string pubkeyHex, string? petname, float density)
        {
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

            var cacheDir = IOPath.Combine(context.CacheDir?.AbsolutePath ?? "", "profile_avatars");
            Directory.CreateDirectory(cacheDir);
            var srcFilePath = IOPath.Combine(cacheDir, $"{pubkeyHex}_src.png");
            if (File.Exists(srcFilePath)) return;

            Task.Run(async () =>
            {
                try
                {
                    var picUrl = await UserProfileLoader.FetchPictureUrlAsync(pubkeyHex, preferredRelay).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(picUrl)) return;

                    var avatar = await UserProfileLoader.DownloadAndProcessAvatarAsync(picUrl, srcFilePath, context).ConfigureAwait(false);
                    avatar?.Recycle();

                    // Invalidate composite cache for this pubkey
                    var prefix = $"{pubkeyHex}_";
                    foreach (var key in CompositeCache.Keys)
                    {
                        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            CompositeCache.TryRemove(key, out _);
                        }
                    }

                    onDownloaded?.Invoke();
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Debug("LiveNoteAvatarHelper", $"Avatar fetch failed: {ex.Message}");
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
    }
}
