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
                var nsec = SecurePreferences.GetNsec(context);
                if (string.IsNullOrWhiteSpace(nsec)) return false;

                var (hrp, data) = NostrKeyDecoder.Bech32Decode(nsec.Trim());
                if (hrp != "nsec" || data == null) return false;

                var bytes = NostrKeyDecoder.ConvertBits(data, 5, 8, false);
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


    }
}
