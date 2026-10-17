using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidUri = Android.Net.Uri;

namespace nokandro
{
    [Activity(
        Label = "Nostr Signer",
        Exported = true,
        ExcludeFromRecents = true,
        TaskAffinity = "",
        LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
        Theme = "@style/AppTheme.TranslucentDialog")]
    [IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "nostrsigner")]
    public sealed class NostrSignerActivity : Activity
    {
        private string _requestType = "";
        private string _requestId = "";
        private string _payload = "";
        private string? _callbackUrl;
        private string? _pubkeyParam;
        private string? _currentUser;
        private string? _permissionsJson;
        private string _returnType = "signature";
        private string _compressionType = "none";
        private string? _callerPackage;
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

            if (intent?.DataString == null)
            {
                FinishWithFailure("missing intent data");
                return;
            }

            _callerPackage = intent.GetStringExtra("caller_package")
                ?? CallingActivity?.PackageName
                ?? Referrer?.Host;
            _requestType = GetRequestType(intent) ?? "";
            _requestId = intent.GetStringExtra("id") ?? GetQueryParameterFromDataString(intent.DataString, "id") ?? Guid.NewGuid().ToString("N");
            _callbackUrl = GetCallbackUrl(intent);
            _pubkeyParam = GetPubkey(intent);
            _currentUser = intent.GetStringExtra("current_user") ?? GetQueryParameterFromDataString(intent.DataString, "current_user");
            _permissionsJson = intent.GetStringExtra("permissions");
            _returnType = intent.GetStringExtra("returnType") ?? GetQueryParameterFromDataString(intent.DataString, "returnType") ?? "signature";
            _compressionType = intent.GetStringExtra("compressionType") ?? GetQueryParameterFromDataString(intent.DataString, "compressionType") ?? "none";
            _payload = ExtractPayload(intent.DataString);

            if (string.IsNullOrWhiteSpace(_requestType))
            {
                FinishWithFailure("missing type", _requestId, _callbackUrl);
                return;
            }

            if (!NostrSignerOperations.TryLoadPrivateKey(this, out _, out _))
            {
                FinishWithFailure("nsec not configured", _requestId, _callbackUrl);
                return;
            }

            var kind = _requestType == "sign_event" ? TryGetEventKind(_payload) : null;
            if (Nip55PermissionStore.HasPermission(this, _callerPackage, _requestType, kind)
                && !Nip55PermissionStore.IsAlwaysRejected(this, _callerPackage, _requestType))
            {
                try
                {
                    SignerApprovalNotifier.CancelNip55Notification(this, _callerPackage, _requestType);
                    ExecuteAndDeliver();
                    return;
                }
                catch (Exception ex)
                {
                    FinishWithFailure(ex.Message, _requestId, _callbackUrl);
                    return;
                }
            }

            SignerApprovalNotifier.NotifyNip55Request(this, intent, _callerPackage, _requestType);
            ShowApprovalUi();
        }

        private void ShowApprovalUi()
        {
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var pad = (int)(16 * Resources!.DisplayMetrics!.Density);
            root.SetPadding(pad, pad, pad, pad);

            var detail = new TextView(this)
            {
                Text = $"Sign request from another app\n\nApp: {_callerPackage ?? "unknown"}\nType: {_requestType}\n\n{Truncate(_payload, 400)}"
            };
            detail.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
            root.AddView(detail);

            var remember = new CheckBox(this) { Text = "Remember my choice" };
            root.AddView(remember);

            // AlertDialogでタイトルバーをカスタム
            var dlg = DialogHelper.CreateBuilder(this)
                .SetTitle("nokandro")
                .SetIcon(Resource.Mipmap.ic_launcher)
                .SetView(root)
                .SetCancelable(true)
                .SetPositiveButton("Approve", (s, e) => {
                    try {
                        SignerApprovalNotifier.CancelNip55Notification(this, _callerPackage, _requestType);
                        if (remember.Checked)
                        {
                            if (_requestType == "get_public_key" && !string.IsNullOrEmpty(_permissionsJson))
                                Nip55PermissionStore.GrantFromPermissionsJson(this, _callerPackage, _permissionsJson);
                            else
                                Nip55PermissionStore.Grant(this, _callerPackage, _requestType, TryGetEventKind(_payload));
                        }
                        ExecuteAndDeliver();
                    } catch (Exception ex) { FinishWithFailure(ex.Message, _requestId, _callbackUrl); }
                })
                .SetNegativeButton("Deny", (s, e) => {
                    SignerApprovalNotifier.CancelNip55Notification(this, _callerPackage, _requestType);
                    if (remember.Checked)
                        Nip55PermissionStore.SetAlwaysRejected(this, _callerPackage, _requestType);
                    DeliverRejection(_requestId, _callbackUrl);
                })
                .SetOnCancelListener(new DialogCancelListener(() =>
                {
                    // Dismissed via Back: keep notification in shade so user can reopen the prompt
                    FinishQuietly();
                }))
                .Create();
            _dialog = dlg;
            dlg.SetCanceledOnTouchOutside(false);
            dlg.Show();
            DialogHelper.ApplyThemeColors(dlg, this);
        }

        private void DeliverRejection(string requestId, string? callbackUrl)
        {
            try
            {
                if (!string.IsNullOrEmpty(callbackUrl))
                {
                    FinishWithFailure("rejected", requestId, callbackUrl);
                    return;
                }
                var resultIntent = new Intent();
                resultIntent.PutExtra("rejected", true);
                if (!string.IsNullOrEmpty(requestId)) resultIntent.PutExtra("id", requestId);
                SetResult(Result.Ok, resultIntent);
            }
            catch { }
            finally
            {
                FinishQuietly();
            }
        }

        private void ExecuteAndDeliver()
        {
            if (!NostrSignerOperations.TryLoadPrivateKey(this, out var privKey, out var pubkeyHex) || privKey == null)
                throw new InvalidOperationException("nsec not configured");

            NostrSignerOperations.ValidateCurrentUser(_currentUser, pubkeyHex);

            string result;
            string? signedEvent = null;

            switch (_requestType)
            {
                case "get_public_key":
                    result = pubkeyHex;
                    DeliverResult(_requestId, result, _callbackUrl, null, PackageName, _returnType, _compressionType);
                    return;
                case "sign_event":
                    signedEvent = NostrSignerOperations.SignEvent(_payload, privKey);
                    result = NostrSignerOperations.GetSignature(signedEvent);
                    break;
                case "nip04_encrypt":
                    result = NostrSignerOperations.Encrypt(_payload, RequirePubkey(), privKey, nip44: false);
                    break;
                case "nip04_decrypt":
                    result = NostrSignerOperations.Decrypt(_payload, RequirePubkey(), privKey, nip44: false);
                    break;
                case "nip44_encrypt":
                    result = NostrSignerOperations.Encrypt(_payload, RequirePubkey(), privKey, nip44: true);
                    break;
                case "nip44_decrypt":
                    result = NostrSignerOperations.Decrypt(_payload, RequirePubkey(), privKey, nip44: true);
                    break;
                case "decrypt_zap_event":
                    result = NostrZapDecrypt.Decrypt(_payload, privKey, pubkeyHex);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported NIP-55 type: {_requestType}");
            }

            DeliverResult(_requestId, result, _callbackUrl, signedEvent, PackageName, _returnType, _compressionType);
        }

        private string RequirePubkey()
        {
            var pk = _pubkeyParam ?? _currentUser;
            if (string.IsNullOrEmpty(pk)) throw new ArgumentException("missing pubkey");
            return pk;
        }

        private static int? TryGetEventKind(string payload)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("kind", out var k)) return k.GetInt32();
            }
            catch { }
            return null;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "…";

        private void DeliverResult(string requestId, string result, string? callbackUrl, string? signedEvent, string? packageName, string returnType, string compressionType)
        {
            try
            {
                if (!string.IsNullOrEmpty(callbackUrl))
                {
                    var formatted = NostrSignerOperations.FormatWebResult(returnType, compressionType, result, signedEvent);
                    var callbackIntent = new Intent(Intent.ActionView, BuildCallbackUri(callbackUrl, formatted, requestId, signedEvent, packageName, null));
                    callbackIntent.AddFlags(ActivityFlags.NewTask);
                    StartActivity(callbackIntent);
                }
                else
                {
                    var resultIntent = new Intent();
                    resultIntent.PutExtra("signature", result);
                    resultIntent.PutExtra("result", result);
                    resultIntent.PutExtra("id", requestId);
                    if (!string.IsNullOrEmpty(signedEvent))
                    {
                        resultIntent.PutExtra("event", signedEvent);
                    }
                    if (!string.IsNullOrEmpty(packageName))
                    {
                        resultIntent.PutExtra("package", packageName);
                    }

                    var builder = new AndroidUri.Builder()
                        .Scheme("nostrsigner")
                        .Authority("result")
                        .AppendQueryParameter("result", result)
                        .AppendQueryParameter("signature", result)
                        .AppendQueryParameter("id", requestId);
                    if (!string.IsNullOrEmpty(signedEvent))
                        builder.AppendQueryParameter("event", signedEvent);
                    if (!string.IsNullOrEmpty(packageName))
                        builder.AppendQueryParameter("package", packageName);
                    resultIntent.SetData(builder.Build());

                    SetResult(Result.Ok, resultIntent);

                    if (CallingActivity == null && GetSystemService(ClipboardService) is ClipboardManager cm)
                        cm.PrimaryClip = ClipData.NewPlainText("nostr-signer-result", result);
                }
            }
            catch
            {
                // ignore delivery errors
            }
            finally
            {
                FinishQuietly();
            }
        }

        private void FinishWithFailure(string error, string requestId = "", string? callbackUrl = null)
        {
            try
            {
                var resultIntent = new Intent();
                resultIntent.PutExtra("error", error);
                if (!string.IsNullOrEmpty(requestId)) resultIntent.PutExtra("id", requestId);
                SetResult(Result.Canceled, resultIntent);

                if (!string.IsNullOrEmpty(callbackUrl))
                {
                    var callbackIntent = new Intent(Intent.ActionView, BuildCallbackUri(callbackUrl, string.Empty, requestId, null, null, error));
                    callbackIntent.AddFlags(ActivityFlags.NewTask);
                    StartActivity(callbackIntent);
                }
                else
                {
                    Toast.MakeText(this, error, ToastLength.Short)?.Show();
                }
            }
            catch { }
            finally
            {
                FinishQuietly();
            }
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

        private static AndroidUri BuildCallbackUri(string callbackUrl, string result, string requestId, string? signedEvent, string? packageName, string? error)
        {
            var builder = new System.Text.StringBuilder(callbackUrl);
            var separator = callbackUrl.Contains('?') ? "&" : "?";
            if (!string.IsNullOrEmpty(result))
            {
                builder.Append(separator).Append("result=").Append(global::System.Uri.EscapeDataString(result));
                separator = "&";
                builder.Append(separator).Append("signature=").Append(global::System.Uri.EscapeDataString(result));
            }
            if (!string.IsNullOrEmpty(requestId))
            {
                builder.Append(separator).Append("id=").Append(global::System.Uri.EscapeDataString(requestId));
                separator = "&";
            }
            if (!string.IsNullOrEmpty(signedEvent))
            {
                builder.Append(separator).Append("event=").Append(global::System.Uri.EscapeDataString(signedEvent));
                separator = "&";
            }
            if (!string.IsNullOrEmpty(packageName))
            {
                builder.Append(separator).Append("package=").Append(global::System.Uri.EscapeDataString(packageName));
                separator = "&";
            }
            if (!string.IsNullOrEmpty(error))
                builder.Append(separator).Append("error=").Append(global::System.Uri.EscapeDataString(error));
            return AndroidUri.Parse(builder.ToString());
        }

        private static string? GetRequestType(Intent intent) =>
            intent.GetStringExtra("type") ?? GetQueryParameterFromDataString(intent.DataString, "type");

        private static string? GetCallbackUrl(Intent intent) =>
            intent.GetStringExtra("callbackUrl") ?? GetQueryParameterFromDataString(intent.DataString, "callbackUrl");

        private static string? GetPubkey(Intent intent) =>
            intent.GetStringExtra("pubkey") ?? GetQueryParameterFromDataString(intent.DataString, "pubkey");

        private static string? GetQueryParameterFromDataString(string? dataString, string key)
        {
            if (string.IsNullOrEmpty(dataString)) return null;
            var qIndex = dataString.IndexOf('?');
            if (qIndex < 0 || qIndex + 1 >= dataString.Length) return null;
            var query = dataString[(qIndex + 1)..];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var k = global::System.Uri.UnescapeDataString(pair[..eq]);
                if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
                return global::System.Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }

        private static string ExtractPayload(string dataString)
        {
            var raw = dataString;
            if (raw.StartsWith("nostrsigner:", StringComparison.OrdinalIgnoreCase))
                raw = raw["nostrsigner:".Length..];
            var queryIndex = FindSignerQueryStart(raw);
            if (queryIndex >= 0) raw = raw[..queryIndex];
            return global::System.Uri.UnescapeDataString(raw);
        }

        private static int FindSignerQueryStart(string raw)
        {
            var markers = new[]
            {
                "?compressionType=", "&compressionType=", "?returnType=", "&returnType=",
                "?type=", "&type=", "?pubkey=", "&pubkey=", "?callbackUrl=", "&callbackUrl=",
                "?id=", "&id=", "?current_user=", "&current_user=", "?permissions=", "&permissions="
            };
            var best = -1;
            foreach (var marker in markers)
            {
                var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (best < 0 || idx < best)) best = idx;
            }
            return best;
        }
    }
}
