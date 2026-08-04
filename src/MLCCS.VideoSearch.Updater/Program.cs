using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text.Json;
using MLCCS.VideoSearch.Core.Contracts;
using MLCCS.VideoSearch.Core.Privacy;
using MLCCS.VideoSearch.Core.Updates;

namespace MLCCS.VideoSearch.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args is not ["apply", var manifestPath, var downloadDirectory, var installRoot])
        {
            Console.Error.WriteLine("Usage: Updater apply <signed-manifest> <download-directory> <install-root>");
            return 2;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(manifestPath), JsonDefaults.Options)
                           ?? throw new InvalidDataException("UPDATE_MANIFEST_INVALID");
            using var key = EmbeddedUpdateKey.Create();
            if (!UpdateVerifier.VerifyManifest(manifest, key))
                throw new System.Security.Cryptography.CryptographicException("UPDATE_SIGNATURE_INVALID");
            await PortableUpdater.ApplyVerifiedReleaseAsync(downloadDirectory, installRoot, manifest, CancellationToken.None);
            return 0;
        }
        catch (Exception exception)
        {
            var safe = DiagnosticRedactor.Redact(exception.ToString(), Environment.UserName);
            PortableUpdater.WriteStatus("failed", 1,
                DiagnosticRedactor.Redact(exception.Message, Environment.UserName));
            Console.Error.WriteLine(safe);
            return 1;
        }
    }
}

internal static class PortableUpdater
{
    public static async Task ApplyVerifiedReleaseAsync(string downloadDirectory, string root,
        ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        WriteStatus("verifying", 0.96, null);
        var rootFull = Path.GetFullPath(root);
        var recordPath = Path.Combine(rootFull, "_installer", "installed.json");
        var installed = ReadInstalledHashes(recordPath);
        var installedIds = installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = manifest.Components.Where(item => item.InstallScope == "current" || item.Required || installedIds.Contains(item.Id))
            .Where(item => !installed.TryGetValue(item.Id, out var hash) ||
                           !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (changed.Length == 0) return;
        var staging = Path.Combine(rootFull, $".staging-{manifest.ProductVersion}");
        var current = Path.Combine(rootFull, "current");
        var previous = Path.Combine(rootFull, "previous");
        var drive = new DriveInfo(Path.GetPathRoot(rootFull)!);
        if (drive.AvailableFreeSpace < changed.Sum(item => item.Size) * 2 + 512L * 1024 * 1024)
            throw new IOException("UPDATE_INSUFFICIENT_SPACE");

        var archives = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in changed)
        {
            var archive = Path.Combine(downloadDirectory, $"{component.Id}-{component.Sha256}.zip");
            if (!File.Exists(archive)) throw new FileNotFoundException("Verified component archive missing.", archive);
            await using var input = File.OpenRead(archive);
            await UpdateVerifier.VerifyArchiveAsync(input, component, cancellationToken);
            archives[component.Id] = archive;
        }

        var appComponent = changed.SingleOrDefault(item => item.InstallScope == "current");
        if (appComponent is not null)
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            SafeExtract(archives[appComponent.Id], staging);
            await VerifyFilesAsync(staging, appComponent.Files, cancellationToken);
        }
        foreach (var component in changed.Where(item => item.InstallScope != "current"))
        {
            var destination = ComponentDestination(rootFull, component);
            if (Directory.Exists(destination))
            {
                await VerifyFilesAsync(destination, component.Files, cancellationToken);
                continue;
            }
            var componentStaging = destination + ".staging";
            if (Directory.Exists(componentStaging)) Directory.Delete(componentStaging, true);
            Directory.CreateDirectory(componentStaging);
            SafeExtract(archives[component.Id], componentStaging);
            await VerifyFilesAsync(componentStaging, component.Files, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(componentStaging, destination);
        }

        await RequestShutdownAsync(cancellationToken);
        WriteStatus("applying", 0.98, null);
        if (appComponent is not null)
        {
            if (Directory.Exists(previous)) Directory.Delete(previous, true);
            if (Directory.Exists(current)) Directory.Move(current, previous);
            Directory.Move(staging, current);
        }

        if (!await HealthCheckAsync(current, cancellationToken))
        {
            await RequestShutdownAsync(cancellationToken);
            if (appComponent is not null)
            {
                var failed = Path.Combine(rootFull, $"failed-{manifest.ProductVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                Directory.Move(current, failed);
                if (Directory.Exists(previous)) Directory.Move(previous, current);
            }
            WriteStatus("rolled-back", 1, "UPDATE_ROLLBACK_COMPLETED");
            throw new InvalidOperationException("UPDATE_ROLLBACK_COMPLETED");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
        foreach (var component in changed) installed[component.Id] = component.Sha256;
        var record = new { version = manifest.ProductVersion, installedUtc = DateTimeOffset.UtcNow,
            components = installed.Select(item => new { id = item.Key, sha256 = item.Value }),
            previousAvailable = Directory.Exists(previous) };
        await File.WriteAllTextAsync(recordPath, JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        WriteStatus("completed", 1, null);
        Process.Start(new ProcessStartInfo(Path.Combine(current, "ui", "MLCCS.VideoSearch.UI.exe")) { UseShellExecute = true });
    }

    internal static void WriteStatus(string stage, double progress, string? error)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MLCCS", "VideoSearch");
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "update-status.json");
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            { stage, progress, error, updatedUtc = DateTimeOffset.UtcNow }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            File.Move(temporary, target, true);
        }
        catch { }
    }

    private static string ComponentDestination(string root, ReleaseComponent component)
    {
        var scope = component.InstallScope switch
        {
            "runtime" => "runtime", "qdrant" => "qdrant", "visual-model" => "visual-model",
            "text-model" => "text-model", "ocr-models" => "ocr-models",
            _ => throw new InvalidDataException("UPDATE_COMPONENT_SCOPE_INVALID")
        };
        return Path.Combine(root, "components", scope, component.Sha256);
    }

    private static Dictionary<string, string> ReadInstalledHashes(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("components", StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in property.Value.EnumerateArray())
            {
                string? id = null, hash = null;
                foreach (var field in item.EnumerateObject())
                {
                    if (field.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) id = field.Value.GetString();
                    if (field.Name.Equals("sha256", StringComparison.OrdinalIgnoreCase)) hash = field.Value.GetString();
                }
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(hash)) result[id] = hash;
            }
        }
        return result;
    }

    internal static void SafeExtract(string archive, string destination)
    {
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var output = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UPDATE_PATH_TRAVERSAL");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, true);
        }
    }

    private static async Task VerifyFilesAsync(string root, IReadOnlyList<ReleaseFile> files,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidDataException("UPDATE_FILE_MISSING");
            await using var input = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(input, cancellationToken));
            if (new FileInfo(path).Length != file.Size || !hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("UPDATE_FILE_HASH_MISMATCH");
        }
    }

    private static async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "MLCCS.VideoSearch.Agent.v1",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3000, cancellationToken);
            var request = IpcEnvelope.Create("agent.shutdown");
            await PipeFraming.WriteAsync(pipe, request, cancellationToken);
            await PipeFraming.ReadAsync(pipe, cancellationToken);
        }
        catch (IOException) { }
        catch (TimeoutException) { }
        foreach (var name in new[] { "MLCCS.VideoSearch.UI", "MLCCS.VideoSearch.Agent" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            process.CloseMainWindow();
            try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            catch (TimeoutException) { throw new IOException($"UPDATE_FILE_IN_USE: {name} did not exit safely."); }
            finally { process.Dispose(); }
        }
    }

    private static async Task<bool> HealthCheckAsync(string current, CancellationToken cancellationToken)
    {
        var ui = Path.Combine(current, "ui", "MLCCS.VideoSearch.UI.exe");
        var agent = Path.Combine(current, "agent", "MLCCS.VideoSearch.Agent.exe");
        if (!File.Exists(ui) || !File.Exists(agent)) return false;
        using var process = Process.Start(new ProcessStartInfo(ui, "--health-check") { UseShellExecute = false, CreateNoWindow = true });
        if (process is null) return false;
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (process.ExitCode != 0) return false;
            var agentProcess = Process.Start(new ProcessStartInfo(agent) { UseShellExecute = true });
            agentProcess?.Dispose();
            await using var pipe = new NamedPipeClientStream(".", "MLCCS.VideoSearch.Agent.v1",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(20_000, cancellationToken);
            await PipeFraming.WriteAsync(pipe, IpcEnvelope.Create("release.health"), cancellationToken);
            var response = await PipeFraming.ReadAsync(pipe, cancellationToken);
            if (response.Error is not null || response.Payload is not { } payload) return false;
            return payload.TryGetProperty("agent", out var agentState) && agentState.GetString() == "healthy" &&
                   payload.TryGetProperty("workerPid", out var workerPid) && workerPid.GetInt32() > 0 &&
                   payload.TryGetProperty("qdrant", out var qdrant) &&
                   qdrant.TryGetProperty("running", out var running) && running.GetBoolean();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { try { process.Kill(true); } catch { } return false; }
    }
}
