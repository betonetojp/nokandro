using Android.Content;
using Android.Database;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nokandro
{
    public abstract class NostrSignerProviderBase : ContentProvider
    {
        private const string PREFS_NAME = "nokandro_prefs";
        private const string PREF_NSEC = "pref_nsec";

        protected abstract string Authority { get; }

        public override bool OnCreate() => true;

        public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
        {
            try
            {
                var args = projection ?? Array.Empty<string>();
                if (!TryLoadPrivateKey(Context, out var privKey) || privKey == null)
                    return Reject("nsec not configured");

                return Authority switch
                {
                    "com.nokakoi.nokandro.GET_PUBLIC_KEY" => SingleColumn(Columns.Result, GetPublicKey(privKey)),
                    "com.nokakoi.nokandro.SIGN_EVENT" => SignEvent(args, privKey),
                    "com.nokakoi.nokandro.NIP04_ENCRYPT" => SingleColumn(Columns.Result, Encrypt(args, privKey, nip44: false)),
                    "com.nokakoi.nokandro.NIP44_ENCRYPT" => SingleColumn(Columns.Result, Encrypt(args, privKey, nip44: true)),
                    "com.nokakoi.nokandro.NIP04_DECRYPT" => SingleColumn(Columns.Result, Decrypt(args, privKey, nip44: false)),
                    "com.nokakoi.nokandro.NIP44_DECRYPT" => SingleColumn(Columns.Result, Decrypt(args, privKey, nip44: true)),
                    "com.nokakoi.nokandro.DECRYPT_ZAP_EVENT" => SingleColumn(Columns.Result, GetArg(args, 0)),
                    _ => Reject($"unsupported authority: {Authority}")
                };
            }
            catch (Exception ex)
            {
                return Reject(ex.Message);
            }
        }

        public override string? GetType(Android.Net.Uri? uri) => null;
        public override Android.Net.Uri? Insert(Android.Net.Uri? uri, ContentValues? values) => null;
        public override int Delete(Android.Net.Uri? uri, string? selection, string[]? selectionArgs) => 0;
        public override int Update(Android.Net.Uri? uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;

        private static ICursor Reject(string message)
        {
            var cursor = new MatrixCursor([Columns.Result, Columns.Error]);
            cursor.AddRow([string.Empty, message]);
            return cursor;
        }

        private static ICursor SingleColumn(string column, string value)
        {
            var cursor = new MatrixCursor([column]);
            cursor.AddRow([value]);
            return cursor;
        }

        private static ICursor SignEvent(string[] args, byte[] privKey)
        {
            var eventJson = GetArg(args, 0);
            if (string.IsNullOrEmpty(eventJson)) return Reject("missing event");

            using var evDoc = JsonDocument.Parse(eventJson);
            var ev = evDoc.RootElement;
            var kind = ev.TryGetProperty("kind", out var kEl) ? kEl.GetInt32() : 1;
            var createdAt = ev.TryGetProperty("created_at", out var ctEl) ? ctEl.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var content = ev.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
            var tags = ev.TryGetProperty("tags", out var tEl) ? tEl.GetRawText() : "[]";

            var pubkeyBytes = NostrCrypto.GetPublicKey(privKey);
            var pubkeyHex = BitConverter.ToString(pubkeyBytes).Replace("-", string.Empty).ToLowerInvariant();
            var eventId = ComputeEventId(pubkeyHex, createdAt, kind, tags, content);
            var sig = NostrCrypto.Sign(eventId, privKey);
            var signedEvent = $"{{\"id\":\"{BytesToHex(eventId)}\",\"pubkey\":\"{pubkeyHex}\",\"created_at\":{createdAt},\"kind\":{kind},\"tags\":{tags},\"content\":{EscapeJsonString(content)},\"sig\":\"{BytesToHex(sig)}\"}}";

            var cursor = new MatrixCursor([Columns.Result, Columns.Event]);
            cursor.AddRow([BytesToHex(sig), signedEvent]);
            return cursor;
        }

        private static string Encrypt(string[] args, byte[] privKey, bool nip44)
        {
            var payload = GetArg(args, 0);
            var pubkey = GetArg(args, 1);
            if (string.IsNullOrEmpty(pubkey)) throw new ArgumentException("missing pubkey");
            return nip44
                ? NostrCrypto.EncryptNip44(payload, pubkey, privKey)
                : NostrCrypto.EncryptNip04(payload, pubkey, privKey);
        }

        private static string Decrypt(string[] args, byte[] privKey, bool nip44)
        {
            var payload = GetArg(args, 0);
            var pubkey = GetArg(args, 1);
            if (string.IsNullOrEmpty(pubkey)) throw new ArgumentException("missing pubkey");
            return NostrCrypto.Decrypt(payload, pubkey, privKey)
                ?? throw new InvalidOperationException(nip44 ? "NIP-44 decryption failed" : "NIP-04 decryption failed");
        }

        private static string GetArg(string[] args, int index) => index >= 0 && index < args.Length ? (args[index] ?? string.Empty) : string.Empty;

        private static bool TryLoadPrivateKey(Context? context, out byte[]? privKey)
        {
            privKey = null;
            try
            {
                if (context == null) return false;
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
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

        private static string GetPublicKey(byte[] privKey)
        {
            var pubBytes = NostrCrypto.GetPublicKey(privKey);
            return BitConverter.ToString(pubBytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static class Columns
        {
            public const string Result = "result";
            public const string Event = "event";
            public const string Error = "error";
        }
    }

    [ContentProvider(["com.nokakoi.nokandro.GET_PUBLIC_KEY"], Exported = true)]
    public sealed class NostrSignerGetPublicKeyProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.GET_PUBLIC_KEY"; }

    [ContentProvider(["com.nokakoi.nokandro.SIGN_EVENT"], Exported = true)]
    public sealed class NostrSignerSignEventProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.SIGN_EVENT"; }

    [ContentProvider(["com.nokakoi.nokandro.NIP04_ENCRYPT"], Exported = true)]
    public sealed class NostrSignerNip04EncryptProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.NIP04_ENCRYPT"; }

    [ContentProvider(["com.nokakoi.nokandro.NIP44_ENCRYPT"], Exported = true)]
    public sealed class NostrSignerNip44EncryptProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.NIP44_ENCRYPT"; }

    [ContentProvider(["com.nokakoi.nokandro.NIP04_DECRYPT"], Exported = true)]
    public sealed class NostrSignerNip04DecryptProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.NIP04_DECRYPT"; }

    [ContentProvider(["com.nokakoi.nokandro.NIP44_DECRYPT"], Exported = true)]
    public sealed class NostrSignerNip44DecryptProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.NIP44_DECRYPT"; }

    [ContentProvider(["com.nokakoi.nokandro.DECRYPT_ZAP_EVENT"], Exported = true)]
    public sealed class NostrSignerDecryptZapEventProvider : NostrSignerProviderBase { protected override string Authority => "com.nokakoi.nokandro.DECRYPT_ZAP_EVENT"; }
}
