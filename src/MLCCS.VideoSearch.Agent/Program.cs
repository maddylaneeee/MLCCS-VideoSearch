using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MLCCS.VideoSearch.Core.Contracts;
using MLCCS.VideoSearch.Core.Privacy;
using MLCCS.VideoSearch.Core.Storage;
using MLCCS.VideoSearch.Core.Updates;

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
    public int SchemaVersion { get; set; } = 1;
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
    public bool AutomaticUpdates { get; set; } = true;
    public string? SkippedVersion { get; set; }
    public string PhoneticExpansion { get; set; } = "medium";
    public int SearchModelIdleMinutes { get; set; } = 10;
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
    private int _activeSearchRequests;
    private readonly QdrantProcessManager _qdrant;
    private JsonElement? _hardwareStatus;
    private ReleaseManifest? _availableUpdate;
    private DateTime _lastConfigCheckUtc = DateTime.MinValue;

    public AgentHost(string root)
    {
        _root = root;
        _releaseRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        _configPath = Path.Combine(_root, "libraries.json");
        _qdrant = new QdrantProcessManager(_root, _releaseRoot);
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
            // IPC must be available even if a driver or hardware probe is slow or wedged.
            // Indexing waits for a successful probe, but library/settings operations do not.
            _ = DetectHardwareAtStartupAsync(cancellationToken);
            await Task.WhenAll(AcceptPipeAsync(cancellationToken), QueueLoopAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(_root, $"agent-crash-{DateTime.UtcNow:yyyyMMddHHmmss}.log"),
                DiagnosticRedactor.Redact(exception.ToString(), Environment.UserName));
        }
    }

    private async Task DetectHardwareAtStartupAsync(CancellationToken cancellationToken)
    {
        try { _hardwareStatus = await DetectHardwareAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { AppendLog(Path.Combine(_root, "hardware.log"), error.Message); }
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
                "release.health" => await ReleaseHealthAsync(cancellationToken),
                "agent.shutdown" => RequestShutdown(),
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
                "update.check" => await CheckUpdateAsync(request, cancellationToken),
                "update.apply" => await ApplyUpdateAsync(cancellationToken),
                "update.skip" => SkipUpdate(request),
                "update.status" => UpdateStatus(),
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
            modelDownloadStatus,
            hardware = _hardwareStatus,
            searchWorker = _searchWorker?.Status(_configuration.SearchModelIdleMinutes) ?? new
            {
                running = false, pid = (int?)null, busy = false, lastUsedUtc = (DateTimeOffset?)null,
                idleSecondsRemaining = 0d
            },
            qdrant = _qdrant.Status()
        };
    }

    private async Task<object> ReleaseHealthAsync(CancellationToken cancellationToken)
    {
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var python = ResolvePrivatePython();
        var modelsRoot = ResolveRequiredComponentRoot("visual-model", "openclip-standard", "open_clip_pytorch_model.bin");
        var textModelsRoot = ResolveRequiredComponentRoot("text-model", "bge-small", "config.json");
        if (!File.Exists(python) ||
            !File.Exists(Path.Combine(modelsRoot, "openclip-standard", "open_clip_pytorch_model.bin")) ||
            !File.Exists(Path.Combine(textModelsRoot, "bge-small", "config.json")))
            throw new InvalidDataException("RELEASE_REQUIRED_COMPONENT_MISSING");
        await _qdrant.EnsureStartedAsync(cancellationToken);
        if (_searchWorker is null || !_searchWorker.Matches(_qdrant.Endpoint!, _qdrant.ApiKey))
        {
            _searchWorker?.Dispose();
            _searchWorker = new SearchWorkerClient(python, workerRoot, Path.Combine(_root, "catalog.db"),
                modelsRoot, textModelsRoot, Path.Combine(_root, "search-worker.log"),
                _qdrant.Endpoint!, _qdrant.ApiKey);
        }
        var workerPid = await _searchWorker.EnsureHealthyAsync(cancellationToken);
        return new { agent = "healthy", workerPid, qdrant = _qdrant.Status() };
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
        foreach (var (table, collection) in new[]
                 {
                     ("visual_segments", "visual_v1"), ("speech_windows", "speech_v1"),
                     ("ocr_observations", "ocr_v1")
                 })
        {
            using var dependent = connection.CreateCommand();
            dependent.Transaction = transaction;
            dependent.CommandText =
                $"INSERT INTO vector_outbox(operation,collection,point_id,payload_json,created_utc) " +
                $"SELECT 'delete',$collection,id,'{{}}',$utc FROM {table} WHERE asset_id IN " +
                "(SELECT asset_id FROM media_assets WHERE library_root=$library) " +
                "ON CONFLICT(operation,collection,point_id) DO UPDATE SET completed_utc=NULL,created_utc=$utc";
            dependent.Parameters.AddWithValue("$library", library);
            dependent.Parameters.AddWithValue("$collection", collection);
            dependent.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            dependent.ExecuteNonQuery();
        }
        using (var assets = connection.CreateCommand())
        {
            assets.Transaction = transaction;
            assets.CommandText = "DELETE FROM media_assets WHERE library_root=$library";
            assets.Parameters.AddWithValue("$library", library);
            assets.ExecuteNonQuery();
        }
        using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = "DELETE FROM search_fts WHERE asset_id IN " +
                              "(SELECT id FROM assets WHERE library_id IN " +
                              "(SELECT id FROM libraries WHERE canonical_root=$library))";
            fts.Parameters.AddWithValue("$library", library);
            fts.ExecuteNonQuery();
        }
        using (var canonicalAssets = connection.CreateCommand())
        {
            canonicalAssets.Transaction = transaction;
            canonicalAssets.CommandText = "DELETE FROM assets WHERE library_id IN " +
                                          "(SELECT id FROM libraries WHERE canonical_root=$library)";
            canonicalAssets.Parameters.AddWithValue("$library", library);
            canonicalAssets.ExecuteNonQuery();
        }
        using (var libraryRow = connection.CreateCommand())
        {
            libraryRow.Transaction = transaction;
            libraryRow.CommandText = "DELETE FROM libraries WHERE canonical_root=$library";
            libraryRow.Parameters.AddWithValue("$library", library);
            libraryRow.ExecuteNonQuery();
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
        if (incoming.SchemaVersion != 1)
            throw new ProtocolException("SETTINGS_VERSION_UNSUPPORTED", "只支持设置 Schema 1。");
        incoming.Libraries = incoming.Libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        incoming.IntervalSeconds = Math.Clamp(incoming.IntervalSeconds, 2, 8);
        incoming.BatchSize = Math.Max(0, incoming.BatchSize);
        incoming.DecoderWorkers = Math.Max(0, incoming.DecoderWorkers);
        incoming.Filename = true;
        incoming.Visual = true;
        incoming.ResourcePolicy = incoming.ResourcePolicy is "adaptive-full" or "balanced" or "efficiency"
            ? incoming.ResourcePolicy : "adaptive-full";
        incoming.PhoneticExpansion = incoming.PhoneticExpansion is "off" or "low" or "medium" or "high"
            ? incoming.PhoneticExpansion : "medium";
        if (incoming.SpeechModel is not ("whisper-tiny" or "whisper-base" or "whisper-small" or
            "whisper-medium" or "whisper-large-v3-turbo" or "whisper-large-v3"))
            incoming.SpeechModel = "whisper-medium";
        incoming.SearchModelIdleMinutes = incoming.SearchModelIdleMinutes is 5 or 10 or 30
            ? incoming.SearchModelIdleMinutes : 10;
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
        JsonElement? filters = payload.TryGetProperty("filters", out var filtersElement) &&
                               filtersElement.ValueKind == JsonValueKind.Object
            ? filtersElement.Clone() : null;
        var sort = payload.TryGetProperty("sort", out var sortElement)
            ? sortElement.GetString() ?? "relevance" : "relevance";
        if (library.Length > 0 &&
            !_configuration.Libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
            throw new ProtocolException("IPC_INVALID_LIBRARY", "所选资源文件夹已不在当前资源库中。");
        if (filters is { } filterObject && filterObject.TryGetProperty("libraries", out var filterLibraries))
        {
            foreach (var selected in filterLibraries.EnumerateArray().Select(item => item.GetString())
                         .Where(item => !string.IsNullOrWhiteSpace(item)))
                if (!_configuration.Libraries.Contains(selected!, StringComparer.OrdinalIgnoreCase))
                    throw new ProtocolException("IPC_INVALID_LIBRARY", "筛选条件包含不属于当前配置的资源库。");
        }
        if (sort is not ("relevance" or "modified-desc" or "filename"))
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "Unknown search sort mode.");
        var limit = payload.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 60;
        if (string.IsNullOrWhiteSpace(query))
            return new { results = Array.Empty<object>(), elapsedMs = 0d };
        await EnsureSupportedHardwareAsync(cancellationToken);
        Interlocked.Increment(ref _activeSearchRequests);
        try
        {
            await _qdrant.EnsureStartedAsync(cancellationToken);
            var modelsRoot = ResolveRequiredComponentRoot("visual-model", "openclip-standard", "open_clip_pytorch_model.bin");
            var textModelsRoot = ResolveRequiredComponentRoot("text-model", "bge-small", "config.json");
            if (_searchWorker is not null && !_searchWorker.Matches(_qdrant.Endpoint!, _qdrant.ApiKey))
            {
                _searchWorker.Dispose();
                _searchWorker = null;
            }
            _searchWorker ??= new SearchWorkerClient(
                ResolvePrivatePython(),
                Path.Combine(_releaseRoot, "worker"),
                Path.Combine(_root, "catalog.db"),
                modelsRoot,
                textModelsRoot,
                Path.Combine(_root, "search-worker.log"),
                _qdrant.Endpoint!, _qdrant.ApiKey);
            var started = Stopwatch.GetTimestamp();
            var response = await _searchWorker.SearchAsync(query, source, library, filters, sort,
                Math.Clamp(limit, 1, 100), _configuration.PhoneticExpansion, cancellationToken);
            return new { results = response, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        }
        finally { Interlocked.Decrement(ref _activeSearchRequests); }
    }

    private async Task<JsonElement> DetectHardwareAsync(CancellationToken cancellationToken)
    {
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var python = ResolvePrivatePython();
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
        using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCancellation.CancelAfter(TimeSpan.FromSeconds(90));
        var outputTask = process.StandardOutput.ReadToEndAsync(probeCancellation.Token);
        var errorTask = process.StandardError.ReadToEndAsync(probeCancellation.Token);
        try
        {
            await process.WaitForExitAsync(probeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
            try { await Task.WhenAll(outputTask, errorTask); }
            catch (OperationCanceledException) { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("硬件检测超过 90 秒，已终止本次检测；Agent 其他功能仍可使用。");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"硬件检测失败：{error.Trim()}");
        using var document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }

    private async Task EnsureSupportedHardwareAsync(CancellationToken cancellationToken)
    {
        _hardwareStatus ??= await DetectHardwareAsync(cancellationToken);
        if (_hardwareStatus.Value.TryGetProperty("supported", out var supported) && supported.GetBoolean())
            return;
        var issues = _hardwareStatus.Value.TryGetProperty("support_issues", out var issueNode)
            ? string.Join("；", issueNode.EnumerateArray().Select(item => item.GetString()))
            : "硬件检测未通过";
        throw new ProtocolException("CAPABILITY_UNSUPPORTED_HARDWARE",
            $"v1.0.0 只支持 Windows 10 1809+/11 x64、4 GB 级 NVIDIA GPU（允许驱动保留少量显存）和兼容驱动：{issues}");
    }

    private async Task<object> CheckUpdateAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        var interactive = request.Payload is { } payload && payload.TryGetProperty("interactive", out var value) && value.GetBoolean();
        if (!interactive && !_configuration.AutomaticUpdates)
            return new { available = false, message = "自动检查更新已关闭。" };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var client = new UpdateClient(http);
        _availableUpdate = await client.CheckAsync(new Version(1, 0, 0), true,
            _configuration.SkippedVersion, cancellationToken);
        if (_availableUpdate is null)
            return new { available = false, message = "当前已是最新稳定版。" };
        return new { available = true, version = _availableUpdate.ProductVersion,
            releaseNotes = _availableUpdate.ReleaseNotes, mandatory = _availableUpdate.Mandatory,
            message = $"发现版本 {_availableUpdate.ProductVersion}。" };
    }

    private object SkipUpdate(IpcEnvelope request)
    {
        var version = ReadString(request, "version");
        _configuration.SkippedVersion = version;
        SaveConfiguration();
        return new { skipped = version };
    }

    private async Task<object> ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        if (_availableUpdate is null)
            throw new ProtocolException("UPDATE_NOT_CHECKED", "请先检查更新。");
        var installRoot = Path.GetFullPath(Path.Combine(_releaseRoot, ".."));
        var installerRoot = Path.Combine(installRoot, "_installer");
        var downloads = Path.Combine(installerRoot, "downloads");
        var statusPath = Path.Combine(_root, "update-status.json");
        var installedHashes = ReadInstalledComponentHashes(Path.Combine(installerRoot, "installed.json"));
        var installedIds = installedHashes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = _availableUpdate.Components.Where(item =>
            item.InstallScope == "current" || item.Required || installedIds.Contains(item.Id))
            .Where(item => !installedHashes.TryGetValue(item.Id, out var hash) ||
                           !hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (changed.Length == 0) return new { started = false, version = _availableUpdate.ProductVersion };
        var drive = new DriveInfo(Path.GetPathRoot(installRoot)!);
        if (drive.AvailableFreeSpace < changed.Sum(item => item.Size) * 2 + 512L * 1024 * 1024)
            throw new IOException("UPDATE_INSUFFICIENT_SPACE");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new UpdateClient(http);
        long completed = 0;
        var overallTotal = Math.Max(1, changed.Sum(item => item.Size));
        foreach (var component in changed)
        {
            var baseCompleted = completed;
            var progress = new Progress<(long Downloaded, long Total)>(value =>
            {
                var downloaded = Math.Min(overallTotal, baseCompleted + value.Downloaded);
                var temporary = statusPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    stage = "downloading", downloaded, total = overallTotal,
                    progress = (double)downloaded / overallTotal, updatedUtc = DateTimeOffset.UtcNow
                }, JsonOptions));
                File.Move(temporary, statusPath, true);
            });
            await client.DownloadAsync(component, downloads, progress, cancellationToken);
            completed += component.Size;
        }
        var manifestPath = Path.Combine(downloads, $"release-manifest-{_availableUpdate.ProductVersion}.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(_availableUpdate, JsonOptions));
        var updater = Path.Combine(installerRoot, "MLCCS.VideoSearch.Updater.exe");
        if (!File.Exists(updater)) throw new FileNotFoundException("外置更新器缺失。", updater);
        File.WriteAllText(statusPath, JsonSerializer.Serialize(new
        {
            stage = "applying", downloaded = overallTotal, total = overallTotal,
            progress = 1d, updatedUtc = DateTimeOffset.UtcNow
        }, JsonOptions));
        var start = new ProcessStartInfo(updater) { UseShellExecute = true, WorkingDirectory = installerRoot };
        foreach (var argument in new[] { "apply", manifestPath, downloads, installRoot }) start.ArgumentList.Add(argument);
        Process.Start(start);
        return new { started = true, version = _availableUpdate.ProductVersion };
    }

    private static Dictionary<string, string> ReadInstalledComponentHashes(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var components = document.RootElement.EnumerateObject().FirstOrDefault(item =>
            item.Name.Equals("components", StringComparison.OrdinalIgnoreCase)).Value;
        if (components.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in components.EnumerateArray())
        {
            string? id = null, hash = null;
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) id = property.Value.GetString();
                if (property.Name.Equals("sha256", StringComparison.OrdinalIgnoreCase)) hash = property.Value.GetString();
            }
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(hash)) result[id] = hash;
        }
        return result;
    }

    private object UpdateStatus()
    {
        var path = Path.Combine(_root, "update-status.json");
        if (!File.Exists(path)) return new { stage = "idle", progress = 0d };
        using var document = JsonDocument.Parse(File.ReadAllText(path));
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
            FileName = ResolvePrivatePython(),
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
            _searchWorker?.ReleaseIfIdle(TimeSpan.FromMinutes(_configuration.SearchModelIdleMinutes));
            if (_indexer is not { HasExited: false } && Volatile.Read(ref _activeSearchRequests) == 0 &&
                (_searchWorker is null || !_searchWorker.Running))
                _qdrant.Stop();
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
                File.WriteAllText(Path.Combine(_root, "configuration-error.log"),
                    DiagnosticRedactor.Redact(error.ToString(), Environment.UserName));
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
            // QueueLoop starts the worker. Keeping startup out of the IPC request makes
            // library.add/library.rescan return immediately even when Qdrant is slow.
        }
    }

    private void StartNextIndexerLocked()
    {
        if (_pendingLibraries.Count == 0 || !_configuration.Visual) return;
        if (_hardwareStatus is not { } hardware ||
            !hardware.TryGetProperty("supported", out var supported) || !supported.GetBoolean())
        {
            File.WriteAllText(Path.Combine(_root, "agent-startup-error.log"),
                "CAPABILITY_UNSUPPORTED_HARDWARE: 索引已阻止。请更新 Windows/NVIDIA 驱动并确保使用 4 GB 级 NVIDIA GPU。");
            return;
        }
        var library = _pendingLibraries.Dequeue();
        if (!_configuration.Libraries.Contains(library, StringComparer.OrdinalIgnoreCase))
        {
            StartNextIndexerLocked();
            return;
        }
        var workerRoot = Path.Combine(_releaseRoot, "worker");
        var python = ResolvePrivatePython();
        var modelsRoot = ResolveRequiredComponentRoot("visual-model", "openclip-standard", "open_clip_pytorch_model.bin");
        var textModelsRoot = ResolveRequiredComponentRoot("text-model", "bge-small", "config.json");
        var ocrModelsRoot = ResolveOcrModelsRoot();
        var checkpoint = Path.Combine(modelsRoot, "openclip-standard", "open_clip_pytorch_model.bin");
        if (!File.Exists(python) || !File.Exists(checkpoint))
        {
            File.WriteAllText(Path.Combine(_root, "agent-startup-error.log"),
                DiagnosticRedactor.Redact(
                    $"Private runtime or standard model missing.{Environment.NewLine}{python}{Environment.NewLine}{checkpoint}",
                    Environment.UserName));
            return;
        }
        try { _qdrant.EnsureStartedAsync(_shutdown.Token).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(_root, "agent-startup-error.log"),
                DiagnosticRedactor.Redact(error.Message, Environment.UserName));
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
            "--models-root", modelsRoot, "--text-models-root", textModelsRoot, "--interval-seconds",
            _configuration.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--batch-size", _configuration.BatchSize.ToString(),
            "--decoder-workers", _configuration.DecoderWorkers.ToString(),
            "--resource-policy", _configuration.ResourcePolicy,
            "--extra-models-root", Path.Combine(_root, "models"),
            "--ocr-models-root", ocrModelsRoot,
            "--speech-model", _configuration.SpeechModel
        }) start.ArgumentList.Add(argument);
        if (_configuration.Speech && Directory.Exists(Path.Combine(_root, "models", _configuration.SpeechModel)))
            start.ArgumentList.Add("--speech-enabled");
        if (_configuration.Ocr && Directory.Exists(Path.Combine(ocrModelsRoot, "ppocrv5-mobile-det")) &&
            Directory.Exists(Path.Combine(ocrModelsRoot, "ppocrv5-mobile-rec")))
            start.ArgumentList.Add("--ocr-enabled");
        start.Environment["MLCCS_QDRANT_URL"] = _qdrant.Endpoint!.ToString();
        start.Environment["MLCCS_QDRANT_API_KEY"] = _qdrant.ApiKey;
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
                // QueueLoop starts the next library without holding this exit callback.
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

    private string ResolvePrivatePython()
    {
        var runtimeRoot = ComponentLayout.ResolveActiveRoot(_releaseRoot, "runtime", "private-runtime");
        var candidates = Directory.Exists(runtimeRoot)
            ? Directory.EnumerateFiles(runtimeRoot, "python.exe", SearchOption.AllDirectories).ToArray()
            : [];
        return candidates.Length == 1 ? candidates[0] : Path.Combine(runtimeRoot, "missing-python.exe");
    }

    private string ResolveRequiredComponentRoot(string scope, string modelDirectory, string sentinel)
    {
        var root = ComponentLayout.ResolveActiveRoot(_releaseRoot, scope, scope);
        if (!Directory.Exists(root)) return Path.Combine(root, "missing");
        var sentinels = Directory.EnumerateFiles(root, sentinel, SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), modelDirectory,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sentinels.Length != 1) return Path.Combine(root, "missing");
        return Directory.GetParent(Path.GetDirectoryName(sentinels[0])!)!.FullName;
    }

    private string ResolveOcrModelsRoot()
    {
        var installed = ComponentLayout.ResolveActiveRoot(_releaseRoot, "ocr-models", "ocr-models");
        if (Directory.Exists(Path.Combine(installed, "ppocrv5-mobile-det")) &&
            Directory.Exists(Path.Combine(installed, "ppocrv5-mobile-rec")))
            return installed;
        return Path.Combine(_root, "models");
    }

    private void AppendLog(string path, string? line)
    {
        if (line is null) return;
        lock (_logGate) File.AppendAllText(path,
            DiagnosticRedactor.Redact(line, Environment.UserName) + Environment.NewLine);
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

    private object RequestShutdown()
    {
        _ = Task.Run(async () => { await Task.Delay(300); await ExitSafelyAsync(); });
        return new { accepted = true };
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
        _qdrant.Dispose();
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
    private readonly string _textModelsRoot;
    private readonly string _logPath;
    private readonly Uri _qdrantEndpoint;
    private readonly string _qdrantApiKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private DateTimeOffset? _lastUsedUtc;
    private bool _busy;

    public SearchWorkerClient(string python, string workingDirectory, string database, string modelsRoot,
        string textModelsRoot, string logPath,
        Uri qdrantEndpoint, string qdrantApiKey)
    {
        _python = python;
        _workingDirectory = workingDirectory;
        _database = database;
        _modelsRoot = modelsRoot;
        _textModelsRoot = textModelsRoot;
        _logPath = logPath;
        _qdrantEndpoint = qdrantEndpoint;
        _qdrantApiKey = qdrantApiKey;
    }

    public async Task<JsonElement> SearchAsync(string query, string source, string library,
        JsonElement? filters, string sort, int limit, string phoneticLevel,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        _busy = true;
        try
        {
            Exception? firstError = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await EnsureStartedAsync(cancellationToken);
                    var request = JsonSerializer.Serialize(
                        new { query, source, library, filters, sort, limit, phoneticLevel });
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
            _lastUsedUtc = DateTimeOffset.UtcNow;
            _busy = false;
            _gate.Release();
        }
    }

    public object Status(int idleMinutes)
    {
        var running = _process is { HasExited: false };
        var remaining = _lastUsedUtc is null ? 0 : Math.Max(0,
            (TimeSpan.FromMinutes(idleMinutes) - (DateTimeOffset.UtcNow - _lastUsedUtc.Value)).TotalSeconds);
        return new { running, pid = running ? _process!.Id : (int?)null, busy = _busy,
            lastUsedUtc = _lastUsedUtc, idleSecondsRemaining = remaining };
    }

    public bool Running => _process is { HasExited: false };

    public bool Matches(Uri endpoint, string apiKey) =>
        _qdrantEndpoint == endpoint && string.Equals(_qdrantApiKey, apiKey, StringComparison.Ordinal);

    public async Task<int> EnsureHealthyAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            _lastUsedUtc = DateTimeOffset.UtcNow;
            return _process!.Id;
        }
        finally { _gate.Release(); }
    }

    public bool ReleaseIfIdle(TimeSpan idleTimeout)
    {
        if (_busy || _lastUsedUtc is null || DateTimeOffset.UtcNow - _lastUsedUtc < idleTimeout)
            return false;
        if (!_gate.Wait(0)) return false;
        try
        {
            if (_busy || _lastUsedUtc is null || DateTimeOffset.UtcNow - _lastUsedUtc < idleTimeout)
                return false;
            ResetProcess();
            return true;
        }
        finally { _gate.Release(); }
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
            "-m", "mlccs_worker.search_server", "--database", _database, "--models-root", _modelsRoot,
            "--text-models-root", _textModelsRoot
        }) start.ArgumentList.Add(argument);
        start.Environment["MLCCS_QDRANT_URL"] = _qdrantEndpoint.ToString();
        start.Environment["MLCCS_QDRANT_API_KEY"] = _qdrantApiKey;
        _process = Process.Start(start) ?? throw new InvalidOperationException("无法启动搜索 Worker。");
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) File.AppendAllText(_logPath,
                DiagnosticRedactor.Redact(args.Data, Environment.UserName) + Environment.NewLine);
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
        try { File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} " +
                  DiagnosticRedactor.Redact(message, Environment.UserName) + Environment.NewLine); }
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
