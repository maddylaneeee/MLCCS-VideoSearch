using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace MLCCS.VideoSearch.Agent;

internal sealed class QdrantProcessManager : IDisposable
{
    private readonly string _root;
    private readonly string _releaseRoot;
    private readonly string _ownerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _port;
    private string? _apiKey;

    public QdrantProcessManager(string root, string releaseRoot)
    {
        _root = Path.Combine(root, "qdrant");
        _releaseRoot = releaseRoot;
        _ownerPath = Path.Combine(_root, "managed-process.json");
    }

    public bool Running => _process is { HasExited: false };
    public int? Pid => Running ? _process!.Id : null;
    public Uri? Endpoint => Running ? new Uri($"http://127.0.0.1:{_port}/") : null;
    public string ApiKey => _apiKey ?? throw new InvalidOperationException("Qdrant has not started.");

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Running && await IsHealthyAsync(cancellationToken)) return;
            StopRecordedOrphan();
            StopCore();
            Exception? firstFailure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try { await StartOnceAsync(cancellationToken); return; }
                catch (Exception error) when (attempt == 0 && error is not OperationCanceledException)
                {
                    firstFailure = error;
                    StopCore();
                    QuarantineStorage();
                }
            }
            throw new InvalidOperationException("Private Qdrant failed after a clean deterministic rebuild.", firstFailure);
        }
        catch
        {
            StopCore();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartOnceAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "storage"));
        _apiKey = LoadOrCreateApiKey();
        _port = ReserveLoopbackPort();
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = _root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["QDRANT__SERVICE__HOST"] = "127.0.0.1";
        start.Environment["QDRANT__SERVICE__HTTP_PORT"] = _port.ToString();
        start.Environment["QDRANT__SERVICE__GRPC_PORT"] = "0";
        start.Environment["QDRANT__SERVICE__API_KEY"] = _apiKey;
        start.Environment["QDRANT__STORAGE__STORAGE_PATH"] = Path.Combine(_root, "storage");
        start.Environment["QDRANT__LOG_LEVEL"] = "WARN";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start private Qdrant.");
        File.WriteAllText(_ownerPath, JsonSerializer.Serialize(new { pid = _process.Id, executable }));
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
                throw new InvalidOperationException($"Private Qdrant exited with code {_process.ExitCode}.");
            if (await IsHealthyAsync(cancellationToken)) return;
            await Task.Delay(200, cancellationToken);
        }
        throw new TimeoutException("Private Qdrant did not become healthy within 20 seconds.");
    }

    private void QuarantineStorage()
    {
        var storage = Path.Combine(_root, "storage");
        if (!Directory.Exists(storage)) return;
        var quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        Directory.Move(storage, Path.Combine(quarantine, $"storage-{DateTime.UtcNow:yyyyMMddHHmmssfff}"));
    }

    public void Stop()
    {
        _gate.Wait();
        try { StopCore(); }
        finally { _gate.Release(); }
    }

    public object Status() => new
    {
        running = Running,
        pid = Pid,
        endpoint = Running ? $"127.0.0.1:{_port}" : null,
        healthy = Running
    };

    private string ResolveExecutable()
    {
        var componentRoot = ComponentLayout.ResolveActiveRoot(_releaseRoot, "qdrant", "qdrant-server");
        if (!Directory.Exists(componentRoot))
            throw new FileNotFoundException("The required Qdrant v1.18.3 component is not installed.");
        var matches = Directory.EnumerateFiles(componentRoot, "qdrant.exe", SearchOption.AllDirectories).ToArray();
        if (matches.Length != 1)
            throw new FileNotFoundException("Exactly one immutable Qdrant v1.18.3 component is required.");
        return matches[0];
    }

    private string LoadOrCreateApiKey()
    {
        var path = Path.Combine(_root, "instance-secret.json");
        if (File.Exists(path))
        {
            using var existing = JsonDocument.Parse(File.ReadAllText(path));
            return existing.RootElement.GetProperty("apiKey").GetString()
                   ?? throw new InvalidDataException("Invalid Qdrant instance secret.");
        }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        File.WriteAllText(path, JsonSerializer.Serialize(new { apiKey = key }));
        if (OperatingSystem.IsWindows())
        {
            var identity = WindowsIdentity.GetCurrent().User
                           ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(identity);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        return key;
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (!Running) return false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_port}/healthz");
            request.Headers.Add("api-key", _apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.StatusCode is HttpStatusCode.OK;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void StopCore()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); _process.WaitForExit(5000); }
            catch (InvalidOperationException) { }
        }
        _process?.Dispose();
        _process = null;
        _port = 0;
        TryDeleteOwnerRecord();
    }

    private void StopRecordedOrphan()
    {
        if (!File.Exists(_ownerPath)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_ownerPath));
            var pid = document.RootElement.GetProperty("pid").GetInt32();
            var executable = document.RootElement.GetProperty("executable").GetString();
            var expected = ResolveExecutable();
            if (!string.Equals(Path.GetFullPath(executable ?? ""), Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase))
                return;
            using var process = Process.GetProcessById(pid);
            var actual = process.MainModule?.FileName;
            if (!string.Equals(Path.GetFullPath(actual ?? ""), Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase))
                return;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { TryDeleteOwnerRecord(); }
    }

    private void TryDeleteOwnerRecord()
    {
        try { File.Delete(_ownerPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}

internal static class ComponentLayout
{
    internal static string ResolveActiveRoot(string releaseRoot, string scope, string componentId)
    {
        var installRoot = Path.GetFullPath(Path.Combine(releaseRoot, ".."));
        var scopeRoot = Path.Combine(installRoot, "components", scope);
        var record = Path.Combine(installRoot, "_installer", "installed.json");
        if (File.Exists(record))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(record));
            if (TryProperty(document.RootElement, "components", out var components))
            {
                foreach (var item in components.EnumerateArray())
                {
                    if (TryProperty(item, "id", out var id) &&
                        string.Equals(id.GetString(), componentId, StringComparison.OrdinalIgnoreCase) &&
                        TryProperty(item, "sha256", out var hash) && hash.GetString() is { Length: > 0 } value)
                        return Path.Combine(scopeRoot, value);
                }
            }
        }
        var candidates = Directory.Exists(scopeRoot) ? Directory.GetDirectories(scopeRoot) : [];
        return candidates.Length == 1 ? candidates[0] : Path.Combine(scopeRoot, "missing");
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        value = default;
        return false;
    }
}
