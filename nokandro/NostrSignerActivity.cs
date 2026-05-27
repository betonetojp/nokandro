using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AndroidUri = Android.Net.Uri;

namespace nokandro
{
    [Activity(Label = "Nostr Signer", Exported = true, NoHistory = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTop, Theme = "@android:style/Theme.Translucent.NoTitleBar")]
    [IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "nostrsigner")]
    public sealed class NostrSignerActivity : Activity
    {
        private const string PREFS_NAME = "nokandro_prefs";
        private const string PREF_NSEC = "pref_nsec";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HandleIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);
            HandleIntent(intent);
        }

        private void HandleIntent(Intent? intent)
        {
            if (intent?.DataString == null)
            {
                FinishWithFailure("missing intent data");
                return;
            }

            if (!TryLoadPrivateKey(out var privKey) || privKey == null)
            {
                FinishWithFailure("nsec not configured");
                return;
            }

            var requestType = GetRequestType(intent);
            if (string.IsNullOrWhiteSpace(requestType))
            {
                FinishWithFailure("missing type");
                return;
            }

            var requestId = intent.GetStringExtra("id") ?? Guid.NewGuid().ToString("N");
            var callbackUrl = GetCallbackUrl(intent);
            var payload = ExtractPayload(intent.DataString);

            try
            {
                string result;
                string? signedEvent = null;
                string? packageName = null;

                switch (requestType)
                {
                    case "get_public_key":
                        result = GetPublicKey(privKey);
                        packageName = PackageName;
                        break;
                    case "sign_event":
                        signedEvent = SignEvent(payload, privKey);
                        result = GetSignature(signedEvent);
                        break;
                    case "nip04_encrypt":
                        result = Encrypt(payload, intent, privKey, nip44: false);
                        break;
                    case "nip04_decrypt":
                        result = Decrypt(payload, intent, privKey, nip44: false);
                        break;
                    case "nip44_encrypt":
                        result = Encrypt(payload, intent, privKey, nip44: true);
                        break;
                    case "nip44_decrypt":
                        result = Decrypt(payload, intent, privKey, nip44: true);
                        break;
                    case "decrypt_zap_event":
                        result = payload;
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported NIP-55 type: {requestType}");
                }

                DeliverResult(requestId, result, callbackUrl, signedEvent, packageName);
            }
            catch (Exception ex)
            {
                FinishWithFailure(ex.Message, requestId, callbackUrl);
            }
        }

        private void DeliverResult(string requestId, string result, string? callbackUrl, string? signedEvent = null, string? packageName = null)
        {
            try
            {
                var builder = new AndroidUri.Builder()
                    .Scheme("nostrsigner")
                    .Authority("result")
                    .AppendQueryParameter("result", result)
                    .AppendQueryParameter("id", requestId);

                if (!string.IsNullOrEmpty(signedEvent)) builder.AppendQueryParameter("event", signedEvent);
                if (!string.IsNullOrEmpty(packageName)) builder.AppendQueryParameter("package", packageName);

                var resultIntent = new Intent();
                resultIntent.SetData(builder.Build());
                resultIntent.PutExtra("result", result);
                resultIntent.PutExtra("id", requestId);
                if (!string.IsNullOrEmpty(signedEvent)) resultIntent.PutExtra("event", signedEvent);
                if (!string.IsNullOrEmpty(packageName)) resultIntent.PutExtra("package", packageName);
                SetResult(Result.Ok, resultIntent);

                if (!string.IsNullOrEmpty(callbackUrl))
                {
                    var callbackIntent = new Intent(Intent.ActionView, BuildCallbackUri(callbackUrl, result, requestId, signedEvent, packageName));
                    callbackIntent.AddFlags(ActivityFlags.NewTask);
                    StartActivity(callbackIntent);
                }
            }
            catch
            {
                // ignore delivery errors, but still close the activity
            }
            finally
            {
                Finish();
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
            catch
            {
                // ignore
            }
            finally
            {
                Finish();
            }
        }

        private static AndroidUri BuildCallbackUri(string callbackUrl, string result, string requestId, string? signedEvent = null, string? packageName = null, string? error = null)
        {
            var builder = new StringBuilder(callbackUrl);
            var separator = callbackUrl.Contains('?') ? "&" : "?";

            if (!string.IsNullOrEmpty(result))
            {
                builder.Append(separator).Append("result=").Append(global::System.Uri.EscapeDataString(result));
                separator = "&";
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
            {
                builder.Append(separator).Append("error=").Append(global::System.Uri.EscapeDataString(error));
            }

            return AndroidUri.Parse(builder.ToString());
        }

        private static string GetRequestType(Intent intent)
        {
            return intent.GetStringExtra("type")
                ?? GetQueryParameterFromDataString(intent.DataString, "type")
                ?? string.Empty;
        }

        private static string? GetCallbackUrl(Intent intent)
        {
            return intent.GetStringExtra("callbackUrl")
                ?? GetQueryParameterFromDataString(intent.DataString, "callbackUrl");
        }

        private static string? GetPubkey(Intent intent)
        {
            return intent.GetStringExtra("pubkey")
                 ?? GetQueryParameterFromDataString(intent.DataString, "pubkey");
        }

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
            {
                raw = raw["nostrsigner:".Length..];
            }

            var queryIndex = FindSignerQueryStart(raw);
            if (queryIndex >= 0)
            {
                raw = raw[..queryIndex];
            }

            return global::System.Uri.UnescapeDataString(raw);
        }

        private static int FindSignerQueryStart(string raw)
        {
            var markers = new[]
            {
                "?compressionType=", "&compressionType=",
                "?returnType=", "&returnType=",
                "?type=", "&type=",
                "?pubkey=", "&pubkey=",
                "?callbackUrl=", "&callbackUrl=",
                "?id=", "&id=",
                "?current_user=", "&current_user=",
                "?permissions=", "&permissions=",
                "?secret=", "&secret=",
                "?relay=", "&relay=",
                "?metadata=", "&metadata=",
                "?name=", "&name=",
            };

            var best = -1;
            foreach (var marker in markers)
            {
                var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (best < 0 || idx < best))
                {
                    best = idx;
                }
            }

            return best;
        }

        private static string GetPublicKey(byte[] privKey)
        {
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            return BitConverter.ToString(pubBytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string SignEvent(string unsignedJson, byte[] privKey)
        {
            using var evDoc = JsonDocument.Parse(unsignedJson);
            var ev = evDoc.RootElement;

            var kind = ev.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) ? ctEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
            var tags = ev.TryGetProperty("tags", out var tEl) ? tEl.GetRawText() : "[]";

            var pubkeyBytes = NostrCrypto.GetPublicKey(privKey);
            var pubkeyHex = BitConverter.ToString(pubkeyBytes).Replace("-", string.Empty).ToLowerInvariant();
            var eventId = ComputeEventId(pubkeyHex, createdAt, kind, tags, content);
            var sig = NostrCrypto.Sign(eventId, privKey);

            return $"{{\"id\":\"{BytesToHex(eventId)}\",\"pubkey\":\"{pubkeyHex}\",\"created_at\":{createdAt},\"kind\":{kind},\"tags\":{tags},\"content\":{EscapeJsonString(content)},\"sig\":\"{BytesToHex(sig)}\"}}";
        }

        private static string GetSignature(string signedEventJson)
        {
            using var doc = JsonDocument.Parse(signedEventJson);
            if (doc.RootElement.TryGetProperty("sig", out var sigEl))
            {
                return sigEl.GetString() ?? string.Empty;
            }

            throw new InvalidOperationException("signed event is missing sig");
        }

        private static string Encrypt(string plaintext, Intent intent, byte[] privKey, bool nip44)
        {
            var pubkey = GetPubkey(intent);
            if (string.IsNullOrEmpty(pubkey)) throw new ArgumentException("missing pubkey");
            return nip44
                ? NostrCrypto.EncryptNip44(plaintext, pubkey, privKey)
                : NostrCrypto.EncryptNip04(plaintext, pubkey, privKey);
        }

        private static string Decrypt(string ciphertext, Intent intent, byte[] privKey, bool nip44)
        {
            var pubkey = intent.GetStringExtra("pubkey")
                ?? intent.GetStringExtra("current_user")
                ?? GetQueryParameterFromDataString(intent.DataString, "pubkey");
             if (string.IsNullOrEmpty(pubkey)) throw new ArgumentException("missing pubkey");
             return NostrCrypto.Decrypt(ciphertext, pubkey, privKey)
                 ?? throw new InvalidOperationException(nip44 ? "NIP-44 decryption failed" : "NIP-04 decryption failed");
        }

        private bool TryLoadPrivateKey(out byte[]? privKey)
        {
            privKey = null;
            try
            {
                var prefs = GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var nsec = prefs?.GetString(PREF_NSEC, null);
                if (string.IsNullOrWhiteSpace(nsec)) return false;

                var (hrp, data) = Bech32Decode(nsec.Trim());
                if (hrp != "nsec" || data == null) return false;

                var bytes = ConvertBits(data, 5, 8, false);
                if (bytes == null || bytes.Length != 32) return false;

                privKey = bytes;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[]? ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            var acc = 0;
            var bits = 0;
            var maxv = (1 << toBits) - 1;
            var result = new List<byte>();
            foreach (var value in data)
            {
                if ((value >> fromBits) != 0) return null;
                acc = (acc << fromBits) | value;
                bits += fromBits;
                while (bits >= toBits)
                {
                    bits -= toBits;
                    result.Add((byte)((acc >> bits) & maxv));
                }
            }

            if (pad)
            {
                if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv));
            }
            else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
            {
                return null;
            }

            return [.. result];
        }

        private static (string? hrp, byte[]? data) Bech32Decode(string bech)
        {
            const string Bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
            if (string.IsNullOrEmpty(bech)) return (null, null);
            bech = bech.ToLowerInvariant();
            var pos = bech.LastIndexOf('1');
            if (pos < 1 || pos + 7 > bech.Length) return (null, null);
            var hrp = bech[..pos];
            var dataPart = bech[(pos + 1)..];
            var data = new byte[dataPart.Length];
            for (int i = 0; i < dataPart.Length; i++)
            {
                var idx = Bech32Chars.IndexOf(dataPart[i]);
                if (idx == -1) return (null, null);
                data[i] = (byte)idx;
            }

            var values = new List<byte>();
            values.AddRange(HrpExpand(hrp));
            values.AddRange(data);
            if (Polymod([.. values]) != 1) return (null, null);

            var payload = new byte[data.Length - 6];
            Array.Copy(data, 0, payload, 0, payload.Length);
            return (hrp, payload);
        }

        private static int Polymod(byte[] values)
        {
            var chk = 1;
            var generators = new[] { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffff) << 5) ^ v;
                for (int i = 0; i < 5; i++)
                {
                    if (((top >> i) & 1) != 0) chk ^= generators[i];
                }
            }
            return chk;
        }

        private static byte[] HrpExpand(string hrp)
        {
            var hrpBytes = Encoding.ASCII.GetBytes(hrp);
            var expand = new List<byte>(hrpBytes.Length * 2 + 1);
            foreach (var b in hrpBytes) expand.Add((byte)(b >> 5));
            expand.Add(0);
            foreach (var b in hrpBytes) expand.Add((byte)(b & 31));
            return [.. expand];
        }

        private static byte[] ComputeEventId(string pubkey, long createdAt, int kind, string tagsJson, string content)
        {
            var sb = new StringBuilder();
            sb.Append("[0,\"");
            sb.Append(pubkey);
            sb.Append("\",");
            sb.Append(createdAt);
            sb.Append(',');
            sb.Append(kind);
            sb.Append(',');
            sb.Append(tagsJson);
            sb.Append(',');
            sb.Append(EscapeJsonString(content));
            sb.Append(']');

            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string EscapeJsonString(string text)
        {
            if (text == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }
    }
}
