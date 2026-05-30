using Android.Content;
using AndroidUri = Android.Net.Uri;

namespace nokandro
{
    internal static class NostrSignerProviderPrompt
    {
        private const string ExtraCallerPackage = "caller_package";
        private static readonly object DebounceLock = new();
        private static string? _lastKey;
        private static long _lastUtcMs;
        private const int DebounceMs = 3000;

        public static void TryStartApproval(Context context, string method, string[]? projection, string? callingPackage)
        {
            if (string.IsNullOrEmpty(callingPackage)) return;

            var key = callingPackage + ":" + method;
            lock (DebounceLock)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (_lastKey == key && now - _lastUtcMs < DebounceMs)
                    return;
                _lastKey = key;
                _lastUtcMs = now;
            }

            try
            {
                var intent = BuildApprovalIntent(context, method, projection, callingPackage);
                context.StartActivity(intent);
            }
            catch
            {
                // ignore if activity cannot start
            }
        }

        private static Intent BuildApprovalIntent(Context context, string method, string[]? projection, string callingPackage)
        {
            var args = projection ?? [];
            var payload = GetArg(args, 0);
            var dataUri = string.IsNullOrEmpty(payload)
                ? AndroidUri.Parse("nostrsigner:")
                : AndroidUri.Parse("nostrsigner:" + global::System.Uri.EscapeDataString(payload));

            var intent = new Intent(context, typeof(NostrSignerActivity));
            intent.SetAction(Intent.ActionView);
            intent.SetData(dataUri);
            intent.PutExtra("type", method);
            intent.PutExtra(ExtraCallerPackage, callingPackage);
            intent.PutExtra("id", Guid.NewGuid().ToString("N"));

            var currentUser = GetArg(args, 2);
            if (string.IsNullOrEmpty(currentUser) && method != "get_public_key")
                currentUser = GetArg(args, 1);
            if (!string.IsNullOrEmpty(currentUser))
                intent.PutExtra("current_user", currentUser);

            var peerPubkey = GetArg(args, 1);
            if (method is "nip04_encrypt" or "nip04_decrypt" or "nip44_encrypt" or "nip44_decrypt"
                && !string.IsNullOrEmpty(peerPubkey))
                intent.PutExtra("pubkey", peerPubkey);

            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            return intent;
        }

        private static string GetArg(string[] args, int index) =>
            index >= 0 && index < args.Length ? (args[index] ?? "") : "";
    }
}
