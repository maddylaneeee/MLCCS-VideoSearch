using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MLCCS.VideoSearch.Core.Contracts;
using MLCCS.VideoSearch.Core.Updates;

try
{
    return args switch
    {
        ["generate", var encryptedPrivateKeyPath, var publicKeyPath] =>
            Generate(encryptedPrivateKeyPath, publicKeyPath),
        ["fingerprint", var encryptedPrivateKeyPath] => Fingerprint(encryptedPrivateKeyPath),
        ["backup", var encryptedPrivateKeyPath, var encryptedBackupPath] =>
            Backup(encryptedPrivateKeyPath, encryptedBackupPath),
        ["sign", var unsignedPath, var encryptedPrivateKeyPath, var outputPath] =>
            await SignAsync(unsignedPath, encryptedPrivateKeyPath, outputPath),
        _ => Usage()
    };
}
catch (Exception error)
{
    Console.Error.WriteLine($"ERROR: {error.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Commands:\n  generate <encrypted-private.pem> <public.pem>\n  fingerprint <encrypted-private.pem>\n  backup <encrypted-private.pem> <encrypted-backup.pem.enc>\n  sign <unsigned-manifest.json> <encrypted-private.pem> <output.json>");
    return 2;
}

static int Generate(string privatePath, string publicPath)
{
    if (File.Exists(privatePath) || File.Exists(publicPath))
        throw new IOException("Refusing to overwrite an existing key file.");
    var password = ReadSecret("New production-key password: ");
    var confirmation = ReadSecret("Confirm production-key password: ");
    try
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(confirmation)))
            throw new CryptographicException("Password confirmation does not match.");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteEncryptedKey(privatePath, key, password, overwrite: false);
        WriteAtomic(publicPath, key.ExportSubjectPublicKeyInfoPem(), overwrite: false);
        Console.WriteLine($"Generated P-256 production key; public fingerprint {UpdateVerifier.PublicKeyFingerprint(key)}");
        return 0;
    }
    finally { Forget(password, confirmation); }
}

static int Fingerprint(string privatePath)
{
    var password = ReadSecret("Production-key password: ");
    try
    {
        using var key = Load(privatePath, password);
        Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Public fingerprint {UpdateVerifier.PublicKeyFingerprint(key)}");
        return 0;
    }
    finally { Forget(password); }
}

static int Backup(string privatePath, string backupPath)
{
    if (File.Exists(backupPath)) throw new IOException("Refusing to overwrite an existing backup.");
    var sourcePassword = ReadSecret("Production-key password: ");
    var backupPassword = ReadSecret("Independent backup password: ");
    var confirmation = ReadSecret("Confirm backup password: ");
    try
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(backupPassword), Encoding.UTF8.GetBytes(confirmation)))
            throw new CryptographicException("Backup password confirmation does not match.");
        using var key = Load(privatePath, sourcePassword);
        WriteEncryptedKey(backupPath, key, backupPassword, overwrite: false);
        using var verified = Load(backupPath, backupPassword);
        var sourceFingerprint = UpdateVerifier.PublicKeyFingerprint(key);
        var backupFingerprint = UpdateVerifier.PublicKeyFingerprint(verified);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sourceFingerprint), Encoding.ASCII.GetBytes(backupFingerprint)))
            throw new CryptographicException("Backup decryption fingerprint mismatch.");
        Console.WriteLine($"Backup decrypted and verified; public fingerprint {sourceFingerprint}");
        return 0;
    }
    finally { Forget(sourcePassword, backupPassword, confirmation); }
}

static async Task<int> SignAsync(string unsignedPath, string privatePath, string outputPath)
{
    var password = ReadSecret("Production-key password: ");
    try
    {
        var options = new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = true };
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(unsignedPath), options)
                       ?? throw new InvalidDataException("Manifest is empty.");
        if (!string.IsNullOrEmpty(manifest.ManifestSignature))
            throw new InvalidDataException("Input manifest must not already contain a signature.");
        if (manifest.ProductVersion != "1.0.0" || manifest.KeyId != "manifest-v1")
            throw new InvalidDataException("Production signing is restricted to v1.0.0 and key manifest-v1.");
        if (manifest.Components.Count == 0 || manifest.Components.Any(component =>
                component.Url.Scheme != Uri.UriSchemeHttps || component.Size <= 0 ||
                component.Sha256.Length != 64 || component.Files.Count == 0))
            throw new InvalidDataException("Manifest component metadata is incomplete.");
        using var key = Load(privatePath, password);
        var signature = key.SignData(UpdateVerifier.CanonicalBytes(manifest), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signed = manifest with { ManifestSignature = Convert.ToBase64String(signature) };
        if (!UpdateVerifier.VerifyManifest(signed, key))
            throw new CryptographicException("Self-verification of the signed manifest failed.");
        WriteAtomic(outputPath, JsonSerializer.Serialize(signed, options) + Environment.NewLine, overwrite: true);
        Console.WriteLine($"Signed {signed.ProductVersion}; key fingerprint {UpdateVerifier.PublicKeyFingerprint(key)}");
        return 0;
    }
    finally { Forget(password); }
}

static ECDsa Load(string path, string password)
{
    var key = ECDsa.Create();
    key.ImportFromEncryptedPem(File.ReadAllText(path), password);
    if (key.KeySize != 256) { key.Dispose(); throw new CryptographicException("Manifest key must be P-256."); }
    return key;
}

static void WriteEncryptedKey(string path, ECDsa key, string password, bool overwrite)
{
    var encrypted = key.ExportEncryptedPkcs8PrivateKey(password,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
    try { WriteAtomic(path, PemEncoding.WriteString("ENCRYPTED PRIVATE KEY", encrypted), overwrite); }
    finally { CryptographicOperations.ZeroMemory(encrypted); }
}

static void WriteAtomic(string path, string value, bool overwrite)
{
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    var temporary = full + $".{Guid.NewGuid():N}.tmp";
    File.WriteAllText(temporary, value, new UTF8Encoding(false));
    try { File.Move(temporary, full, overwrite); }
    catch { File.Delete(temporary); throw; }
}

static string ReadSecret(string prompt)
{
    Console.Error.Write(prompt);
    if (Console.IsInputRedirected) return Console.ReadLine() ?? throw new EndOfStreamException();
    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return value.ToString(); }
        if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
        else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
}

static void Forget(params string[] values)
{
    foreach (var value in values)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        CryptographicOperations.ZeroMemory(bytes);
    }
}
