using Android.Content;
using Android.OS;
using Android.Views;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace nokandro
{
    [Activity(Label = "Quick Post", Theme = "@android:style/Theme.DeviceDefault.Dialog", ExcludeFromRecents = true, NoHistory = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTask, Exported = false)]
    public class QuickPostActivity : Activity
    {
        private EditText? _input;
        private Button? _sendBtn;
        private Button? _cancelBtn;
        private ProgressBar? _progress;
        private string? _relay;
        private string? _nsec;
        private byte[]? _privKey;
        private static readonly string Bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_quick_post);

            _input = FindViewById<EditText>(Resource.Id.quickPostInput);
            _sendBtn = FindViewById<Button>(Resource.Id.quickPostSend);
            _cancelBtn = FindViewById<Button>(Resource.Id.quickPostCancel);
            _progress = FindViewById<ProgressBar>(Resource.Id.quickPostProgress);

            // Read prefs
            var prefs = GetSharedPreferences("nokandro_prefs", FileCreationMode.Private);
            _relay = prefs?.GetString("pref_relay", "wss://yabu.me");
            var nsec = prefs?.GetString("pref_nsec", null);

            if (string.IsNullOrEmpty(nsec))
            {
                Toast.MakeText(this, "Please set nsec in main app first", ToastLength.Long).Show();
                Finish();
                return;
            }

            try
            {
                var (hrp, data) = Bech32Decode(nsec);
                if (hrp == "nsec" && data != null)
                {
                    _privKey = ConvertBits(data, 5, 8, false);
                }
            }
            catch { }

            if (_privKey == null || _privKey.Length != 32)
            {
                Toast.MakeText(this, "Invalid nsec configuration", ToastLength.Long).Show();
                Finish();
                return;
            }

            if (_cancelBtn != null) _cancelBtn.Click += (s, e) => Finish();
            if (_sendBtn != null) _sendBtn.Click += async (s, e) => await SendPostAsync();

            // Focus input/show keyboard
            if (_input != null)
            {
                _input.RequestFocus();
                Window?.SetSoftInputMode(SoftInput.StateVisible);
            }
        }

        private async Task SendPostAsync()
        {
            var text = _input?.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            if (_sendBtn != null) _sendBtn.Enabled = false;
            if (_input != null) _input.Enabled = false;
            if (_progress != null) _progress.Visibility = ViewStates.Visible;

            try
            {
                await Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(_relay ?? "wss://yabu.me"), cts.Token);

                    // Sign
                    var pubkeyBytes = NostrCrypto.GetPublicKey(_privKey!);
                    var pubkeyHex = BitConverter.ToString(pubkeyBytes).Replace("-", "").ToLowerInvariant();
                    var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    
                    var contentJson = EscapeJsonString(text);
                    var tagsJson = "[[\"client\",\"nokandro\",\"31990:21ac29561b5de90cdc21995fc0707525cd78c8a52d87721ab681d3d609d1e2df:1763998625847\",\"wss://relay.nostr.band\"]]";
                    var rawEvent = $"[0,\"{pubkeyHex}\",{createdAt},1,{tagsJson},{contentJson}]";
                    
                    using var sha = SHA256.Create();
                    var eventId = sha.ComputeHash(Encoding.UTF8.GetBytes(rawEvent));
                    var sig = NostrCrypto.Sign(eventId, _privKey);
                    var idHex = BitConverter.ToString(eventId).Replace("-", "").ToLowerInvariant();
                    var sigHex = BitConverter.ToString(sig).Replace("-", "").ToLowerInvariant();

                    var signedJson = $"{{\"kind\":1,\"created_at\":{createdAt},\"tags\":{tagsJson},\"content\":{contentJson},\"pubkey\":\"{pubkeyHex}\",\"id\":\"{idHex}\",\"sig\":\"{sigHex}\"}}";
                    var msg = $"[\"EVENT\",{signedJson}]";

                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, cts.Token);

                    // Wait for OK (simplified, just wait a bit or for response)
                    var buffer = new byte[4096];
                    // We just give it a second to send, as we don't strictly parsing OK here for simplicity in widget activity
                    // but reading the response is good practice to ensure socket stays open long enough
                    try {
                        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    } catch {}

                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", cts.Token);
                });

                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, "Posted!", ToastLength.Short).Show();
                    Finish();
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    Toast.MakeText(this, "Failed: " + ex.Message, ToastLength.Long).Show();
                    if (_sendBtn != null) _sendBtn.Enabled = true;
                    if (_input != null) _input.Enabled = true;
                    if (_progress != null) _progress.Visibility = ViewStates.Gone;
                });
            }
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

        // Bech32 Utils
        private static (string? hrp, byte[]? data) Bech32Decode(string bech)
        {
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
            var hrpExpanded = HrpExpand(hrp);
            var values = new List<byte>();
            values.AddRange(hrpExpanded);
            values.AddRange(data);
            if (Polymod([.. values]) != 1) return (null, null);
            var payload = new byte[data.Length - 6];
            Array.Copy(data, 0, payload, 0, payload.Length);
            return (hrp, payload);
        }

        private static byte[]? ConvertBits(byte[] data, int fromBits, int toBits, bool pad)
        {
            int acc = 0;
            int bits = 0;
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
            else if (bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0) return null;
            return [.. result];
        }

        private static int Polymod(byte[] values)
        {
            var chk = 1;
            var generators = new int[] { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
            foreach (var v in values)
            {
                var top = chk >> 25;
                chk = ((chk & 0x1ffffff) << 5) ^ v;
                for (int i = 0; i < 5; i++) if (((top >> i) & 1) != 0) chk ^= generators[i];
            }
            return chk;
        }

        private static byte[] HrpExpand(string hrp)
        {
            var hrpBytes = Encoding.ASCII.GetBytes(hrp);
            var expand = new List<byte>();
            foreach (var b in hrpBytes) expand.Add((byte)(b >> 5));
            expand.Add(0);
            foreach (var b in hrpBytes) expand.Add((byte)(b & 31));
            return [.. expand];
        }
    }
}