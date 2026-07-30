using System.Security.Cryptography;
using System.Text.Json;

if (args is not [var archivePath, var privateKeyPath, var version, var minimumVersion, var downloadUrl, var publishedUtc, var notesPath, var outputPath, var mandatoryText])
{
    Console.Error.WriteLine("Usage: SigningTool <zip> <external-private.pem> <version> <minimum-version> <https-url> <published-utc> <notes-file> <output-json> <true|false>");
    return 2;
}
if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Download URL must use HTTPS.");
var archive = await File.ReadAllBytesAsync(archivePath);
using var key = ECDsa.Create(); key.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
var archiveSignature = Convert.ToBase64String(key.SignData(archive, HashAlgorithmName.SHA256));
var values = new
{
    protocolVersion = 1,
    appVersion = version,
    minimumCompatibleVersion = minimumVersion,
    downloadUrl,
    size = archive.LongLength,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(archive)),
    archiveSignature,
    publishedUtc = DateTimeOffset.Parse(publishedUtc),
    releaseNotes = await File.ReadAllTextAsync(notesPath),
    mandatory = bool.Parse(mandatoryText)
};
var canonical = JsonSerializer.SerializeToUtf8Bytes(values);
var document = new
{
    values.protocolVersion, values.appVersion, values.minimumCompatibleVersion, values.downloadUrl,
    values.size, values.sha256, values.archiveSignature, values.publishedUtc, values.releaseNotes,
    values.mandatory, manifestSignature = Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256))
};
var temporary = outputPath + ".tmp";
await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
File.Move(temporary, outputPath, true);
return 0;

