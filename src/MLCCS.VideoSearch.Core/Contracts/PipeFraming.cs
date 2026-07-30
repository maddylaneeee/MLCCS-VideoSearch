using System.Buffers.Binary;
using System.Text.Json;

namespace MLCCS.VideoSearch.Core.Contracts;

public static class PipeFraming
{
    public const int MaximumFrameBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        envelope.Validate();
        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options);
        if (body.Length > MaximumFrameBytes)
            throw new ProtocolException("IPC_FRAME_TOO_LARGE", $"Frame is {body.Length} bytes.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, body.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IpcEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new ProtocolException("IPC_FRAME_TOO_LARGE", $"Rejected frame length {length}.");

        var body = new byte[length];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<IpcEnvelope>(body, JsonDefaults.Options)
            ?? throw new ProtocolException("IPC_INVALID_JSON", "The frame did not contain an envelope.");
        envelope.Validate();
        return envelope;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }
}

