using Android.Content;
using AndroidX.Security.Crypto;

namespace nokandro
{
    public static class SecurePreferences
    {
        private const string SECURE_PREFS_NAME = "nokandro_secure_prefs";
        private const string PLAIN_PREFS_NAME = "nokandro_prefs";
        private const string PREF_NSEC = "pref_nsec";
        private const string TAG = "SecurePreferences";

        public static ISharedPreferences GetEncryptedSharedPreferences(Context context)
        {
            var masterKeyAlias = MasterKeys.GetOrCreate(MasterKeys.Aes256GcmSpec);
            return EncryptedSharedPreferences.Create(
                masterKeyAlias,
                SECURE_PREFS_NAME,
                context,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.Aes256Siv,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.Aes256Gcm
            );
        }

        public static void MigrateIfNeeded(Context context)
        {
            try
            {
                var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                if (plainPrefs == null) return;

                var nsec = plainPrefs.GetString(PREF_NSEC, null);
                if (!string.IsNullOrEmpty(nsec))
                {
                    AppLog.D(TAG, "Migrating nsec to secure storage");
                    var securePrefs = GetEncryptedSharedPreferences(context);
                    securePrefs.Edit()?.PutString(PREF_NSEC, nsec)?.Apply();

                    // Remove plaintext nsec for security
                    plainPrefs.Edit()?.Remove(PREF_NSEC)?.Apply();
                }
            }
            catch (System.Exception ex)
            {
                AppLog.E(TAG, "Migration failed: " + ex.Message);
            }
        }

        public static string GetNsec(Context context)
        {
            try
            {
                MigrateIfNeeded(context);
                var securePrefs = GetEncryptedSharedPreferences(context);
                return securePrefs.GetString(PREF_NSEC, string.Empty) ?? string.Empty;
            }
            catch (System.Exception ex)
            {
                AppLog.E(TAG, "Failed to get nsec: " + ex.Message);
                return string.Empty;
            }
        }

        public static void SaveNsec(Context context, string nsec)
        {
            try
            {
                var securePrefs = GetEncryptedSharedPreferences(context);
                securePrefs.Edit()?.PutString(PREF_NSEC, nsec)?.Apply();

                // Clean up plaintext if exists
                var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                if (plainPrefs?.Contains(PREF_NSEC) == true)
                {
                    plainPrefs.Edit()?.Remove(PREF_NSEC)?.Apply();
                }
            }
            catch (System.Exception ex)
            {
                AppLog.E(TAG, "Failed to save nsec: " + ex.Message);
            }
        }
    }
}
