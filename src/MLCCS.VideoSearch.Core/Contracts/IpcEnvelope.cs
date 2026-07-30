using System.Text.Json;
using System.Text.Json.Serialization;

namespace MLCCS.VideoSearch.Core.Contracts;

public sealed record StableError(string Code, string Summary, string? Remediation = null);

public sealed record IpcEnvelope(
    string ProtocolVersion,
    Guid RequestId,
    Guid? TaskId,
    DateTimeOffset TimestampUtc,
    string Kind,
    string Stage,
    double Progress,
    bool Recoverable,
    StableError? Error,
    JsonElement? Payload)
{
    public const string CurrentProtocol = "1.0";

    public static IpcEnvelope Create(string kind, string stage = "idle", double progress = 0, Guid? taskId = null) =>
        new(CurrentProtocol, Guid.NewGuid(), taskId, DateTimeOffset.UtcNow, kind, stage,
            Math.Clamp(progress, 0, 1), true, null, null);

    public void Validate()
    {
        if (!string.Equals(ProtocolVersion, CurrentProtocol, StringComparison.Ordinal))
            throw new ProtocolException("IPC_PROTOCOL_INCOMPATIBLE", $"Protocol {ProtocolVersion} is incompatible with {CurrentProtocol}.");
        if (RequestId == Guid.Empty || string.IsNullOrWhiteSpace(Kind) || string.IsNullOrWhiteSpace(Stage))
            throw new ProtocolException("IPC_INVALID_ENVELOPE", "The message envelope is incomplete.");
        if (Progress is < 0 or > 1)
            throw new ProtocolException("IPC_INVALID_PROGRESS", "Progress must be between zero and one.");
    }
}

public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

