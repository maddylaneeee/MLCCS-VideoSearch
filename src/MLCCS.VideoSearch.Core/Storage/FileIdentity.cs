using System.IO.Hashing;
using System.Security.Cryptography;

namespace MLCCS.VideoSearch.Core.Storage;

public sealed record FileIdentity(string CanonicalPath, long SizeBytes, DateTimeOffset ModifiedUtc, string FastFingerprint, string? FullSha256 = null);

public static class FileIdentityReader
{
    private const int BlockSize = 64 * 1024;

    public static async Task<FileIdentity> ReadAsync(string path, bool fullHash = false, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        var canonical = Path.GetFullPath(info.FullName).TrimEnd(Path.DirectorySeparatorChar);
        await using var stream = new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var xx = new XxHash128();
        foreach (var offset in Offsets(info.Length))
        {
            stream.Position = offset;
            var buffer = new byte[Math.Min(BlockSize, checked((int)Math.Max(0, info.Length - offset)))];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            xx.Append(buffer);
        }
        xx.Append(BitConverter.GetBytes(info.Length));
        var fast = Convert.ToHexStringLower(xx.GetCurrentHash());
        string? sha = null;
        if (fullHash)
        {
            stream.Position = 0;
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        return new FileIdentity(canonical, info.Length, info.LastWriteTimeUtc, fast, sha);
    }

    private static IEnumerable<long> Offsets(long length)
    {
        if (length <= BlockSize) { yield return 0; yield break; }
        yield return 0;
        yield return Math.Max(0, (length / 2) - (BlockSize / 2));
        yield return Math.Max(0, length - BlockSize);
    }
}

public sealed record ScanFailure(string Path, string Code, string Summary);
public sealed record ScanResult(IReadOnlyList<FileIdentity> Files, IReadOnlyList<ScanFailure> Failures);

public static class LibraryScanner
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".mp3", ".m4a", ".wav", ".flac", ".aac" };

    public static async Task<ScanResult> ScanAsync(string root, IReadOnlyCollection<string> excludedRoots, CancellationToken cancellationToken)
    {
        var files = new List<FileIdentity>();
        var failures = new List<ScanFailure>();
        var queue = new Queue<DirectoryInfo>(); queue.Enqueue(new DirectoryInfo(Path.GetFullPath(root)));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exclusions = excludedRoots.Select(x => Path.GetFullPath(x).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = queue.Dequeue();
            if (!visited.Add(directory.FullName) || exclusions.Any(x => directory.FullName.Equals(x, StringComparison.OrdinalIgnoreCase) || directory.FullName.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                foreach (var child in directory.EnumerateDirectories())
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    queue.Enqueue(child);
                }
                foreach (var file in directory.EnumerateFiles().Where(f => Extensions.Contains(f.Extension)))
                {
                    try { files.Add(await FileIdentityReader.ReadAsync(file.FullName, cancellationToken: cancellationToken)); }
                    catch (UnauthorizedAccessException e) { failures.Add(new(file.FullName, "INDEX_PERMISSION_DENIED", e.Message)); }
                    catch (IOException e) { failures.Add(new(file.FullName, "INDEX_MEDIA_UNAVAILABLE", e.Message)); }
                }
            }
            catch (UnauthorizedAccessException e) { failures.Add(new(directory.FullName, "INDEX_PERMISSION_DENIED", e.Message)); }
            catch (IOException e) { failures.Add(new(directory.FullName, "INDEX_MEDIA_UNAVAILABLE", e.Message)); }
        }
        return new(files, failures);
    }
}

