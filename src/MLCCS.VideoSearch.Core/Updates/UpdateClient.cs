using System.Net.Http.Json;

namespace MLCCS.VideoSearch.Core.Updates;

public sealed class UpdateClient(HttpClient httpClient)
{
    public static readonly Uri StableManifest = new("https://lixinchen.ca/downloads/mlccs-videosearch/stable/latest.json");

    public async Task<UpdateManifest?> CheckAsync(Version current, bool automaticCheckEnabled, CancellationToken cancellationToken)
    {
        if (!automaticCheckEnabled) return null;
        var manifest = await httpClient.GetFromJsonAsync<UpdateManifest>(StableManifest, cancellationToken) ?? throw new InvalidDataException("UPDATE_MANIFEST_INVALID");
        using var key = EmbeddedUpdateKey.Create();
        if (!UpdateVerifier.VerifyManifest(manifest, key)) throw new System.Security.Cryptography.CryptographicException("UPDATE_SIGNATURE_INVALID");
        if (manifest.ProtocolVersion != 1) throw new InvalidDataException("UPDATE_PROTOCOL_INCOMPATIBLE");
        return Version.Parse(manifest.AppVersion) > current ? manifest : null;
    }

    public async Task<string> DownloadAsync(UpdateManifest manifest, string stagingDirectory, IProgress<(long Downloaded, long Total)>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var target = Path.Combine(stagingDirectory, $"{manifest.AppVersion}.zip"); var partial = target + ".partial";
        var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, manifest.DownloadUrl);
        if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (offset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) offset = 0;
        response.EnsureSuccessStatusCode();
        await using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[1024 * 1024]; int read; var downloaded = offset;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            { await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); downloaded += read; progress?.Report((downloaded, manifest.Size)); }
            await output.FlushAsync(cancellationToken);
        }
        await using var archive = File.OpenRead(partial); using var key = EmbeddedUpdateKey.Create();
        await UpdateVerifier.VerifyArchiveAsync(archive, manifest, key, cancellationToken);
        File.Move(partial, target, true); return target;
    }
}
