using System;
using Android.Content;

namespace nokandro
{
    public class SettingsRepository
    {
        private const string PREFS_NAME = "nokandro_prefs";

        // Keys
        public const string PREF_NSEC = "pref_nsec";
        public const string PREF_BUNKER_ENABLED = "pref_bunker_enabled";
        public const string PREF_BUNKER_RELAY = "pref_bunker_relay";
        public const string PREF_BUNKER_SECRET = "pref_bunker_secret";
        public const string PREF_BUNKER_AUTOSTART_BOOT = "pref_bunker_autostart_boot";
        public const string PREF_BUNKER_BATTERY_PROMPT_SHOWN = "pref_bunker_battery_prompt_shown";
        public const string PREF_SKIP_CW = "pref_skip_cw";
        public const string PREF_TRUNCATE_LEN = "pref_truncate_len";
        public const string PREF_SPEECH_RATE = "pref_speech_rate";
        public const string PREF_SPEAK_PETNAME = "pref_speak_petname";
        public const string PREF_ALLOW_OTHERS = "pref_allow_others";
        public const string PREF_AUTO_STOP = "pref_auto_stop";
        public const string PREF_ENABLE_TTS = "pref_enable_tts";
        public const string PREF_ENABLE_MUSIC_STATUS = "pref_enable_music_status";
        public const string PREF_FONT_SCALE = "pref_font_scale";
        public const string PREF_OFF_TIMER_MINUTES = "pref_off_timer_minutes";
        public const string PREF_OFF_TIMER_ENABLED = "pref_off_timer_enabled";
        public const string PREF_MUTED_WORDS = "pref_muted_words";
        public const string PREF_NC_NAMES = "pref_nc_names";
        public const string PREF_VOICE_FOLLOWED = "pref_voice_followed";
        public const string PREF_VOICE_OTHER = "pref_voice_other";

        private readonly ISharedPreferences _prefs;

        public SettingsRepository(Context context)
        {
            _prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private) 
                ?? throw new InvalidOperationException("SharedPreferences not available");
        }

        public string GetNsec(Context context) => SecurePreferences.GetNsec(context);
        public void SaveNsec(Context context, string nsec) => SecurePreferences.SaveNsec(context, nsec);

        public bool BunkerEnabled
        {
            get => _prefs.GetBoolean(PREF_BUNKER_ENABLED, false);
            set => _prefs.Edit()?.PutBoolean(PREF_BUNKER_ENABLED, value).Apply();
        }

        public string BunkerRelay
        {
            get => _prefs.GetString(PREF_BUNKER_RELAY, "wss://ephemeral.snowflare.cc/") ?? "wss://ephemeral.snowflare.cc/";
            set => _prefs.Edit()?.PutString(PREF_BUNKER_RELAY, value).Apply();
        }

        public string? BunkerSecret
        {
            get => _prefs.GetString(PREF_BUNKER_SECRET, null);
            set => _prefs.Edit()?.PutString(PREF_BUNKER_SECRET, value).Apply();
        }

        public bool BunkerAutostartBoot
        {
            get => _prefs.GetBoolean(PREF_BUNKER_AUTOSTART_BOOT, false);
            set => _prefs.Edit()?.PutBoolean(PREF_BUNKER_AUTOSTART_BOOT, value).Apply();
        }

        public bool BunkerBatteryPromptShown
        {
            get => _prefs.GetBoolean(PREF_BUNKER_BATTERY_PROMPT_SHOWN, false);
            set => _prefs.Edit()?.PutBoolean(PREF_BUNKER_BATTERY_PROMPT_SHOWN, value).Apply();
        }

        public bool SkipContentWarning
        {
            get => _prefs.GetBoolean(PREF_SKIP_CW, false);
            set => _prefs.Edit()?.PutBoolean(PREF_SKIP_CW, value).Apply();
        }

        public int TruncateLength
        {
            get => _prefs.GetInt(PREF_TRUNCATE_LEN, 20);
            set => _prefs.Edit()?.PutInt(PREF_TRUNCATE_LEN, value).Apply();
        }

        public float SpeechRate
        {
            get => _prefs.GetFloat(PREF_SPEECH_RATE, 1.0f);
            set => _prefs.Edit()?.PutFloat(PREF_SPEECH_RATE, value).Apply();
        }

        public bool SpeakPetname
        {
            get => _prefs.GetBoolean(PREF_SPEAK_PETNAME, false);
            set => _prefs.Edit()?.PutBoolean(PREF_SPEAK_PETNAME, value).Apply();
        }

        public bool AllowOthers
        {
            get => _prefs.GetBoolean(PREF_ALLOW_OTHERS, false);
            set => _prefs.Edit()?.PutBoolean(PREF_ALLOW_OTHERS, value).Apply();
        }

        public bool AutoStop
        {
            get => _prefs.GetBoolean(PREF_AUTO_STOP, true);
            set => _prefs.Edit()?.PutBoolean(PREF_AUTO_STOP, value).Apply();
        }

        public bool EnableTts
        {
            get => _prefs.GetBoolean(PREF_ENABLE_TTS, true);
            set => _prefs.Edit()?.PutBoolean(PREF_ENABLE_TTS, value).Apply();
        }

        public bool EnableMusicStatus
        {
            get => _prefs.GetBoolean(PREF_ENABLE_MUSIC_STATUS, false);
            set => _prefs.Edit()?.PutBoolean(PREF_ENABLE_MUSIC_STATUS, value).Apply();
        }

        public float FontScale
        {
            get => _prefs.GetFloat(PREF_FONT_SCALE, 1.0f);
            set => _prefs.Edit()?.PutFloat(PREF_FONT_SCALE, value).Apply();
        }

        public int OffTimerMinutes
        {
            get => _prefs.GetInt(PREF_OFF_TIMER_MINUTES, 60);
            set => _prefs.Edit()?.PutInt(PREF_OFF_TIMER_MINUTES, value).Apply();
        }

        public bool OffTimerEnabled
        {
            get => _prefs.GetBoolean(PREF_OFF_TIMER_ENABLED, false);
            set => _prefs.Edit()?.PutBoolean(PREF_OFF_TIMER_ENABLED, value).Apply();
        }

        public string MutedWords
        {
            get => _prefs.GetString(PREF_MUTED_WORDS, "") ?? "";
            set => _prefs.Edit()?.PutString(PREF_MUTED_WORDS, value).Apply();
        }

        public string NcNames
        {
            get => _prefs.GetString(PREF_NC_NAMES, "") ?? "";
            set => _prefs.Edit()?.PutString(PREF_NC_NAMES, value).Apply();
        }

        public string? VoiceFollowed
        {
            get => _prefs.GetString(PREF_VOICE_FOLLOWED, null);
            set => _prefs.Edit()?.PutString(PREF_VOICE_FOLLOWED, value).Apply();
        }

        public string? VoiceOther
        {
            get => _prefs.GetString(PREF_VOICE_OTHER, null);
            set => _prefs.Edit()?.PutString(PREF_VOICE_OTHER, value).Apply();
        }
    }
}
