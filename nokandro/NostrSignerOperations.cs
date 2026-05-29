using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;

namespace nokandro
{
    internal static class NostrSignerOperations
    {
        private const string PrefsName = "nokandro_prefs";
        private const string PrefNsec = "pref_nsec";

        public static bool TryLoadPrivateKey(Context context, out byte[]? privKey, out string pubkeyHex)
        {
            privKey = null;
            pubkeyHex = "";
            try
            {
                var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
                var nsec = prefs?.GetString(PrefNsec, null);
                if (string.IsNullOrWhiteSpace(nsec)) return false;

                var (hrp, data) = Bech32Decode(nsec.Trim());
                if (hrp != "nsec" || data == null) return false;

                var bytes = ConvertBits(data, 5, 8, false);
                if (bytes == null || bytes.Length != 32) return false;

                privKey = bytes;
                var pubBytes = NostrCrypto.GetPublicKey(bytes);
                pubkeyHex = BitConverter.ToString(pubBytes).Replace("-", "").ToLowerInvariant();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void ValidateCurrentUser(string? currentUser, string pubkeyHex)
        {
            if (string.IsNullOrEmpty(currentUser)) return;
            var normalized = currentUser.Trim().ToLowerInvariant();
            if (normalized.Length == 64 && normalized != pubkeyHex)
                throw new UnauthorizedAccessException("current_user does not match signer pubkey");
        }

        public static string GetPublicKey(byte[] privKey) =>
            BitConverter.ToString(NostrCrypto.GetPublicKey(privKey)).Replace("-", "").ToLowerInvariant();

        public static string SignEvent(string unsignedJson, byte[] privKey) =>
            Nip46Json.SignUnsignedEvent(unsignedJson, GetPublicKey(privKey), privKey);

        public static string GetSignature(string signedEventJson)
        {
            using var doc = JsonDocument.Parse(signedEventJson);
            if (doc.RootElement.TryGetProperty("sig", out var sigEl))
                return sigEl.GetString() ?? "";
            throw new InvalidOperationException("signed event is missing sig");
        }

        public static string Encrypt(string plaintext, string pubkey, byte[] privKey, bool nip44) =>
            nip44 ? NostrCrypto.EncryptNip44(plaintext, pubkey, privKey) : NostrCrypto.EncryptNip04(plaintext, pubkey, privKey);

        public static string Decrypt(string ciphertext, string pubkey, byte[] privKey, bool nip44) =>
            (nip44 ? NostrCrypto.DecryptNip44(ciphertext, pubkey, privKey) : NostrCrypto.Decrypt(ciphertext, pubkey, privKey))
            ?? throw new InvalidOperationException("decryption failed");

        public static string FormatWebResult(string returnType, string compressionType, string result, string? signedEvent)
        {
            var payload = returnType.Equals("event", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(signedEvent)
                ? signedEvent
                : result;

            if (compressionType.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                && returnType.Equals("event", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = Encoding.UTF8.GetBytes(payload);
                using var ms = new MemoryStream();
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal))
                    gz.Write(bytes, 0, bytes.Length);
                return "Signer1" + Convert.ToBase64String(ms.ToArray());
            }

            return payload;
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
            if (pad) { if (bits > 0) result.Add((byte)((acc << (toBits - bits)) & maxv)); }
            else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0) return null;
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
                    if (((top >> i) & 1) != 0) chk ^= generators[i];
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
    }
}
