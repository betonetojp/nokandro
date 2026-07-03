using System;
using Android.Content;

namespace nokandro
{
    public class BunkerSessionController
    {
        private readonly Context _context;
        private readonly SettingsRepository _settings;
        private bool _bunkerStarted;

        public bool IsBunkerStarted => _bunkerStarted;

        public BunkerSessionController(Context context, SettingsRepository settings)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void StartBunker(string nsecHex)
        {
            if (_bunkerStarted) return;

            var relay = _settings.BunkerRelay;
            var secret = _settings.BunkerSecret;

            var serviceIntent = new Intent(_context, typeof(BunkerService));
            serviceIntent.PutExtra("nsecHex", nsecHex);
            serviceIntent.PutExtra("relay", relay);
            if (!string.IsNullOrEmpty(secret))
            {
                serviceIntent.PutExtra("secret", secret);
            }

            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
            {
                _context.StartForegroundService(serviceIntent);
            }
            else
            {
                _context.StartService(serviceIntent);
            }

            _bunkerStarted = true;
            _settings.BunkerEnabled = true;
        }

        public void StopBunker()
        {
            var serviceIntent = new Intent(_context, typeof(BunkerService));
            _context.StopService(serviceIntent);

            _bunkerStarted = false;
            _settings.BunkerEnabled = false;
        }

        public void UpdateStartedState(bool started)
        {
            _bunkerStarted = started;
        }
    }
}
