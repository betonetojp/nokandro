using Android.Content;
using Android.Database;
using System.Text.Json;

namespace nokandro
{
    public abstract class NostrSignerProviderBase : ContentProvider
    {
        protected abstract string Authority { get; }

        public override bool OnCreate() => true;

        public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder)
        {
            var package = CallingPackage;
            var method = AuthorityToMethod(Authority);

            if (Nip55PermissionStore.IsAlwaysRejected(Context!, package, method))
                return RejectedCursor();

            if (method != "get_public_key" && !Nip55PermissionStore.HasPermission(Context!, package, method, ExtractEventKind(method, projection)))
                return null;

            try
            {
                if (!NostrSignerOperations.TryLoadPrivateKey(Context!, out var privKey, out var pubkeyHex) || privKey == null)
                    return null;

                var currentUser = GetArg(projection ?? [], 2);
                if (string.IsNullOrEmpty(currentUser) && method != "get_public_key")
                    currentUser = GetArg(projection ?? [], 1);
                NostrSignerOperations.ValidateCurrentUser(currentUser, pubkeyHex);

                return Authority switch
                {
                    "com.nokakoi.nokandro.GET_PUBLIC_KEY" => SingleColumn("result", NostrSignerOperations.GetPublicKey(privKey)),
                    "com.nokakoi.nokandro.SIGN_EVENT" => SignEventCursor(projection ?? [], privKey),
                    "com.nokakoi.nokandro.NIP04_ENCRYPT" => SingleColumn("result", NostrSignerOperations.Encrypt(GetArg(projection ?? [], 0), GetArg(projection ?? [], 1), privKey, nip44: false)),
                    "com.nokakoi.nokandro.NIP44_ENCRYPT" => SingleColumn("result", NostrSignerOperations.Encrypt(GetArg(projection ?? [], 0), GetArg(projection ?? [], 1), privKey, nip44: true)),
                    "com.nokakoi.nokandro.NIP04_DECRYPT" => SingleColumn("result", NostrSignerOperations.Decrypt(GetArg(projection ?? [], 0), GetArg(projection ?? [], 1), privKey, nip44: false)),
                    "com.nokakoi.nokandro.NIP44_DECRYPT" => SingleColumn("result", NostrSignerOperations.Decrypt(GetArg(projection ?? [], 0), GetArg(projection ?? [], 1), privKey, nip44: true)),
                    "com.nokakoi.nokandro.DECRYPT_ZAP_EVENT" => SingleColumn("result", NostrZapDecrypt.Decrypt(GetArg(projection ?? [], 0), privKey, pubkeyHex)),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        public override string? GetType(Android.Net.Uri? uri) => null;
        public override Android.Net.Uri? Insert(Android.Net.Uri? uri, ContentValues? values) => null;
        public override int Delete(Android.Net.Uri? uri, string? selection, string[]? selectionArgs) => 0;
        public override int Update(Android.Net.Uri? uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;

        private static ICursor RejectedCursor()
        {
            var cursor = new MatrixCursor(["rejected"]);
            cursor.AddRow([""]);
            return cursor;
        }

        private static ICursor SingleColumn(string column, string value)
        {
            var cursor = new MatrixCursor([column]);
            cursor.AddRow([value]);
            return cursor;
        }

        private static ICursor SignEventCursor(string[] args, byte[] privKey)
        {
            var eventJson = GetArg(args, 0);
            if (string.IsNullOrEmpty(eventJson)) return RejectedCursor();
            var signed = NostrSignerOperations.SignEvent(eventJson, privKey);
            var sig = NostrSignerOperations.GetSignature(signed);
            var cursor = new MatrixCursor(["result", "event"]);
            cursor.AddRow([sig, signed]);
            return cursor;
        }

        private static string GetArg(string[] args, int index) => index >= 0 && index < args.Length ? (args[index] ?? "") : "";

        private static string AuthorityToMethod(string authority) => authority switch
        {
            "com.nokakoi.nokandro.GET_PUBLIC_KEY" => "get_public_key",
            "com.nokakoi.nokandro.SIGN_EVENT" => "sign_event",
            "com.nokakoi.nokandro.NIP04_ENCRYPT" => "nip04_encrypt",
            "com.nokakoi.nokandro.NIP44_ENCRYPT" => "nip44_encrypt",
            "com.nokakoi.nokandro.NIP04_DECRYPT" => "nip04_decrypt",
            "com.nokakoi.nokandro.NIP44_DECRYPT" => "nip44_decrypt",
            "com.nokakoi.nokandro.DECRYPT_ZAP_EVENT" => "decrypt_zap_event",
            _ => authority
        };

        private static int? ExtractEventKind(string method, string[]? projection)
        {
            if (method != "sign_event" || projection == null || projection.Length == 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(projection[0]);
                if (doc.RootElement.TryGetProperty("kind", out var k)) return k.GetInt32();
            }
            catch { }
            return null;
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
