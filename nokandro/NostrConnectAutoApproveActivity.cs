using Android.App;
using Android.Content;
using Android.OS;

namespace nokandro
{
    [Activity(Label = "NostrConnectAutoApproveActivity", Exported = true, NoHistory = true)]
    [IntentFilter(new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "nostrconnect")]
    public class NostrConnectAutoApproveActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            var data = Intent?.DataString;
            if (!string.IsNullOrEmpty(data))
            {
                // MainActivityをSingleTopで起動し、nostrconnectURIをextraで渡す
                var mainIntent = new Intent(this, typeof(MainActivity));
                mainIntent.SetAction(Intent.ActionView);
                mainIntent.PutExtra("nostrconnect_uri", data);
                mainIntent.AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);
                StartActivity(mainIntent);
            }
            Finish();
        }
    }
}
