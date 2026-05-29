using Android.Content;
using Android.OS;

namespace nokandro
{
    [BroadcastReceiver(Enabled = true, Exported = true, DirectBootAware = true)]
    [IntentFilter([Intent.ActionBootCompleted, Intent.ActionLockedBootCompleted, Intent.ActionMyPackageReplaced])]
    public sealed class BootReceiver : BroadcastReceiver
    {
        private const string PREFS_NAME = "nokandro_prefs";
        private const string PREF_BUNKER_AUTOSTART_BOOT = "pref_bunker_autostart_boot";
        private const string PREF_BUNKER_ENABLED = "pref_bunker_enabled";
        private const string PREF_BUNKER_RELAY = "pref_bunker_relay";
        private const string PREF_BUNKER_SECRET = "pref_bunker_secret";
        private const string PREF_NSEC = "pref_nsec";

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context == null || intent == null) return;

            if (intent.Action != Intent.ActionBootCompleted &&
                intent.Action != Intent.ActionLockedBootCompleted &&
                intent.Action != Intent.ActionMyPackageReplaced)
                return;

            try
            {
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                if (prefs == null) return;

                var autoStart = prefs.GetBoolean(PREF_BUNKER_AUTOSTART_BOOT, false);
                var wantBunker = prefs.GetBoolean(PREF_BUNKER_ENABLED, false);
                if (!autoStart || !wantBunker) return;

                var nsec = prefs.GetString(PREF_NSEC, string.Empty) ?? string.Empty;
                if (!TryDecodeNsecToHex(nsec, out var nsecHex) || string.IsNullOrEmpty(nsecHex)) return;

                var relay = prefs.GetString(PREF_BUNKER_RELAY, "wss://ephemeral.snowflare.cc/") ?? "wss://ephemeral.snowflare.cc/";
                var secret = prefs.GetString(PREF_BUNKER_SECRET, null);

                var serviceIntent = new Intent(context, typeof(BunkerService));
                serviceIntent.PutExtra("nsecHex", nsecHex);
                serviceIntent.PutExtra("relay", relay);
                if (!string.IsNullOrEmpty(secret)) serviceIntent.PutExtra("secret", secret);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(serviceIntent);
                else
                    context.StartService(serviceIntent);
            }
            catch { }
        }

        private static bool TryDecodeNsecToHex(string nsec, out string? nsecHex)
        {
            nsecHex = null;
            try
            {
                if (string.IsNullOrWhiteSpace(nsec) || !nsec.StartsWith("nsec1", StringComparison.OrdinalIgnoreCase)) return false;
                var (hrp, data) = Bech32Decode(nsec.Trim());
                if (hrp != "nsec" || data == null) return false;
                var priv = ConvertBits(data, 5, 8, false);
                if (priv == null || priv.Length != 32) return false;
                nsecHex = BitConverter.ToString(priv).Replace("-", string.Empty).ToLowerInvariant();
                return true;
            }
            catch
            {
                return false;
            }
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
            var hrpBytes = System.Text.Encoding.ASCII.GetBytes(hrp);
            var expand = new List<byte>(hrpBytes.Length * 2 + 1);
            foreach (var b in hrpBytes) expand.Add((byte)(b >> 5));
            expand.Add(0);
            foreach (var b in hrpBytes) expand.Add((byte)(b & 31));
            return [.. expand];
        }
    }
}
