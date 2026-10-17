using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;

namespace nokandro
{
    [Activity(
        Label = "NostrConnect",
        Exported = true,
        ExcludeFromRecents = true,
        TaskAffinity = "",
        LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
        Theme = "@style/AppTheme.TranslucentDialog")]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "nostrconnect")]
    public sealed class NostrConnectDeepLinkActivity : Activity
    {
        private AndroidX.AppCompat.App.AlertDialog? _dialog;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HandleIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            Intent = intent;
            HandleIntent(intent);
        }

        private void HandleIntent(Intent? intent)
        {
            try { _dialog?.Dismiss(); } catch { }
            _dialog = null;

            var data = intent?.DataString ?? intent?.GetStringExtra("nostrconnect_uri");
            if (string.IsNullOrWhiteSpace(data))
            {
                FinishQuietly();
                return;
            }

            if (!NostrConnectUri.TryParse(data, out var parsed) || parsed == null)
            {
                Toast.MakeText(this, "Invalid nostrconnect:// URI (relay and secret required)", ToastLength.Short)?.Show();
                FinishQuietly();
                return;
            }

            if (!NostrSignerOperations.TryLoadPrivateKey(this, out var privKey, out _) || privKey == null || privKey.Length != 32)
            {
                Toast.MakeText(this, "nsec required for nostrconnect", ToastLength.Short)?.Show();
                FinishQuietly();
                return;
            }

            var nsecHex = BitConverter.ToString(privKey).Replace("-", "").ToLowerInvariant();

            // Post notification so user can also re-open the confirmation dialog from notification shade
            SignerApprovalNotifier.NotifyNostrConnectRequest(this, parsed);

            var shortPk = $"{parsed.ClientPubkey[..12]}...";
            var displayName = parsed.GetClientName();
            var relayPreview = parsed.Relays.Length > 0 ? parsed.Relays[0] : "";
            if (relayPreview.Length > 56)
                relayPreview = relayPreview[..56] + "...";

            var message = string.IsNullOrEmpty(displayName)
                ? $"Client: {shortPk}\nRelay: {relayPreview}\n\nConnect this app as a remote signer?"
                : $"Client: {displayName}\n({shortPk})\nRelay: {relayPreview}\n\nConnect this app as a remote signer?";

            var dlg = DialogHelper.CreateBuilder(this)
                .SetTitle("NostrConnect request")
                .SetIcon(Resource.Mipmap.ic_launcher)
                .SetMessage(message)
                .SetCancelable(true)
                .SetPositiveButton("Connect", (sender, args) =>
                {
                    try
                    {
                        SignerApprovalNotifier.CancelNostrConnectNotification(this, parsed.ClientPubkey);
                        SignerApprovalNotifier.StartAndPersistNostrConnectSession(this, parsed.RawUri, nsecHex);
                        var clientLabel = !string.IsNullOrEmpty(displayName) ? displayName : shortPk;
                        Toast.MakeText(this, $"Connected: {clientLabel}", ToastLength.Short)?.Show();
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, "NostrConnect error: " + ex.Message, ToastLength.Short)?.Show();
                    }
                    finally
                    {
                        FinishQuietly();
                    }
                })
                .SetNegativeButton("Cancel", (sender, args) =>
                {
                    SignerApprovalNotifier.CancelNostrConnectNotification(this, parsed.ClientPubkey);
                    FinishQuietly();
                })
                .SetOnCancelListener(new DialogCancelListener(() =>
                {
                    // Dismissed via Back / outside: finish activity, keep notification so user can reopen from notification shade
                    FinishQuietly();
                }))
                .Create();

            _dialog = dlg;
            dlg.Show();
            DialogHelper.ApplyThemeColors(dlg, this);
        }

        private void FinishQuietly()
        {
            Finish();
#pragma warning disable CA1422
            try { OverridePendingTransition(0, 0); } catch { }
#pragma warning restore CA1422
        }

        protected override void OnDestroy()
        {
            try { _dialog?.Dismiss(); } catch { }
            _dialog = null;
            base.OnDestroy();
        }

        private sealed class DialogCancelListener(Action onCancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
        {
            public void OnCancel(IDialogInterface? dialog) => onCancel();
        }
    }
}
