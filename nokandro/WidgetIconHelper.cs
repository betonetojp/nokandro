using Android.Content;
using Android.Graphics;

namespace nokandro
{
    public static class WidgetIconHelper
    {
        private static Bitmap? _grayscaleIcon;
        private static readonly object _lock = new();

        public static Bitmap? GetGrayscaleLauncherIcon(Context context)
        {
            lock (_lock)
            {
                if (_grayscaleIcon != null && !_grayscaleIcon.IsRecycled)
                {
                    return _grayscaleIcon;
                }

                try
                {
                    var drawable = context.GetDrawable(Resource.Mipmap.ic_launcher);
                    if (drawable == null) return null;

                    var width = drawable.IntrinsicWidth;
                    var height = drawable.IntrinsicHeight;
                    if (width <= 0) width = 96;
                    if (height <= 0) height = 96;

                    var bmp = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
                    var canvas = new Canvas(bmp);
                    drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
                    drawable.Draw(canvas);

                    var bmpGray = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
                    var c = new Canvas(bmpGray);
                    var paint = new Paint();
                    var cm = new ColorMatrix();
                    cm.SetSaturation(0);
                    paint.SetColorFilter(new ColorMatrixColorFilter(cm));
                    c.DrawBitmap(bmp, 0, 0, paint);
                    bmp.Recycle();

                    _grayscaleIcon = bmpGray;
                    return _grayscaleIcon;
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
