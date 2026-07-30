using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MLCCS.VideoSearch.Core.Updates;

public sealed record UpdateManifest(
    int ProtocolVersion, string AppVersion, string MinimumCompatibleVersion, Uri DownloadUrl,
    long Size, string Sha256, string ArchiveSignature, DateTimeOffset PublishedUtc,
    string ReleaseNotes, bool Mandatory, string ManifestSignature);

public static class UpdateVerifier
{
    public static bool VerifyManifest(UpdateManifest manifest, ECDsa publicKey)
    {
        var signature = Convert.FromBase64String(manifest.ManifestSignature);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest.ProtocolVersion, manifest.AppVersion, manifest.MinimumCompatibleVersion,
            downloadUrl = manifest.DownloadUrl.ToString(), manifest.Size, manifest.Sha256,
            manifest.ArchiveSignature, manifest.PublishedUtc, manifest.ReleaseNotes, manifest.Mandatory
        }, MLCCS.VideoSearch.Core.Contracts.JsonDefaults.Options);
        return publicKey.VerifyData(canonical, signature, HashAlgorithmName.SHA256);
    }

    public static async Task VerifyArchiveAsync(Stream archive, UpdateManifest manifest, ECDsa publicKey, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        if (bytes.LongLength != manifest.Size) throw new InvalidDataException("UPDATE_SIZE_MISMATCH");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(manifest.Sha256)))
            throw new InvalidDataException("UPDATE_HASH_MISMATCH");
        if (!publicKey.VerifyData(bytes, Convert.FromBase64String(manifest.ArchiveSignature), HashAlgorithmName.SHA256))
            throw new CryptographicException("UPDATE_SIGNATURE_INVALID");
    }
}
