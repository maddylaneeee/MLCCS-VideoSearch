using System.Diagnostics;
using System.IO.Compression;

namespace MLCCS.VideoSearch.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is not ["apply", var archive, var installRoot, var expectedVersion])
        {
            Console.Error.WriteLine("Usage: MLCCS.VideoSearch.Updater apply <verified-archive> <install-root> <expected-version>");
            return 2;
        }

        try
        {
            await PortableUpdater.ApplyVerifiedArchiveAsync(archive, installRoot, expectedVersion, CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

internal static class PortableUpdater
{
    public static async Task ApplyVerifiedArchiveAsync(string archive, string root, string version, CancellationToken cancellationToken)
    {
        var rootFull = Path.GetFullPath(root);
        var staging = Path.Combine(rootFull, $".staging-{version}");
        var current = Path.Combine(rootFull, "current");
        var previous = Path.Combine(rootFull, "previous");
        if (!File.Exists(archive)) throw new FileNotFoundException("Verified archive missing.", archive);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        SafeExtract(archive, staging);

        await RequestShutdownAsync(cancellationToken);
        if (Directory.Exists(previous)) Directory.Delete(previous, true);
        if (Directory.Exists(current)) Directory.Move(current, previous);
        Directory.Move(staging, current);

        if (!await HealthCheckAsync(current, cancellationToken))
        {
            var failed = Path.Combine(rootFull, $"failed-{version}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.Move(current, failed);
            if (Directory.Exists(previous)) Directory.Move(previous, current);
            throw new InvalidOperationException("UPDATE_ROLLBACK_COMPLETED");
        }
    }

    private static void SafeExtract(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Archive path traversal rejected.");
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, true);
        }
    }

    private static async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var name in new[] { "MLCCS.VideoSearch.UI", "MLCCS.VideoSearch.Agent", "MLCCS.VideoSearch.Worker" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                process.CloseMainWindow();
                try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
                catch (TimeoutException) { throw new IOException($"UPDATE_FILE_IN_USE: {name} did not exit safely."); }
            }
        }
    }

    private static async Task<bool> HealthCheckAsync(string current, CancellationToken cancellationToken)
    {
        var ui = Path.Combine(current, "ui", "MLCCS.VideoSearch.UI.exe");
        if (!File.Exists(ui)) return false;
        using var process = Process.Start(new ProcessStartInfo(ui, "--health-check") { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) return false;
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken); return process.ExitCode == 0; }
        catch (TimeoutException) { try { process.Kill(true); } catch { } return false; }
    }
}
