using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MLCCS.VideoSearch.Core.Contracts;
using MLCCS.VideoSearch.Core.Indexing;
using MLCCS.VideoSearch.Core.Privacy;
using MLCCS.VideoSearch.Core.Search;
using MLCCS.VideoSearch.Core.Storage;
using MLCCS.VideoSearch.Core.Updates;
using Xunit;

namespace MLCCS.VideoSearch.Core.Tests;

public sealed class CoreAlgorithmsTests
{
    [Fact]
    public void Embedded_production_manifest_key_has_expected_fingerprint()
    {
        using var key = EmbeddedUpdateKey.Create();
        Assert.Equal("65079f3d3d648643e409dfbcf2fbec2b29f8a4280f495f0ce76c547e143773b7",
            UpdateVerifier.PublicKeyFingerprint(key));
    }

    [Fact]
    public void Whisper_windows_preserve_original_segments_and_map_hit()
    {
        var first = new WhisperSegment(Guid.NewGuid(), 0, 5_000, "你好", []);
        var second = new WhisperSegment(Guid.NewGuid(), 5_100, 11_000, "世界", []);
        var windows = SpeechWindowMerger.Merge([first, second]);
        Assert.Single(windows);
        Assert.Equal([first.Id, second.Id], windows[0].OriginalSegmentIds);
        Assert.Equal(second.Id, SpeechWindowMerger.MostRelevantOriginal(windows[0], [first, second], 9_000).Id);
    }

    [Fact]
    public void Exact_and_normal_sources_outrank_phonetic_only()
    {
        var fused = RrfRanker.Fuse([
            new RecallHit("exact", "filename", 2, 0.9, Exact: true),
            new RecallHit("semantic", "visual", 1, 0.9),
            new RecallHit("phonetic", "speech", 1, 0.9, Phonetic: true)
        ]);
        Assert.Equal("exact", fused[0].ResultId);
        Assert.Equal("phonetic", fused[^1].ResultId);
        Assert.True(fused[0].Contributions[0].Value > fused[^1].Contributions[0].Value);
    }

    [Fact]
    public void Phonetic_expansion_is_capped_and_fuzzy()
    {
        var corpus = Enumerable.Range(0, 12).Select(i => new PhoneticTerm($"李新晨{i}", "", $"lixinchen{i}", "lxc", $"nixincen{i}"));
        var candidates = PhoneticCandidates.Expand("lixinchen", corpus, maximum: 8, tolerance: 1);
        Assert.Equal(8, candidates.Count);
    }

    [Fact]
    public void Diagnostics_remove_paths_secrets_queries_and_transcripts()
    {
        var input = "C:\\Users\\Alice\\Videos\\private.mp4 authorization: Bearer top.secret {\"query\":\"name\",\"transcript\":\"words\"}";
        var output = DiagnosticRedactor.Redact(input, "Alice");
        Assert.DoesNotContain("private.mp4", output);
        Assert.DoesNotContain("top.secret", output);
        Assert.DoesNotContain("name", output);
        Assert.DoesNotContain("words", output);
    }

    [Fact]
    public async Task Release_manifest_and_component_require_p1363_signature_size_and_hash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var archive = Encoding.UTF8.GetBytes("v1 component");
        var component = new ReleaseComponent("app", "App", "core", new Uri("https://lixinchen.ca/app.zip"),
            "current", true, true, archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)),
            [new ReleaseFile("ui/app.exe", archive.Length, Convert.ToHexStringLower(SHA256.HashData(archive)))]);
        var unsigned = new ReleaseManifest(1, "1.0.0", "1.0.0",
            new ReleaseRequirements("Windows 10 1809", 17763, "x64", "NVIDIA", 4L * 1024 * 1024 * 1024,
                "PyTorch 2.7.1+cu128", false), "current/ui/MLCCS.VideoSearch.UI.exe",
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"), "notes", false, [component], "manifest-v1", "");
        var manifest = unsigned with { ManifestSignature = Convert.ToBase64String(key.SignData(
            UpdateVerifier.CanonicalBytes(unsigned), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
        Assert.True(UpdateVerifier.VerifyManifest(manifest, key));
        Assert.False(UpdateVerifier.VerifyManifest(manifest with { ProductVersion = "1.0.1" }, key));
        Assert.False(UpdateVerifier.VerifyManifest(manifest with { KeyId = "other-key" }, key));
        Assert.False(UpdateVerifier.VerifyManifest(manifest with { ManifestSignature = Convert.ToBase64String(new byte[63]) }, key));
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateVerifier.VerifyManifest(manifest, wrongKey));
        await UpdateVerifier.VerifyArchiveAsync(new MemoryStream(archive), component, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.VerifyArchiveAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("tampered")), component, CancellationToken.None));
        var sameSizeReplacement = Enumerable.Repeat((byte)'x', archive.Length).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.VerifyArchiveAsync(
            new MemoryStream(sameSizeReplacement), component, CancellationToken.None));
    }

    [Fact]
    public async Task Database_migration_creates_fts_and_version()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mlccs-{Guid.NewGuid():N}.db");
        try
        {
            await new CatalogDatabase(path).InitializeAsync();
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name IN " +
                                      "('schema_migrations','media_assets','visual_segments','vector_outbox','search_fts')";
                Assert.Equal(5L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public void Canonical_manifest_is_independent_of_indented_output_json()
    {
        var manifest = new ReleaseManifest(1, "1.0.0", "1.0.0",
            new ReleaseRequirements("Windows 10 1809", 17763, "x64", "NVIDIA", 4L << 30,
                "PyTorch 2.7.1+cu128", false), "current/ui/MLCCS.VideoSearch.UI.exe",
            DateTimeOffset.Parse("2026-08-03T00:00:00Z"), "notes", false, [], "manifest-v1", "");
        var roundTrip = JsonSerializer.Deserialize<ReleaseManifest>(
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(UpdateVerifier.CanonicalBytes(manifest), UpdateVerifier.CanonicalBytes(roundTrip));
    }
}
