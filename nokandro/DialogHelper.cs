using Android.App;
using Android.Content;
using Android.Graphics;

namespace nokandro
{
    internal static class DialogHelper
    {
        internal static void ApplyThemeColors(AlertDialog? dialog, Context context)
        {
            if (dialog == null) return;
            try
            {
                var primary = new Color(context.GetColor(Resource.Color.primary));
                var secondary = new Color(context.GetColor(Resource.Color.on_surface_variant));
                dialog.GetButton((int)DialogButtonType.Positive)?.SetTextColor(primary);
                dialog.GetButton((int)DialogButtonType.Negative)?.SetTextColor(secondary);
            }
            catch { }
        }

        internal static Color OnSurfaceColor(Context context) =>
            new(context.GetColor(Resource.Color.on_surface));

        internal static Color OnSurfaceVariantColor(Context context) =>
            new(context.GetColor(Resource.Color.on_surface_variant));
    }
}
