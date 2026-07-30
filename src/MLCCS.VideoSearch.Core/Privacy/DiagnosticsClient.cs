using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace MLCCS.VideoSearch.Core.Privacy;

public sealed record DiagnosticUploadPreview(IReadOnlyList<string> Files, long ArchiveBytes, string RedactionSummary, bool UserConsented);
public sealed record DiagnosticUploadReceipt(string ReportId);

public sealed class DiagnosticsClient(HttpClient httpClient, Uri endpoint)
{
    public const int ChunkBytes = 512 * 1024;
    public const int MaximumBytes = 25 * 1024 * 1024;

    public async Task<DiagnosticUploadReceipt> UploadAsync(
        IReadOnlyDictionary<string, string> textFiles,
        string installationHash,
        string userNote,
        DiagnosticUploadPreview preview,
        IProgress<(long Sent, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        if (!preview.UserConsented) throw new InvalidOperationException("DIAGNOSTIC_CONSENT_REQUIRED");
        var archive = CreateRedactedArchive(textFiles);
        if (archive.Length > MaximumBytes) throw new InvalidDataException("DIAGNOSTIC_REPORT_TOO_LARGE");
        var sha = Convert.ToHexStringLower(SHA256.HashData(archive));
        var totalChunks = (archive.Length + ChunkBytes - 1) / ChunkBytes;
        var beginResponse = await httpClient.PostAsJsonAsync(new Uri(endpoint, "begin"), new
        { size = archive.Length, sha256 = sha, total_chunks = totalChunks, installation_hash = installationHash }, cancellationToken);
        beginResponse.EnsureSuccessStatusCode();
        var begin = await beginResponse.Content.ReadFromJsonAsync<BeginResponse>(cancellationToken: cancellationToken) ?? throw new InvalidDataException("DIAGNOSTIC_BEGIN_INVALID");
        for (var index = 0; index < totalChunks; index++)
        {
            var offset = index * ChunkBytes; var count = Math.Min(ChunkBytes, archive.Length - offset);
            using var content = new ByteArrayContent(archive, offset, count);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, $"chunk?report_id={Uri.EscapeDataString(begin.ReportId)}&index={index}")) { Content = content };
            request.Headers.Add("X-Upload-Token", begin.Token);
            using var response = await httpClient.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
            progress?.Report((offset + count, archive.Length));
        }
        using var completeRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "complete"))
        { Content = JsonContent.Create(new { report_id = begin.ReportId, user_note = userNote }) };
        completeRequest.Headers.Add("X-Upload-Token", begin.Token);
        using var complete = await httpClient.SendAsync(completeRequest, cancellationToken); complete.EnsureSuccessStatusCode();
        return new(begin.ReportId);
    }

    public async Task SendAnonymousEventAsync(object allowlistedEvent, bool helpImprove, CancellationToken cancellationToken)
    {
        if (!helpImprove) return;
        using var response = await httpClient.PostAsJsonAsync(new Uri(endpoint, "event"), allowlistedEvent, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static byte[] CreateRedactedArchive(IReadOnlyDictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var pair in files)
            {
                if (Path.GetFileName(pair.Key) != pair.Key) throw new InvalidDataException("DIAGNOSTIC_FILENAME_INVALID");
                var entry = zip.CreateEntry(pair.Key, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open()); writer.Write(DiagnosticRedactor.Redact(pair.Value, Environment.UserName));
            }
        }
        return stream.ToArray();
    }

    private sealed record BeginResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("report_id")] string ReportId,
        string Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("chunk_size")] int ChunkSize);
}

