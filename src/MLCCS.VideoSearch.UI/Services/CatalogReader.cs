using Microsoft.Data.Sqlite;
using MLCCS.VideoSearch.UI.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace MLCCS.VideoSearch.UI.Services;

internal static class CatalogReader
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".webm" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".heic" };
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ThumbnailRetryAfter =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<string?>> ThumbnailTasks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim ThumbnailConcurrency = new(4, 4);

    public static async Task<IReadOnlyList<MediaItemViewModel>> ReadAssetsAsync(IEnumerable<string> libraries)
    {
        var configuredLibraries = libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var indexed = await ReadIndexedAssetsAsync(configuredLibraries);
        var byPath = indexed.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var discovered = await Task.Run(() =>
        {
            foreach (var library in configuredLibraries)
            {
                if (!Directory.Exists(library)) continue;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(library, "*", SearchOption.AllDirectories))
                    {
                        var extension = Path.GetExtension(path);
                        if (!VideoExtensions.Contains(extension) && !ImageExtensions.Contains(extension)) continue;
                        if (byPath.ContainsKey(path)) continue;
                        try
                        {
                            var info = new FileInfo(path);
                            byPath[path] = new MediaItemViewModel
                            {
                                Path = path,
                                Name = info.Name,
                                Library = library,
                                Extension = extension,
                                SizeBytes = info.Length,
                                DurationMs = 0,
                                Status = VideoExtensions.Contains(extension) ? "等待索引" : "资源库图片",
                                ThumbnailPath = ImageExtensions.Contains(extension) ? path : null
                            };
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var item in byPath.Values.Where(item =>
                         item.ThumbnailPath is null && VideoExtensions.Contains(item.Extension)))
            {
                var cached = TryGetCachedThumbnail(item.Path);
                if (cached is not null) item.ThumbnailPath = cached;
            }
            return byPath.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        });
        return discovered;
    }

    private static async Task<List<MediaItemViewModel>> ReadIndexedAssetsAsync(
        IReadOnlyList<string> configuredLibraries)
    {
        var items = new List<MediaItemViewModel>();
        if (!File.Exists(AppPaths.Database)) return items;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.Database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT media_path,name,library_root,extension,size_bytes,duration_ms,status,
                       COALESCE(thumbnail_path,
                         (SELECT thumbnail_path FROM real_visual_frames frame
                          WHERE frame.media_path=media_assets.media_path
                          ORDER BY timestamp_ms LIMIT 1))
                FROM media_assets WHERE status!='Missing' ORDER BY name COLLATE NOCASE
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var libraryRoot = reader.GetString(2);
                if (!configuredLibraries.Contains(libraryRoot, StringComparer.OrdinalIgnoreCase))
                    continue;
                var thumbnailPath = reader.IsDBNull(7) ? null : reader.GetString(7);
                items.Add(new MediaItemViewModel
                {
                    Path = reader.GetString(0), Name = reader.GetString(1), Library = libraryRoot,
                    Extension = reader.GetString(3), SizeBytes = reader.GetInt64(4),
                    DurationMs = reader.GetInt64(5), Status = reader.GetString(6),
                    ThumbnailPath = File.Exists(thumbnailPath) ? thumbnailPath : null
                });
            }
        }
        catch (SqliteException) { return []; }
        return items;
    }

    internal static async Task EnsureThumbnailAsync(MediaItemViewModel item)
    {
        if (item.ThumbnailPath is not null || !VideoExtensions.Contains(item.Extension) ||
            !File.Exists(item.Path)) return;
        var now = DateTimeOffset.UtcNow;
        if (ThumbnailRetryAfter.TryGetValue(item.Path, out var retry) && retry > now) return;
        var cached = TryGetCachedThumbnail(item.Path);
        if (cached is not null)
        {
            item.ThumbnailPath = cached;
            return;
        }
        var task = ThumbnailTasks.GetOrAdd(item.Path, _ => GenerateThumbnailAsync(item.Path));
        try
        {
            var path = await task;
            if (path is not null) item.ThumbnailPath = path;
        }
        finally
        {
            ThumbnailTasks.TryRemove(item.Path, out _);
        }
    }

    private static string? TryGetCachedThumbnail(string path)
    {
        try
        {
            var target = GetThumbnailCachePath(path);
            return File.Exists(target) ? target : null;
        }
        catch { return null; }
    }

    private static string GetThumbnailCachePath(string path)
    {
        var info = new FileInfo(path);
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))).ToLowerInvariant();
        return Path.Combine(AppPaths.Root, "library-thumbnails", $"{key}.jpg");
    }

    private static async Task<string?> GenerateThumbnailAsync(string path)
    {
        await ThumbnailConcurrency.WaitAsync();
        try
        {
            var target = GetThumbnailCachePath(path);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 320,
                    ThumbnailOptions.UseCurrentScale);
                if (thumbnail is null || thumbnail.Size == 0) return null;
                await using var input = thumbnail.AsStreamForRead();
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                await input.CopyToAsync(output);
            }
            ThumbnailRetryAfter.TryRemove(path, out _);
            return target;
        }
        catch
        {
            ThumbnailRetryAfter[path] = DateTimeOffset.UtcNow.AddMinutes(10);
            return null;
        }
        finally { ThumbnailConcurrency.Release(); }
    }
}
