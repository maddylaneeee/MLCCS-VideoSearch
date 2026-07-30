namespace MLCCS.VideoSearch.Core.Contracts;

public sealed record PrivacySettings(bool HelpImprove = true, bool FullLogUploadConsent = false);
public sealed record IndexingSettings(
    bool Filename = true, bool Visual = true, bool Speech = false, bool Ocr = false,
    bool Background = true, string BatteryPolicy = "reduced", string Concurrency = "adaptive");
public sealed record SearchSettings(string PhoneticExpansion = "medium");
public sealed record UpdateSettings(bool AutomaticCheck = true, string Channel = "stable", string? SkippedVersion = null);
public sealed record StorageSettings(
    string DataRoot, string ModelRoot, string CacheRoot, string IndexRoot,
    long CacheLimitBytes = 20L * 1024 * 1024 * 1024);

public sealed record AppSettings(
    int SchemaVersion,
    PrivacySettings Privacy,
    IndexingSettings Indexing,
    SearchSettings Search,
    UpdateSettings Updates,
    StorageSettings Storage)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings CreateDefault(string root) => new(
        CurrentSchemaVersion,
        new(), new(), new(), new(),
        new(Path.Combine(root, "data"), Path.Combine(root, "models"), Path.Combine(root, "cache"), Path.Combine(root, "index")));

    public AppSettings EnforceCapabilities(HardwareCapabilities hardware) =>
        hardware.CudaAvailable ? this : this with { Indexing = Indexing with { Speech = false } };
}

public sealed record HardwareCapabilities(
    string WindowsVersion, int LogicalProcessors, ulong MemoryBytes, ulong FreeDiskBytes,
    string? GpuName, bool CudaAvailable, Version? CudaDriverVersion, ulong VramBytes,
    bool OnBattery, bool Is64BitOperatingSystem);

