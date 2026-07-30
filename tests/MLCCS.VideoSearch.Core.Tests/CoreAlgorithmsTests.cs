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
    public void Long_scene_is_split_into_bounded_windows_with_motion_frame()
    {
        var segments = SceneSegmenter.Segment(20_000, [new SceneSample(6_000, 5, 40)]);
        Assert.Equal([(0L, 8_000L), (8_000L, 16_000L), (16_000L, 20_000L)], segments.Select(x => (x.StartMs, x.EndMs)));
        Assert.Equal([4_000L, 6_000L], segments[0].RepresentativeMs);
        Assert.All(segments, segment => Assert.InRange(segment.EndMs - segment.StartMs, 2_000, 8_000));
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
    public void Expired_running_job_recovers_and_retry_is_bounded()
    {
        var now = DateTimeOffset.UtcNow;
        var job = new DurableJobState(Guid.NewGuid(), JobStatus.Running, 2, 3, "speech", .5, "old", now.AddMinutes(-1), null);
        var recovered = job.Recover(now);
        Assert.Equal(JobStatus.Queued, recovered.Status);
        var lastAttempt = recovered.Lease("new", now, TimeSpan.FromMinutes(5));
        Assert.Equal(3, lastAttempt.Attempt);
        Assert.Equal(JobStatus.FailedAwaitingConfirmation, (lastAttempt with { Status = JobStatus.Queued }).Lease("again", now, TimeSpan.FromMinutes(5)).Status);
    }

    [Fact]
    public void Old_settings_migrate_with_privacy_defaults()
    {
        var settings = SettingsMigrator.Migrate("{}", "C:\\Data");
        Assert.Equal(1, settings.SchemaVersion);
        Assert.True(settings.Privacy.HelpImprove);
        Assert.False(settings.Privacy.FullLogUploadConsent);
        Assert.True(settings.Indexing.Visual);
        Assert.False(settings.Indexing.Speech);
    }

    [Fact]
    public async Task Update_manifest_and_archive_require_valid_signatures()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var archive = Encoding.UTF8.GetBytes("portable update");
        var unsigned = new UpdateManifest(1, "0.2.0", "0.1.0", new Uri("https://lixinchen.ca/update.zip"), archive.Length,
            Convert.ToHexStringLower(SHA256.HashData(archive)), Convert.ToBase64String(key.SignData(archive, HashAlgorithmName.SHA256)),
            DateTimeOffset.Parse("2026-07-28T00:00:00Z"), "notes", false, "");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            unsigned.ProtocolVersion, unsigned.AppVersion, unsigned.MinimumCompatibleVersion,
            downloadUrl = unsigned.DownloadUrl.ToString(), unsigned.Size, unsigned.Sha256,
            unsigned.ArchiveSignature, unsigned.PublishedUtc, unsigned.ReleaseNotes, unsigned.Mandatory
        }, JsonDefaults.Options);
        var manifest = unsigned with { ManifestSignature = Convert.ToBase64String(key.SignData(canonical, HashAlgorithmName.SHA256)) };
        Assert.True(UpdateVerifier.VerifyManifest(manifest, key));
        await UpdateVerifier.VerifyArchiveAsync(new MemoryStream(archive), manifest, key, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateVerifier.VerifyArchiveAsync(new MemoryStream(Encoding.UTF8.GetBytes("tampered")), manifest, key, CancellationToken.None));
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
                command.CommandText = "SELECT count(*) FROM schema_migrations WHERE version=1";
                Assert.Equal(1L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bundled_acceptance_update_verifies_with_embedded_public_key()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "update-sample");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        using var key = EmbeddedUpdateKey.Create();
        Assert.True(UpdateVerifier.VerifyManifest(manifest, key));
        await using var archive = File.OpenRead(Path.Combine(directory, "sample-update.zip"));
        await UpdateVerifier.VerifyArchiveAsync(archive, manifest, key, CancellationToken.None);
    }
}
