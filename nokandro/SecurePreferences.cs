using Android.Content;
using AndroidX.Security.Crypto;
using Java.Security;

namespace nokandro
{
    public static class SecurePreferences
    {
        private const string SECURE_PREFS_NAME = "nokandro_secure_prefs";
        // Legacy bug: Create(masterKeyAlias, fileName, ...) swapped args, so data lived under this file name.
        private const string LegacySwappedPrefsName = "_androidx_security_master_key_";
        private const string PLAIN_PREFS_NAME = "nokandro_prefs";
        private const string PREF_NSEC = "pref_nsec";
        private const string TAG = "SecurePreferences";

        private static readonly object Gate = new();
        private static ISharedPreferences? _cachedSecurePrefs;

        public static ISharedPreferences GetEncryptedSharedPreferences(Context context)
        {
            lock (Gate)
            {
                if (_cachedSecurePrefs != null) return _cachedSecurePrefs;
                _cachedSecurePrefs = CreateSecurePrefs(context, SECURE_PREFS_NAME);
                return _cachedSecurePrefs;
            }
        }

        private static ISharedPreferences CreateSecurePrefs(Context context, string fileName)
        {
            var masterKeyAlias = MasterKeys.GetOrCreate(MasterKeys.Aes256GcmSpec);
            // Android API order: (fileName, masterKeyAlias, context, ...)
            return EncryptedSharedPreferences.Create(
                fileName,
                masterKeyAlias,
                context,
                EncryptedSharedPreferences.PrefKeyEncryptionScheme.Aes256Siv,
                EncryptedSharedPreferences.PrefValueEncryptionScheme.Aes256Gcm
            );
        }

        public static void MigrateIfNeeded(Context context)
        {
            lock (Gate)
            {
                try
                {
                    MigrateFromLegacySwappedFile(context);
                    MigrateFromPlainPrefs(context);
                }
                catch (Exception ex)
                {
                    AppLog.E(TAG, "Migration failed: " + ex.Message);
                }
            }
        }

        private static void MigrateFromPlainPrefs(Context context)
        {
            var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
            if (plainPrefs == null) return;

            var nsec = plainPrefs.GetString(PREF_NSEC, null);
            if (string.IsNullOrEmpty(nsec)) return;

            AppLog.D(TAG, "Migrating nsec to secure storage");
            if (!WriteNsecCommitted(context, nsec))
            {
                AppLog.E(TAG, "Secure write failed during plaintext migration — keeping plaintext copy");
                return;
            }

            plainPrefs.Edit()?.Remove(PREF_NSEC)?.Commit();
        }

        /// <summary>
        /// Older builds passed Create(masterKeyAlias, fileName) so the prefs file was named like the master key alias.
        /// Copy any nsec found there into the correct file, then delete the legacy file.
        /// </summary>
        private static void MigrateFromLegacySwappedFile(Context context)
        {
            try
            {
                var legacyFile = new Java.IO.File(context.FilesDir?.Parent, "shared_prefs/" + LegacySwappedPrefsName + ".xml");
                if (legacyFile == null || !legacyFile.Exists()) return;

                ISharedPreferences legacy;
                try
                {
                    // Legacy store used fileName=masterKeyAlias and masterKeyAlias=SECURE_PREFS_NAME
                    legacy = EncryptedSharedPreferences.Create(
                        LegacySwappedPrefsName,
                        SECURE_PREFS_NAME,
                        context,
                        EncryptedSharedPreferences.PrefKeyEncryptionScheme.Aes256Siv,
                        EncryptedSharedPreferences.PrefValueEncryptionScheme.Aes256Gcm
                    );
                }
                catch (Exception ex)
                {
                    AppLog.W(TAG, "Could not open legacy secure prefs: " + ex.Message);
                    return;
                }

                var nsec = legacy.GetString(PREF_NSEC, null);
                if (!string.IsNullOrEmpty(nsec))
                {
                    AppLog.D(TAG, "Migrating nsec from legacy swapped secure prefs file");
                    if (!WriteNsecCommitted(context, nsec))
                    {
                        AppLog.E(TAG, "Failed to migrate nsec from legacy secure prefs");
                        return;
                    }
                }

                try { legacy.Edit()?.Clear()?.Commit(); } catch { }
                try { context.DeleteSharedPreferences(LegacySwappedPrefsName); } catch { }
                try
                {
                    var ks = KeyStore.GetInstance("AndroidKeyStore");
                    ks?.Load(null);
                    if (ks?.ContainsAlias(SECURE_PREFS_NAME) == true)
                        ks.DeleteEntry(SECURE_PREFS_NAME);
                }
                catch { }
            }
            catch (Exception ex)
            {
                AppLog.W(TAG, "Legacy secure prefs migration error: " + ex.Message);
            }
        }

        public static string GetNsec(Context context)
        {
            lock (Gate)
            {
                try
                {
                    MigrateFromLegacySwappedFile(context);
                    MigrateFromPlainPrefs(context);

                    var securePrefs = GetEncryptedSharedPreferences(context);
                    var value = securePrefs.GetString(PREF_NSEC, string.Empty) ?? string.Empty;
                    if (!string.IsNullOrEmpty(value)) return value;

                    // Fallback: plaintext may still exist if a previous secure write failed mid-migration.
                    var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                    return plainPrefs?.GetString(PREF_NSEC, string.Empty) ?? string.Empty;
                }
                catch (Exception ex)
                {
                    AppLog.E(TAG, "Failed to get nsec: " + ex.Message);
                    try
                    {
                        var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                        return plainPrefs?.GetString(PREF_NSEC, string.Empty) ?? string.Empty;
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }
            }
        }

        public static void SaveNsec(Context context, string nsec)
        {
            lock (Gate)
            {
                try
                {
                    var trimmed = nsec?.Trim() ?? string.Empty;

                    // Never overwrite a stored key with empty/partial input (e.g. OnPause after a failed load).
                    if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("nsec1", StringComparison.Ordinal))
                    {
                        AppLog.W(TAG, "Ignoring SaveNsec with empty/invalid value");
                        return;
                    }

                    if (!WriteNsecCommitted(context, trimmed))
                    {
                        AppLog.E(TAG, "Secure SaveNsec commit failed — writing plaintext fallback");
                        var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                        plainPrefs?.Edit()?.PutString(PREF_NSEC, trimmed)?.Commit();
                        return;
                    }

                    var plain = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                    if (plain?.Contains(PREF_NSEC) == true)
                        plain.Edit()?.Remove(PREF_NSEC)?.Commit();
                }
                catch (Exception ex)
                {
                    AppLog.E(TAG, "Failed to save nsec: " + ex.Message);
                    try
                    {
                        var trimmed = nsec?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(trimmed) && trimmed.StartsWith("nsec1", StringComparison.Ordinal))
                        {
                            var plainPrefs = context.GetSharedPreferences(PLAIN_PREFS_NAME, FileCreationMode.Private);
                            plainPrefs?.Edit()?.PutString(PREF_NSEC, trimmed)?.Commit();
                        }
                    }
                    catch { }
                }
            }
        }

        private static bool WriteNsecCommitted(Context context, string nsec)
        {
            try
            {
                // Drop cached instance so we always write through a healthy EncryptedSharedPreferences.
                _cachedSecurePrefs = null;
                var securePrefs = GetEncryptedSharedPreferences(context);
                var ok = securePrefs.Edit()?.PutString(PREF_NSEC, nsec)?.Commit() == true;
                if (!ok) AppLog.E(TAG, "EncryptedSharedPreferences Commit returned false");
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.E(TAG, "WriteNsecCommitted failed: " + ex.Message);
                _cachedSecurePrefs = null;
                return false;
            }
        }
    }
}
