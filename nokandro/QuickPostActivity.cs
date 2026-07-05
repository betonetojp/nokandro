using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.AppCompat.App;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace nokandro
{
    [Activity(Label = "Quick Post", Theme = "@style/AppTheme.QuickPostDialog", ExcludeFromRecents = true, NoHistory = true, LaunchMode = Android.Content.PM.LaunchMode.SingleTask, Exported = false)]
    public class QuickPostActivity : AppCompatActivity
    {
        private EditText? _input;
        private Button? _sendBtn;
        private Button? _cancelBtn;
        private ProgressBar? _progress;
        private string? _relay;
        private byte[]? _privKey;


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
            _relay = prefs?.GetString("pref_relay", "wss://relay-jp.nostr.wirednet.jp/");
            var nsec = SecurePreferences.GetNsec(this);

            if (string.IsNullOrEmpty(nsec))
            {
                Toast.MakeText(this, "Please set nsec in main app first", ToastLength.Long).Show();
                Finish();
                return;
            }

            try
            {
                var (hrp, data) = NostrKeyDecoder.Bech32Decode(nsec);
                if (hrp == "nsec" && data != null)
                {
                    _privKey = NostrKeyDecoder.ConvertBits(data, 5, 8, false);
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
                    var tagsJson = "[[\"client\",\"nokandro\",\"31990:21ac29561b5de90cdc21995fc0707525cd78c8a52d87721ab681d3d609d1e2df:1763998625847\",\"wss://yabu.me/\"]]";
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


    }
}
