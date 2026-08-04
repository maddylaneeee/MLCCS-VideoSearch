using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MLCCS.VideoSearch.Core.Contracts;

namespace MLCCS.VideoSearch.Core.Updates;

public sealed record ReleaseRequirements(
    string MinimumWindowsVersion,
    int MinimumWindowsBuild,
    string Architecture,
    string GpuVendor,
    long MinimumVramBytes,
    string CudaRuntime,
    bool CudaToolkitRequired);

public sealed record ReleaseFile(string Path, long Size, string Sha256);

public sealed record ReleaseComponent(
    string Id,
    string Name,
    string Description,
    Uri Url,
    string InstallScope,
    bool Required,
    bool DefaultSelected,
    long Size,
    string Sha256,
    IReadOnlyList<ReleaseFile> Files);

public sealed record ReleaseManifest(
    int SchemaVersion,
    string ProductVersion,
    string MinimumCompatibleVersion,
    ReleaseRequirements Requirements,
    string EntryPoint,
    DateTimeOffset PublishedUtc,
    string ReleaseNotes,
    bool Mandatory,
    IReadOnlyList<ReleaseComponent> Components,
    string KeyId,
    string ManifestSignature);

public static class UpdateVerifier
{
    public static byte[] CanonicalBytes(ReleaseManifest manifest)
    {
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.ProductVersion,
            manifest.MinimumCompatibleVersion,
            Requirements = new
            {
                manifest.Requirements.MinimumWindowsVersion,
                manifest.Requirements.MinimumWindowsBuild,
                manifest.Requirements.Architecture,
                manifest.Requirements.GpuVendor,
                manifest.Requirements.MinimumVramBytes,
                manifest.Requirements.CudaRuntime,
                manifest.Requirements.CudaToolkitRequired
            },
            manifest.EntryPoint,
            PublishedUtc = manifest.PublishedUtc.ToUniversalTime().ToString("O"),
            manifest.ReleaseNotes,
            manifest.Mandatory,
            Components = manifest.Components.Select(component => new
            {
                component.Id,
                component.Name,
                component.Description,
                Url = component.Url.AbsoluteUri,
                component.InstallScope,
                component.Required,
                component.DefaultSelected,
                component.Size,
                component.Sha256,
                Files = component.Files.Select(file => new { file.Path, file.Size, file.Sha256 }).ToArray()
            }).ToArray(),
            manifest.KeyId
        };
        return JsonSerializer.SerializeToUtf8Bytes(canonical, JsonDefaults.Options);
    }

    public static bool VerifyManifest(ReleaseManifest manifest, ECDsa publicKey)
    {
        if (manifest.SchemaVersion != 1 || manifest.KeyId != "manifest-v1" ||
            string.IsNullOrWhiteSpace(manifest.ManifestSignature))
            return false;
        try
        {
            var signature = Convert.FromBase64String(manifest.ManifestSignature);
            return signature.Length == 64 && publicKey.VerifyData(CanonicalBytes(manifest), signature,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (FormatException) { return false; }
    }

    public static async Task VerifyArchiveAsync(Stream archive, ReleaseComponent component,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long size = 0;
        int read;
        while ((read = await archive.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        if (size != component.Size) throw new InvalidDataException("UPDATE_SIZE_MISMATCH");
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(component.Sha256.ToLowerInvariant())))
            throw new InvalidDataException("UPDATE_HASH_MISMATCH");
    }

    public static string PublicKeyFingerprint(ECDsa key) =>
        Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
}
