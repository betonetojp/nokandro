using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Content;

namespace nokandro
{
    public class LiveNoteItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("pubkey")]
        public string Pubkey { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("petname")]
        public string? Petname { get; set; }

        [JsonPropertyName("createdAt")]
        public long CreatedAt { get; set; }

        [JsonPropertyName("isReacted")]
        public bool IsReacted { get; set; }
    }

    public static class LiveNoteStore
    {
        private const string PREFS_NAME = "nokandro_live_note_prefs";
        private const string KEY_POSTS_JSON = "posts_json";
        private const int MAX_POSTS = 20;
        private const int MAX_KEEP_POSTS = 50;

        private static readonly object Lock = new();
        private static readonly List<LiveNoteItem> Posts = new();
        private static int _currentIndex = 0;
        private static bool _initialized = false;

        public static int CurrentIndex
        {
            get { lock (Lock) { return _currentIndex; } }
            set
            {
                lock (Lock)
                {
                    if (Posts.Count == 0) _currentIndex = 0;
                    else
                    {
                        _currentIndex = Math.Clamp(value, 0, Posts.Count - 1);
                        if (_currentIndex == 0 && Posts.Count > MAX_POSTS)
                        {
                            Posts.RemoveRange(MAX_POSTS, Posts.Count - MAX_POSTS);
                        }
                    }
                }
            }
        }

        public static int Count
        {
            get { lock (Lock) { return Posts.Count; } }
        }

        public static void EnsureInitialized(Context context)
        {
            lock (Lock)
            {
                if (_initialized) return;
                _initialized = true;
                LoadFromPrefs(context);
            }
        }

        public static void AddPost(Context context, string id, string pubkey, string content, string? petname, long createdAt)
        {
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(content)) return;

            lock (Lock)
            {
                EnsureInitialized(context);

                // 最新以外を表示している場合は、表示中の投稿IDを記憶して表示位置をキープする
                string? currentViewingId = (_currentIndex > 0 && _currentIndex < Posts.Count)
                    ? Posts[_currentIndex].Id
                    : null;

                // Avoid duplicate if same event id
                if (!string.IsNullOrEmpty(id))
                {
                    Posts.RemoveAll(p => p.Id == id);
                }

                var item = new LiveNoteItem
                {
                    Id = id ?? string.Empty,
                    Pubkey = pubkey ?? string.Empty,
                    Content = content ?? string.Empty,
                    Petname = string.IsNullOrWhiteSpace(petname) ? null : petname,
                    CreatedAt = createdAt > 0 ? createdAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    IsReacted = false
                };

                Posts.Insert(0, item);

                if (currentViewingId != null)
                {
                    // 過去の投稿を表示中だった場合：表示中投稿の新しいインデックスに追従
                    var newIndex = Posts.FindIndex(p => p.Id == currentViewingId);
                    if (newIndex >= 0)
                    {
                        _currentIndex = newIndex;
                    }
                    else
                    {
                        _currentIndex = Math.Clamp(_currentIndex + 1, 0, Posts.Count - 1);
                    }

                    // 閲覧中の投稿が押し出されないよう MAX_KEEP_POSTS (50件) まで保持
                    if (Posts.Count > MAX_KEEP_POSTS)
                    {
                        Posts.RemoveRange(MAX_KEEP_POSTS, Posts.Count - MAX_KEEP_POSTS);
                        if (_currentIndex >= Posts.Count)
                        {
                            _currentIndex = Posts.Count - 1;
                        }
                    }
                }
                else
                {
                    // 最新を表示していた場合：そのまま最新（0）を表示し、通常の上限 MAX_POSTS (20件) にトリミング
                    _currentIndex = 0;
                    if (Posts.Count > MAX_POSTS)
                    {
                        Posts.RemoveRange(MAX_POSTS, Posts.Count - MAX_POSTS);
                    }
                }

                SaveToPrefs(context);
            }
        }

        public static LiveNoteItem? GetCurrentPost(Context context)
        {
            lock (Lock)
            {
                EnsureInitialized(context);
                if (Posts.Count == 0) return null;
                if (_currentIndex >= Posts.Count) _currentIndex = 0;
                return Posts[_currentIndex];
            }
        }

        public static void StepPrev(Context context)
        {
            lock (Lock)
            {
                EnsureInitialized(context);
                if (Posts.Count > 0)
                {
                    _currentIndex = Math.Min(Posts.Count - 1, _currentIndex + 1);
                }
            }
        }

        public static void StepNext(Context context)
        {
            lock (Lock)
            {
                EnsureInitialized(context);
                if (Posts.Count > 0)
                {
                    _currentIndex = Math.Max(0, _currentIndex - 1);
                    // 最新に戻ったときは、閲覧キープで超過した過去ログを MAX_POSTS (20件) にトリミング
                    if (_currentIndex == 0 && Posts.Count > MAX_POSTS)
                    {
                        Posts.RemoveRange(MAX_POSTS, Posts.Count - MAX_POSTS);
                        SaveToPrefs(context);
                    }
                }
            }
        }

        public static bool MarkReacted(Context context, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (Lock)
            {
                EnsureInitialized(context);
                var found = Posts.FirstOrDefault(p => p.Id == id);
                if (found != null)
                {
                    found.IsReacted = true;
                    SaveToPrefs(context);
                    return true;
                }
                return false;
            }
        }

        public static bool IsReacted(Context context, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (Lock)
            {
                EnsureInitialized(context);
                var found = Posts.FirstOrDefault(p => p.Id == id);
                return found != null && found.IsReacted;
            }
        }

        private static void LoadFromPrefs(Context context)
        {
            try
            {
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var json = prefs?.GetString(KEY_POSTS_JSON, null);
                if (!string.IsNullOrEmpty(json))
                {
                    var list = JsonSerializer.Deserialize<List<LiveNoteItem>>(json);
                    if (list != null)
                    {
                        Posts.Clear();
                        Posts.AddRange(list);
                    }
                }
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LiveNoteStore", $"Failed to load posts from prefs: {ex.Message}");
            }
        }

        private static void SaveToPrefs(Context context)
        {
            try
            {
                var prefs = context.GetSharedPreferences(PREFS_NAME, FileCreationMode.Private);
                var json = JsonSerializer.Serialize(Posts);
                prefs?.Edit()?.PutString(KEY_POSTS_JSON, json)?.Apply();
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("LiveNoteStore", $"Failed to save posts to prefs: {ex.Message}");
            }
        }
    }
}
