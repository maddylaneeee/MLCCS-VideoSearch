using System.Net.Http.Json;

namespace MLCCS.VideoSearch.Core.Updates;

public sealed class UpdateClient(HttpClient httpClient)
{
    public static readonly Uri StableManifest = new("https://lixinchen.ca/docs/mlccs-video-search/stable/latest.json");

    public async Task<ReleaseManifest?> CheckAsync(Version current, bool automaticCheckEnabled,
        string? skippedVersion, CancellationToken cancellationToken)
    {
        if (!automaticCheckEnabled) return null;
        var manifest = await httpClient.GetFromJsonAsync<ReleaseManifest>(StableManifest, cancellationToken)
                       ?? throw new InvalidDataException("UPDATE_MANIFEST_INVALID");
        using var key = EmbeddedUpdateKey.Create();
        if (!UpdateVerifier.VerifyManifest(manifest, key))
            throw new System.Security.Cryptography.CryptographicException("UPDATE_SIGNATURE_INVALID");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("UPDATE_PROTOCOL_INCOMPATIBLE");
        if (string.Equals(skippedVersion, manifest.ProductVersion, StringComparison.OrdinalIgnoreCase)) return null;
        return Version.Parse(manifest.ProductVersion) > current ? manifest : null;
    }

    public async Task<string> DownloadAsync(ReleaseComponent component, string downloadDirectory,
        IProgress<(long Downloaded, long Total)>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadDirectory);
        var target = Path.Combine(downloadDirectory, $"{component.Id}-{component.Sha256}.zip");
        var partial = target + ".partial";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset > component.Size) { File.Delete(partial); offset = 0; }
        if (offset == component.Size && offset > 0)
        {
            try
            {
                await using var completed = File.OpenRead(partial);
                await UpdateVerifier.VerifyArchiveAsync(completed, component, cancellationToken);
            }
            catch { File.Delete(partial); throw; }
            File.Move(partial, target, true);
            progress?.Report((component.Size, component.Size));
            return target;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, component.Url);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) offset = 0;
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create,
                         FileAccess.Write, FileShare.None, 1024 * 1024, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[1024 * 1024];
            var downloaded = offset;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report((downloaded, component.Size));
            }
            await output.FlushAsync(cancellationToken);
        }
        await using var archive = File.OpenRead(partial);
        await UpdateVerifier.VerifyArchiveAsync(archive, component, cancellationToken);
        File.Move(partial, target, true);
        return target;
    }
}
