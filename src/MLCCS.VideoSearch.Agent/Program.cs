using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MLCCS.VideoSearch.Core.Contracts;
using MLCCS.VideoSearch.Core.Storage;

namespace MLCCS.VideoSearch.Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(true, "Local\\MLCCS.VideoSearch.Agent.v1", out var createdNew);
        if (!createdNew) return;
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MLCCS", "VideoSearch");
        Directory.CreateDirectory(root);
        Application.Run(new AgentHost(root));
    }
}

internal sealed class AgentConfiguration
{
    public List<string> Libraries { get; set; } = [];
    public bool AutoStart { get; set; } = true;
    public bool AutoIndexNewFiles { get; set; } = true;
    public double IntervalSeconds { get; set; } = 4.0;
    public int BatchSize { get; set; }
    public int DecoderWorkers { get; set; }
    public string ResourcePolicy { get; set; } = "adaptive-full";
    public bool Filename { get; set; } = true;
    public bool Visual { get; set; } = true;
    public bool Speech { get; set; }
    public bool Ocr { get; set; }
    public string SpeechModel { get; set; } = "whisper-medium";
    public bool HelpImprove { get; set; } = true;
    public bool AutomaticUpdates { get; set; } = true;
    public string PhoneticExpansion { get; set; } = "medium";
}

internal sealed class AgentHost : ApplicationContext
{
    private const string PipeName = "MLCCS.VideoSearch.Agent.v1";
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".webm" };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly NotifyIcon _tray;
    private readonly string _root;
    private readonly string _releaseRoot;
    private readonly string _configPath;
    private readonly object _indexerGate = new();
    private readonly object _logGate = new();
    private readonly object _watcherGate = new();
    private readonly Queue<string> _pendingLibraries = new();
    private readonly Dictionary<string, FileSystemWatcher> _libraryWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _scheduledLibraryChanges = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _activeIndexStartedUtc;
    private string? _activeLibrary;
    private AgentConfiguration _configuration = new();
    private string _configurationFingerprint = "";
    private Process? _indexer;
    private Process? _modelDownloader;
    private SearchWorkerClient? _searchWorker;
    private DateTime _lastConfigCheckUtc = DateTime.MinValue;

    public AgentHost(string root)
    {
        _root = root;
        _releaseRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        _configPath = Path.Combine(_root, "libraries.json");
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 MLCCS Video Search", null, (_, _) => OpenUi());
        menu.Items.Add("暂停索引", null, (_, _) => Pause());
        menu.Items.Add("继续索引", null, (_, _) => Resume());
        menu.Items.Add("重新扫描全部资源库", null, (_, _) => Rescan());
        menu.Items.Add("安全退出", null, async (_, _) => await ExitSafelyAsync());
        _tray = new NotifyIcon
        {
            Text = "MLCCS Video Search — Agent 正在运行",
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => OpenUi();
        ReloadConfiguration(force: true);
        _ = RunAsync(_shutdown.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await new CatalogDatabase(Path.Combine(_root, "catalog.db")).InitializeAsync(cancellationToken);
            await Task.WhenAll(AcceptPipeAsync(cancellationToken), QueueLoopAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(_root, $"agent-crash-{DateTime.UtcNow:yyyyMMddHHmmss}.log"), exception.ToString());
        }
    }

    private async Task AcceptPipeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = CreatePipe(PipeName);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
            _ = HandlePipeConnectionAsync(pipe, cancellationToken);
        }
    }

    private async Task HandlePipeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                var request = await PipeFraming.ReadAsync(pipe, cancellationToken);
                var response = await HandleRequestAsync(request, cancellationToken);
                await PipeFraming.WriteAsync(pipe, response, cancellationToken);
            }
            catch (EndOfStreamException) { }
            catch (IOException) when (!pipe.IsConnected) { }
        }
    }

    private async Task<IpcEnvelope> HandleRequestAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            object payload = request.Kind switch
            {
                "agent.status" => StatusPayload(),
                "library.list" => new { libraries = _configuration.Libraries },
                "library.add" => AddLibrary(ReadString(request, "path")),
                "library.remove" => RemoveLibrary(ReadString(request, "path")),
                "library.rescan" => Rescan(),
                "index.pause" => Pause(),
                "index.resume" => Resume(),
                "index.cancel" => Cancel(),
                "settings.get" => _configuration,
                "settings.update" => UpdateSettings(request),
                "storage.summary" => await StorageSummaryAsync(cancellationToken),
                "storage.cleanup" => CleanupStorage(request),
                "hardware.detect" => await DetectHardwareAsync(cancellationToken),
                "models.ensure" => EnsureModels(request),
                "models.cancel" => CancelModelDownload(),
                "search.query" => await SearchAsync(request, cancellationToken),
                _ => throw new ProtocolException("IPC_UNKNOWN_REQUEST", $"Unknown Agent request {request.Kind}.")
            };
            return request with
            {
                Kind = $"{request.Kind}.response",
                Stage = "completed",
                Progress = 1,
                Error = null,
                Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
            };
        }
        catch (Exception error)
        {
            return request with
            {
                Kind = "error",
                Stage = "failed",
                Progress = 0,
                Error = new StableError(error is ProtocolException protocol ? protocol.Code : "AGENT_REQUEST_FAILED",
                    error.Message, "请查看索引任务页或 Agent 日志。"),
                Payload = null
            };
        }
    }

    private static string ReadString(IpcEnvelope request, string property)
    {
        if (request.Payload is not { } payload || !payload.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ProtocolException("IPC_INVALID_ENVELOPE", $"Missing payload property {property}.");
        return value.GetString()!;
    }

    private object StatusPayload()
    {
        var statusPath = Path.Combine(_root, "real-index-status.json");
        JsonElement? workerStatus = null;
        JsonElement? modelDownloadStatus = null;
        if (File.Exists(statusPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(statusPath));
                workerStatus = document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        var modelStatusPath = Path.Combine(_root, "model-download-status.json");
        if (File.Exists(modelStatusPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(modelStatusPath));
                modelDownloadStatus = document.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        int pendingLibraries;
        int scheduledLibraryChanges;
        lock (_indexerGate) pendingLibraries = _pendingLibraries.Count;
        lock (_watcherGate) scheduledLibraryChanges = _scheduledLibraryChanges.Count;
        return new
        {
            agent = "running",
            configured = _configuration.Libraries.Count > 0,
            libraries = _configuration.Libraries,
            indexerRunning = _indexer is { HasExited: false },
            modelDownloaderRunning = _modelDownloader is { HasExited: false },
            pendingLibraries,
            scheduledLibraryChanges,
            activeIndexStartedUtc = _activeIndexStartedUtc,
            paused = File.Exists(Path.Combine(_root, "real-index.pause")),
            workerStatus,
            modelDownloadStatus
        };
    }

    private object AddLibrary(string path)
    {
        var canonical = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException($"资源库文件夹不可用：{canonical}");
        if (!_configuration.Libraries.Contains(canonical, StringComparer.OrdinalIgnoreCase))
        {
            _configuration.Libraries.Add(canonical);
            SaveConfiguration();
            EnqueueLibraries([canonical], cancelCurrent: false);
        }
        return new { added = canonical, libraries = _configuration.Libraries };
    }

    private object RemoveLibrary(string path)
    {
        var canonical = Path.GetFullPath(path.Trim());
        var removed = _configuration.Libraries.RemoveAll(
            item => string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
            return new { removed = false, path = canonical, libraries = _configuration.Libraries };
        SaveConfiguration();
        lock (_indexerGate)
        {
            var retained = _pendingLibraries.Where(
                item => !string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase)).ToArray();
            _pendingLibraries.Clear();
            foreach (var library in retained) _pendingLibraries.Enqueue(library);
            if (string.Equals(_activeLibrary, canonical, StringComparison.OrdinalIgnoreCase) &&
                _indexer is { HasExited: false })
            {
                File.WriteAllText(Path.Combine(_root, "real-index.cancel"),
                    DateTimeOffset.UtcNow.ToString("O"));
            }
        }
        RemoveLibraryIndex(canonical);
        return new { removed = true, path = canonical, libraries = _configuration.Libraries };
    }

    private void RemoveLibraryIndex(string library)
    {
        var database = Path.Combine(_root, "catalog.db");
        if (!File.Exists(database)) return;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 15
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[]
                 {
                     "real_visual_frames", "transcript_segments_live", "ocr_observations_live"
                 })
        {
            using var dependent = connection.CreateCommand();
            dependent.Transaction = transaction;
            dependent.CommandText =
                $"DELETE FROM {table} WHERE media_path IN " +
                "(SELECT media_path FROM media_assets WHERE library_root=$library)";
            dependent.Parameters.AddWithValue("$library", library);
            dependent.ExecuteNonQuery();
        }
        using (var assets = connection.CreateCommand())
        {
            assets.Transaction = transaction;
            assets.CommandText = "DELETE FROM media_assets WHERE library_root=$library";
            assets.Parameters.AddWithValue("$library", library);
            assets.ExecuteNonQuery();
        }
        using (var runs = connection.CreateCommand())
        {
            runs.Transaction = transaction;
            runs.CommandText = "DELETE FROM real_index_runs WHERE library_root=$library";
            runs.Parameters.AddWithValue("$library", library);
            runs.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private object UpdateSettings(IpcEnvelope request)
    {
        if (request.Payload is not { } payload)
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "Settings payload is missing.");
        var incoming = payload.Deserialize<AgentConfiguration>(JsonOptions)
            ?? throw new ProtocolException("IPC_INVALID_ENVELOPE", "Settings payload is invalid.");
        incoming.Libraries = incoming.Libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        incoming.IntervalSeconds = Math.Clamp(incoming.IntervalSeconds, 2, 8);
        incoming.BatchSize = Math.Max(0, incoming.BatchSize);
        incoming.DecoderWorkers = Math.Max(0, incoming.DecoderWorkers);
        _configuration = incoming;
        SaveConfiguration();
        return _configuration;
    }

    private async Task<object> StorageSummaryAsync(CancellationToken cancellationToken)
    {
        return await Task.Run<object>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workerRoot = Path.Combine(_releaseRoot, "worker");
            var downloadedModels = Path.Combine(_root, "models");
            var packagedModels = Path.Combine(workerRoot, "models");
            var privateRuntimeBytes = DirectoryBytes(Path.Combine(workerRoot, "python")) +
                                      DirectoryBytes(Path.Combine(workerRoot, "vendor"));
            var packagedModelBytes = DirectoryBytes(packagedModels);
            var downloadedModelBytes = DirectoryBytes(downloadedModels);
            var indexBytes = FileBytes(Path.Combine(_root, "catalog.db")) +
                             FileBytes(Path.Combine(_root, "catalog.db-wal")) +
                             FileBytes(Path.Combine(_root, "catalog.db-shm")) +
                             DirectoryBytes(Path.Combine(_root, "thumbnails"));
            var applicationBytes = DirectoryBytes(Path.Combine(_releaseRoot, "ui")) +
                                   DirectoryBytes(Path.Combine(_releaseRoot, "agent")) +
                                   DirectoryBytes(Path.Combine(_releaseRoot, "updater"));
            var stateBytes = DirectoryBytes(_root) - downloadedModelBytes -
                             DirectoryBytes(Path.Combine(_root, "thumbnails")) -
                             FileBytes(Path.Combine(_root, "catalog.db")) -
                             FileBytes(Path.Combine(_root, "catalog.db-wal")) -
                             FileBytes(Path.Combine(_root, "catalog.db-shm"));
            var candidates = new List<object>();
            if (Directory.Exists(downloadedModels))
            {
                foreach (var directory in Directory.EnumerateDirectories(downloadedModels))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(directory);
                    var duplicate = Directory.Exists(Path.Combine(packagedModels, name));
                    var required = (_configuration.Speech && name == _configuration.SpeechModel) ||
                                   (_configuration.Ocr && name is "ppocrv5-mobile-det" or "ppocrv5-mobile-rec") ||
                                   (_configuration.Visual && name == "openclip-standard" && !duplicate);
                    if (required) continue;
                    candidates.Add(new
                    {
                        id = $"model:{name}",
                        name = duplicate ? $"{name}（与应用内模型重复）" : $"{name}（当前未使用）",
                        bytes = DirectoryBytes(directory),
                        reason = duplicate ? "应用已包含同名模型；删除本地副本不会影响当前功能。" :
                            "当前设置未使用此模型；以后启用时可重新下载。"
                    });
                }
            }
            return new
            {
                categories = new[]
                {
                    new { id = "application", name = "应用程序", bytes = applicationBytes },
                    new { id = "runtime", name = "私有运行环境", bytes = privateRuntimeBytes },
                    new { id = "packaged-models", name = "应用内模型", bytes = packagedModelBytes },
                    new { id = "downloaded-models", name = "已下载模型", bytes = downloadedModelBytes },
                    new { id = "index", name = "索引与预览图", bytes = indexBytes },
                    new { id = "state", name = "设置、状态与日志", bytes = Math.Max(0, stateBytes) }
                },
                totalBytes = applicationBytes + privateRuntimeBytes + packagedModelBytes +
                             downloadedModelBytes + indexBytes + Math.Max(0, stateBytes),
                candidates
            };
        }, cancellationToken);
    }

    private object CleanupStorage(IpcEnvelope request)
    {
        if (request.Payload is not { } payload || !payload.TryGetProperty("ids", out var idsNode) ||
            idsNode.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "Storage cleanup selection is missing.");
        var deleted = new List<string>();
        long reclaimedBytes = 0;
        var localModelsRoot = Path.GetFullPath(Path.Combine(_root, "models")) + Path.DirectorySeparatorChar;
        foreach (var id in idsNode.EnumerateArray().Select(item => item.GetString()).Where(item => item is not null))
        {
            if (!id!.StartsWith("model:", StringComparison.Ordinal)) continue;
            var name = id["model:".Length..];
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
                continue;
            var target = Path.GetFullPath(Path.Combine(_root, "models", name));
            if (!target.StartsWith(localModelsRoot, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(target))
                continue;
            var packagedDuplicate = Directory.Exists(Path.Combine(_releaseRoot, "worker", "models", name));
            var active = (_configuration.Speech && name == _configuration.SpeechModel) ||
                         (_configuration.Ocr && name is "ppocrv5-mobile-det" or "ppocrv5-mobile-rec") ||
                         (_configuration.Visual && name == "openclip-standard" && !packagedDuplicate);
            if (active) continue;
            var bytes = DirectoryBytes(target);
            Directory.Delete(target, recursive: true);
            reclaimedBytes += bytes;
            deleted.Add(name);
        }
        return new { deleted, reclaimedBytes };
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    private static long FileBytes(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private async Task<object> SearchAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        if (request.Payload is not { } payload)
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "Search payload is missing.");
        var query = payload.TryGetProperty("query", out var queryElement) ? queryElement.GetString() ?? "" : "";
        var source = payload.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() ?? "all" : "all";
        var library = payload.TryGetProperty("library", out var libraryElement)
            ? libraryElement.GetString() ?? "" : "";
        if (library.Length > 0 &&
            !_configuration.Libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
            throw new ProtocolException("IPC_INVALID_LIBRARY", "所选资源文件夹已不在当前资源库中。");
        var limit = payload.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 60;
        if (string.IsNullOrWhiteSpace(query))
            return new { results = Array.Empty<object>(), elapsedMs = 0d };
        var modelsRoot = ResolveModelsRoot();
        _searchWorker ??= new SearchWorkerClient(
            Path.Combine(_releaseRoot, "worker", "python", "python.exe"),
            Path.Combine(_releaseRoot, "worker"),
            Path.Combine(_root, "catalog.db"),
            modelsRoot,
            Path.Combine(_root, "search-worker.log"));
        var started = Stopwatch.GetTimestamp();
        var response = await _searchWorker.SearchAsync(query, source, library, Math.Clamp(limit, 1, 100),
            _configuration.PhoneticExpansion, cancellationToken);
        return new { results = response, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
    }

    private async Task<JsonElement> DetectHardwareAsync(CancellationToken cancellationToken)
    {
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var python = Path.Combine(workerRoot, "python", "python.exe");
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = workerRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-m", "mlccs_worker.capabilities_cli", "--storage", _root })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动硬件检测。");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"硬件检测失败：{error.Trim()}");
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private object EnsureModels(IpcEnvelope request)
    {
        if (request.Payload is not { } payload || !payload.TryGetProperty("prefixes", out var prefixesElement) ||
            prefixesElement.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "Model prefix list is missing.");
        var prefixes = prefixesElement.EnumerateArray().Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().Distinct().ToArray();
        if (prefixes.Length == 0) return new { started = false, reason = "no-models-requested" };
        if (_modelDownloader is { HasExited: false })
            return new { started = false, reason = "already-running" };
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(workerRoot, "python", "python.exe"),
            WorkingDirectory = workerRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-m", "mlccs_worker.model_download",
            "--manifest", Path.Combine(workerRoot, "manifests", "models.lock.json"),
            "--destination", Path.Combine(_root, "models"),
            "--status", Path.Combine(_root, "model-download-status.json"),
            "--cancel", Path.Combine(_root, "model-download.cancel")
        }) start.ArgumentList.Add(argument);
        foreach (var prefix in prefixes)
        {
            start.ArgumentList.Add("--prefix");
            start.ArgumentList.Add(prefix);
        }
        _modelDownloader = Process.Start(start) ?? throw new InvalidOperationException("无法启动模型下载 Worker。");
        _modelDownloader.EnableRaisingEvents = true;
        _modelDownloader.OutputDataReceived += (_, args) => AppendLog(Path.Combine(_root, "model-download.log"), args.Data);
        _modelDownloader.ErrorDataReceived += (_, args) => AppendLog(Path.Combine(_root, "model-download.log"), args.Data);
        _modelDownloader.BeginOutputReadLine();
        _modelDownloader.BeginErrorReadLine();
        _modelDownloader.Exited += (_, _) =>
        {
            var succeeded = _modelDownloader?.ExitCode == 0;
            _modelDownloader?.Dispose();
            _modelDownloader = null;
            if (succeeded) EnqueueLibraries(_configuration.Libraries, cancelCurrent: false);
        };
        return new { started = true, prefixes };
    }

    private object CancelModelDownload()
    {
        File.WriteAllText(Path.Combine(_root, "model-download.cancel"), DateTimeOffset.UtcNow.ToString("O"));
        return new { cancelled = true };
    }

    private async Task QueueLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow - _lastConfigCheckUtc > TimeSpan.FromSeconds(1))
            {
                ReloadConfiguration(force: false);
                _lastConfigCheckUtc = DateTime.UtcNow;
            }
            FlushScheduledLibraryChanges();
            lock (_indexerGate)
            {
                if ((_indexer is null || _indexer.HasExited) && _pendingLibraries.Count > 0)
                    StartNextIndexerLocked();
            }
            await Task.Delay(500, cancellationToken);
        }
    }

    private void ReloadConfiguration(bool force)
    {
        AgentConfiguration loaded;
        if (!File.Exists(_configPath))
        {
            loaded = new AgentConfiguration();
        }
        else
        {
            try
            {
                loaded = JsonSerializer.Deserialize<AgentConfiguration>(File.ReadAllText(_configPath), JsonOptions) ?? new();
            }
            catch (JsonException error)
            {
                File.WriteAllText(Path.Combine(_root, "configuration-error.log"), error.ToString());
                return;
            }
        }
        var fingerprint = JsonSerializer.Serialize(loaded, JsonOptions);
        if (!force && fingerprint == _configurationFingerprint) return;
        _configuration = loaded;
        _configurationFingerprint = fingerprint;
        ConfigureLibraryWatchers();
        if (_configuration.AutoStart && _configuration.Libraries.Count > 0)
            EnqueueLibraries(_configuration.Libraries, cancelCurrent: false);
    }

    private void SaveConfiguration()
    {
        Directory.CreateDirectory(_root);
        var document = JsonSerializer.Serialize(_configuration, JsonOptions);
        var temporary = _configPath + ".tmp";
        File.WriteAllText(temporary, document);
        File.Move(temporary, _configPath, true);
        _configurationFingerprint = document;
        ConfigureLibraryWatchers();
    }

    private void ConfigureLibraryWatchers()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _libraryWatchers.Values)
                watcher.Dispose();
            _libraryWatchers.Clear();
            _scheduledLibraryChanges.Clear();
            if (!_configuration.AutoIndexNewFiles) return;

            foreach (var library in _configuration.Libraries.Where(Directory.Exists)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var watcher = new FileSystemWatcher(library)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.Size | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024,
                        EnableRaisingEvents = false
                    };
                    watcher.Created += (_, args) => ScheduleLibraryChange(library, args.FullPath);
                    watcher.Changed += (_, args) => ScheduleLibraryChange(library, args.FullPath);
                    watcher.Deleted += (_, args) => ScheduleLibraryChange(library, args.FullPath);
                    watcher.Renamed += (_, args) =>
                    {
                        ScheduleLibraryChange(library, args.OldFullPath);
                        ScheduleLibraryChange(library, args.FullPath);
                    };
                    // Buffer overflow means changes may have been coalesced; a full incremental scan is safest.
                    watcher.Error += (_, _) => ScheduleLibraryChange(library, null);
                    watcher.EnableRaisingEvents = true;
                    _libraryWatchers[library] = watcher;
                }
                catch (IOException error)
                {
                    AppendLog(Path.Combine(_root, "library-watcher.log"), $"{library}: {error.Message}");
                }
                catch (UnauthorizedAccessException error)
                {
                    AppendLog(Path.Combine(_root, "library-watcher.log"), $"{library}: {error.Message}");
                }
            }
        }
    }

    private void ScheduleLibraryChange(string library, string? changedPath)
    {
        if (changedPath is not null && !MediaExtensions.Contains(Path.GetExtension(changedPath)))
            return;
        lock (_watcherGate)
            _scheduledLibraryChanges[library] = DateTime.UtcNow.AddSeconds(4);
    }

    private void FlushScheduledLibraryChanges()
    {
        List<string> due;
        lock (_watcherGate)
        {
            var now = DateTime.UtcNow;
            due = _scheduledLibraryChanges.Where(item => item.Value <= now).Select(item => item.Key).ToList();
            foreach (var library in due)
                _scheduledLibraryChanges.Remove(library);
        }
        if (due.Count > 0)
            EnqueueLibraries(due, cancelCurrent: false);
    }

    private object Rescan()
    {
        EnqueueLibraries(_configuration.Libraries, cancelCurrent: true);
        return new { queued = _configuration.Libraries.Count };
    }

    private void EnqueueLibraries(IEnumerable<string> libraries, bool cancelCurrent)
    {
        lock (_indexerGate)
        {
            if (cancelCurrent && _indexer is { HasExited: false })
                File.WriteAllText(Path.Combine(_root, "real-index.cancel"), DateTimeOffset.UtcNow.ToString("O"));
            foreach (var library in libraries.Where(Directory.Exists))
            {
                if (!_pendingLibraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                    _pendingLibraries.Enqueue(library);
            }
            if (_indexer is null || _indexer.HasExited)
                StartNextIndexerLocked();
        }
    }

    private void StartNextIndexerLocked()
    {
        if (_pendingLibraries.Count == 0 || !_configuration.Visual) return;
        var library = _pendingLibraries.Dequeue();
        if (!_configuration.Libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
        {
            StartNextIndexerLocked();
            return;
        }
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var python = Path.Combine(workerRoot, "python", "python.exe");
        var modelsRoot = ResolveModelsRoot();
        var checkpoint = Path.Combine(modelsRoot, "openclip-standard", "open_clip_pytorch_model.bin");
        if (!File.Exists(python) || !File.Exists(checkpoint))
        {
            File.WriteAllText(Path.Combine(_root, "agent-startup-error.log"),
                $"Private runtime or standard model missing.{Environment.NewLine}{python}{Environment.NewLine}{checkpoint}");
            return;
        }
        File.Delete(Path.Combine(_root, "real-index.cancel"));
        File.Delete(Path.Combine(_root, "real-index.pause"));
        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = workerRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-m", "mlccs_worker.batch_index", "--library", library, "--data-root", _root,
            "--models-root", modelsRoot, "--interval-seconds",
            _configuration.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--batch-size", _configuration.BatchSize.ToString(),
            "--decoder-workers", _configuration.DecoderWorkers.ToString(),
            "--resource-policy", _configuration.ResourcePolicy,
            "--extra-models-root", Path.Combine(_root, "models"),
            "--speech-model", _configuration.SpeechModel
        }) start.ArgumentList.Add(argument);
        if (_configuration.Speech && Directory.Exists(Path.Combine(_root, "models", _configuration.SpeechModel)))
            start.ArgumentList.Add("--speech-enabled");
        if (_configuration.Ocr &&
            Directory.Exists(Path.Combine(_root, "models", "ppocrv5-mobile-det")) &&
            Directory.Exists(Path.Combine(_root, "models", "ppocrv5-mobile-rec")))
            start.ArgumentList.Add("--ocr-enabled");
        _indexer = Process.Start(start);
        if (_indexer is null) return;
        _activeLibrary = library;
        _activeIndexStartedUtc = DateTimeOffset.UtcNow;
        _indexer.EnableRaisingEvents = true;
        _indexer.Exited += (_, _) =>
        {
            lock (_indexerGate)
            {
                _indexer?.Dispose();
                _indexer = null;
                _activeIndexStartedUtc = null;
                _activeLibrary = null;
                if (!_configuration.Libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
                {
                    try { RemoveLibraryIndex(library); }
                    catch (Exception error)
                    {
                        AppendLog(Path.Combine(_root, "library-remove.log"),
                            $"{library}: {error.Message}");
                    }
                }
                if (_pendingLibraries.Count > 0) StartNextIndexerLocked();
            }
        };
        try
        {
            _indexer.PriorityClass = _configuration.ResourcePolicy.StartsWith("adaptive-full", StringComparison.Ordinal)
                ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
        }
        catch { }
        var logPath = Path.Combine(_root, "real-index.log");
        _indexer.OutputDataReceived += (_, args) => AppendLog(logPath, args.Data);
        _indexer.ErrorDataReceived += (_, args) => AppendLog(logPath, args.Data);
        _indexer.BeginOutputReadLine();
        _indexer.BeginErrorReadLine();
    }

    private string ResolveModelsRoot()
    {
        var local = Path.Combine(_root, "models");
        var packaged = Path.Combine(_releaseRoot, "worker", "models");
        return File.Exists(Path.Combine(local, "openclip-standard", "open_clip_pytorch_model.bin")) ? local : packaged;
    }

    private void AppendLog(string path, string? line)
    {
        if (line is null) return;
        lock (_logGate) File.AppendAllText(path, line + Environment.NewLine);
    }

    private object Pause()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "real-index.pause"), DateTimeOffset.UtcNow.ToString("O"));
        return new { paused = true };
    }

    private object Resume()
    {
        File.Delete(Path.Combine(_root, "real-index.pause"));
        return new { paused = false };
    }

    private object Cancel()
    {
        File.WriteAllText(Path.Combine(_root, "real-index.cancel"), DateTimeOffset.UtcNow.ToString("O"));
        return new { cancelled = true };
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var currentSid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current user SID unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(currentSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private void OpenUi()
    {
        var executable = Path.Combine(_releaseRoot, "ui", "MLCCS.VideoSearch.UI.exe");
        if (File.Exists(executable)) Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    }

    private async Task ExitSafelyAsync()
    {
        Pause();
        await Task.Delay(300);
        _shutdown.Cancel();
        lock (_indexerGate)
        {
            if (_indexer is { HasExited: false }) _indexer.Kill(true);
        }
        _searchWorker?.Dispose();
        if (_modelDownloader is { HasExited: false }) _modelDownloader.Kill(true);
        lock (_watcherGate)
        {
            foreach (var watcher in _libraryWatchers.Values)
                watcher.Dispose();
            _libraryWatchers.Clear();
        }
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}

internal sealed class SearchWorkerClient : IDisposable
{
    private readonly string _python;
    private readonly string _workingDirectory;
    private readonly string _database;
    private readonly string _modelsRoot;
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public SearchWorkerClient(string python, string workingDirectory, string database, string modelsRoot, string logPath)
    {
        _python = python;
        _workingDirectory = workingDirectory;
        _database = database;
        _modelsRoot = modelsRoot;
        _logPath = logPath;
    }

    public async Task<JsonElement> SearchAsync(string query, string source, string library, int limit,
        string phoneticLevel,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Exception? firstError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureStartedAsync(cancellationToken);
                    var request = JsonSerializer.Serialize(
                        new { query, source, library, limit, phoneticLevel });
                    await _process!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
                    await _process.StandardInput.FlushAsync(cancellationToken);
                    var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
                        ?? throw new InvalidOperationException("搜索 Worker 意外退出。");
                    using var response = JsonDocument.Parse(line);
                    if (!response.RootElement.GetProperty("ok").GetBoolean())
                        throw new InvalidOperationException(response.RootElement.GetProperty("error").GetString());
                    return response.RootElement.GetProperty("results").Clone();
                }
                catch (Exception error) when (attempt == 0 && error is not OperationCanceledException)
                {
                    firstError = error;
                    ResetProcess();
                    AppendDiagnostic($"搜索 Worker 已自动重启：{error.Message}");
                }
            }
            throw new InvalidOperationException($"搜索 Worker 重试失败：{firstError?.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        ResetProcess();
        var start = new ProcessStartInfo
        {
            FileName = _python,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-m", "mlccs_worker.search_server", "--database", _database, "--models-root", _modelsRoot
        }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动搜索 Worker。");
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) File.AppendAllText(_logPath, args.Data + Environment.NewLine);
        };
        _process.BeginErrorReadLine();
        var ready = await _process.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new InvalidOperationException("搜索 Worker 启动失败。");
        using var document = JsonDocument.Parse(ready);
        if (!document.RootElement.TryGetProperty("ready", out var value) || !value.GetBoolean())
            throw new InvalidOperationException("搜索 Worker 未进入就绪状态。");
    }

    private void AppendDiagnostic(string message)
    {
        try { File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}"); }
        catch { }
    }

    private void ResetProcess()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch { }
        process.Dispose();
    }

    public void Dispose()
    {
        ResetProcess();
        _gate.Dispose();
    }
}
