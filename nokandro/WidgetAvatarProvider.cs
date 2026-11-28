using System;
using System.IO;
using System.Text.RegularExpressions;
using Android.Content;
using Android.Database;
using Android.OS;
using IOPath = System.IO.Path;

namespace nokandro
{
    /// <summary>
    /// Strictly read-only ContentProvider serving pre-rendered circular avatar PNGs
    /// for home screen widgets via setImageViewUri, avoiding large Binder bitmap parcels
    /// and Android 15/16 RemoteCollectionItems bitmap memory limits.
    /// </summary>
    [ContentProvider(
        new[] { Authority },
        Name = "com.nokakoi.nokandro.WidgetAvatarProvider",
        Exported = true)]
    public sealed class WidgetAvatarProvider : ContentProvider
    {
        public const string Authority = "com.nokakoi.nokandro.widget_avatar";
        private static readonly Regex SafeFileNameRegex = new(@"^[a-z0-9_]+\.png$", RegexOptions.Compiled);

        public override bool OnCreate() => true;

        public override string? GetType(Android.Net.Uri uri) => "image/png";

        public override ParcelFileDescriptor? OpenFile(Android.Net.Uri uri, string mode)
        {
            if (mode != "r")
                throw new Java.Lang.SecurityException("Read-only provider");

            var fileName = uri.LastPathSegment;
            if (string.IsNullOrEmpty(fileName) || !SafeFileNameRegex.IsMatch(fileName))
                throw new Java.IO.FileNotFoundException("Invalid avatar file name");

            var ctx = Context;
            if (ctx?.CacheDir == null)
                throw new Java.IO.FileNotFoundException("Cache directory unavailable");

            var smallDir = IOPath.Combine(ctx.CacheDir.AbsolutePath, "profile_avatars", "widget_small");
            var fullPath = IOPath.GetFullPath(IOPath.Combine(smallDir, fileName));
            var expectedPrefix = IOPath.GetFullPath(smallDir) + IOPath.DirectorySeparatorChar;
            if (!fullPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new Java.Lang.SecurityException("Path traversal blocked");

            var javaFile = new Java.IO.File(fullPath);
            if (!javaFile.Exists())
                throw new Java.IO.FileNotFoundException("Avatar file not found");

            return ParcelFileDescriptor.Open(javaFile, ParcelFileMode.ReadOnly);
        }

        public override ICursor? Query(Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;

        public override Android.Net.Uri? Insert(Android.Net.Uri uri, ContentValues? values) =>
            throw new Java.Lang.UnsupportedOperationException("Read-only provider");

        public override int Delete(Android.Net.Uri uri, string? selection, string[]? selectionArgs) =>
            throw new Java.Lang.UnsupportedOperationException("Read-only provider");

        public override int Update(Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) =>
            throw new Java.Lang.UnsupportedOperationException("Read-only provider");
    }
}
