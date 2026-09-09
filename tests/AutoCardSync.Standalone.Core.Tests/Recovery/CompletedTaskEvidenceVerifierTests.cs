using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class CompletedTaskEvidenceVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-CompletedEvidence", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(StandaloneTargetMode.LocalOnly)]
    [InlineData(StandaloneTargetMode.NasOnly)]
    [InlineData(StandaloneTargetMode.LocalAndNas)]
    public async Task Matching_evidence_acquires_lease_for_every_required_object(
        StandaloneTargetMode mode)
    {
        Fixture fixture = await CreateFixtureAsync(mode);

        await using FinalPublishedObjectLease lease = await AcquireAsync(fixture);

        foreach (FixtureFile file in fixture.Files)
        {
            AssertWriteBlocked(file.SourcePath);
            if (mode.RequiresLocal())
                AssertWriteBlocked(file.LocalPath);
            if (mode.RequiresNas())
                AssertWriteBlocked(file.NasPath);
        }

        await lease.RevalidateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Same_length_source_content_change_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        FixtureFile file = fixture.Files[0];
        DateTime originalLastWriteUtc = File.GetLastWriteTimeUtc(file.SourcePath);
        await File.WriteAllBytesAsync(file.SourcePath, SameLengthReplacement(file.Content));
        File.SetLastWriteTimeUtc(file.SourcePath, originalLastWriteUtc);
        Assert.Equal(originalLastWriteUtc, File.GetLastWriteTimeUtc(file.SourcePath));

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_length_required_target_content_change_is_rejected(bool nasTarget)
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        FixtureFile file = fixture.Files[0];
        string path = nasTarget ? file.NasPath : file.LocalPath;
        await File.WriteAllBytesAsync(path, SameLengthReplacement(file.Content));

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_hash_replacement_object_is_rejected_by_identity(bool replaceSource)
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        FixtureFile file = fixture.Files[0];
        string path = replaceSource ? file.SourcePath : file.LocalPath;
        string originalIdentity = GetIdentity(path);
        string replacement = Path.Combine(_root, $"replacement-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(replacement, file.Content);
        File.Move(replacement, path, overwrite: true);
        Assert.NotEqual(originalIdentity, GetIdentity(path));

        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture));
    }

    [Fact]
    public async Task Deleted_published_object_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        File.Delete(fixture.Files[0].LocalPath);

        await Assert.ThrowsAnyAsync<IOException>(() => AcquireAsync(fixture));
    }

    [Fact]
    public async Task Offline_required_target_directory_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.NasOnly);
        Directory.Move(fixture.NasRoot, fixture.NasRoot + ".offline");

        await Assert.ThrowsAnyAsync<IOException>(() => AcquireAsync(fixture));
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_evidence_is_acquired()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CompletedTaskEvidenceVerifier.AcquireVerifiedLeaseAsync(
                fixture.SourceRoot,
                fixture.Candidate,
                fixture.Current,
                fixture.LocalRoot,
                fixture.NasRoot,
                cancellation.Token));
    }

    [Fact]
    public async Task Mismatched_receipt_is_rejected_without_changing_source_or_persisted_bytes()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        StandaloneCompletedTaskCandidate candidate = fixture.Candidate with
        {
            Receipt = fixture.Candidate.Receipt with { SafeToRemoveCard = false },
        };
        byte[] sourceBefore = await File.ReadAllBytesAsync(fixture.Files[0].SourcePath);
        string journalPath = await PersistAsync("task.json", candidate.Journal);
        string receiptPath = await PersistAsync("receipt.json", candidate.Receipt);
        byte[] journalBefore = await File.ReadAllBytesAsync(journalPath);
        byte[] receiptBefore = await File.ReadAllBytesAsync(receiptPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, candidate));

        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(fixture.Files[0].SourcePath));
        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(journalPath));
        Assert.Equal(receiptBefore, await File.ReadAllBytesAsync(receiptPath));
    }

    [Fact]
    public async Task Mismatched_current_identity_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        RecoveryIdentitySnapshot changed = fixture.Current with { SourceIdentity = "different-source" };

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, current: changed));
    }

    [Fact]
    public async Task Changed_source_endpoint_requires_explicit_same_card_rebinding_authorization()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        RecoveryIdentitySnapshot reboundCurrent = fixture.Current with
        {
            SourceIdentity = "source-identity-at-new-endpoint",
        };
        var authorization = new CompletedTaskSourceRebindingAuthorization(
            fixture.Candidate.Journal.CardInstanceId,
            reboundCurrent.SourceIdentity);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            current: reboundCurrent));

        await using FinalPublishedObjectLease lease = await AcquireAsync(
            fixture,
            current: reboundCurrent,
            sourceRebindingAuthorization: authorization);
        await lease.RevalidateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Rebinding_authorization_cannot_cross_card_identity()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        RecoveryIdentitySnapshot otherCard = fixture.Current with
        {
            CardInstanceId = Guid.NewGuid(),
            SourceIdentity = "source-identity-at-new-endpoint",
        };
        var authorization = new CompletedTaskSourceRebindingAuthorization(
            otherCard.CardInstanceId,
            otherCard.SourceIdentity);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            current: otherCard,
            sourceRebindingAuthorization: authorization));
    }

    [Fact]
    public async Task Rebinding_authorization_does_not_bypass_tampered_old_receipt()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        RecoveryIdentitySnapshot reboundCurrent = fixture.Current with
        {
            SourceIdentity = "source-identity-at-new-endpoint",
        };
        StandaloneCompletedTaskCandidate tampered = fixture.Candidate with
        {
            Receipt = fixture.Candidate.Receipt with { SafeToRemoveCard = false },
        };
        var authorization = new CompletedTaskSourceRebindingAuthorization(
            fixture.Candidate.Journal.CardInstanceId,
            reboundCurrent.SourceIdentity);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            candidate: tampered,
            current: reboundCurrent,
            sourceRebindingAuthorization: authorization));
    }

    [Fact]
    public async Task Returned_lease_blocks_writes_until_disposed()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        string sourcePath = fixture.Files[0].SourcePath;
        string targetPath = fixture.Files[0].LocalPath;

        await using (FinalPublishedObjectLease lease = await AcquireAsync(fixture))
        {
            AssertWriteBlocked(sourcePath);
            AssertWriteBlocked(targetPath);
        }

        using FileStream sourceWrite = File.Open(sourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        using FileStream targetWrite = File.Open(targetPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
    }

    [Fact]
    public async Task Subset_verifies_only_selected_objects_and_preserves_source_and_persisted_bytes()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        FixtureFile included = fixture.Files[0];
        FixtureFile excluded = fixture.Files[1];
        File.Delete(excluded.SourcePath);
        File.Delete(excluded.LocalPath);
        File.Delete(excluded.NasPath);
        byte[] includedSourceBefore = await File.ReadAllBytesAsync(included.SourcePath);
        string journalPath = await PersistAsync("subset-task.json", fixture.Candidate.Journal);
        string receiptPath = await PersistAsync("subset-receipt.json", fixture.Candidate.Receipt);
        byte[] journalBefore = await File.ReadAllBytesAsync(journalPath);
        byte[] receiptBefore = await File.ReadAllBytesAsync(receiptPath);
        IReadOnlySet<string> subset = new HashSet<string>(StringComparer.Ordinal)
        {
            included.RelativePath,
        };

        await using FinalPublishedObjectLease lease = await AcquireAsync(
            fixture,
            includedRelativePaths: subset);

        AssertWriteBlocked(included.SourcePath);
        AssertWriteBlocked(included.LocalPath);
        AssertWriteBlocked(included.NasPath);
        Assert.Equal(includedSourceBefore, await File.ReadAllBytesAsync(included.SourcePath));
        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(journalPath));
        Assert.Equal(receiptBefore, await File.ReadAllBytesAsync(receiptPath));
    }

    [Fact]
    public async Task Empty_subset_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            includedRelativePaths: new HashSet<string>()));
    }

    [Fact]
    public async Task Unknown_subset_path_is_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        IReadOnlySet<string> subset = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("DCIM", "unknown.bin"),
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            includedRelativePaths: subset));
    }

    [Fact]
    public async Task Case_only_duplicate_subset_paths_are_rejected()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalOnly);
        string relativePath = fixture.Files[0].RelativePath;
        IReadOnlySet<string> subset = new HashSet<string>(StringComparer.Ordinal)
        {
            relativePath,
            relativePath.ToUpperInvariant(),
        };
        Assert.Equal(2, subset.Count);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            includedRelativePaths: subset));
    }

    [Fact]
    public async Task Tampered_receipt_for_excluded_file_is_rejected_before_subset_projection()
    {
        Fixture fixture = await CreateFixtureAsync(StandaloneTargetMode.LocalAndNas);
        StandaloneCompletionFileFact[] facts = fixture.Candidate.Receipt.Files.ToArray();
        facts[1] = facts[1] with { SourceSha256 = new string('0', 64) };
        StandaloneCompletedTaskCandidate candidate = fixture.Candidate with
        {
            Receipt = fixture.Candidate.Receipt with { Files = facts },
        };
        IReadOnlySet<string> subset = new HashSet<string>(StringComparer.Ordinal)
        {
            fixture.Files[0].RelativePath,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(
            fixture,
            candidate,
            includedRelativePaths: subset));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<Fixture> CreateFixtureAsync(StandaloneTargetMode mode)
    {
        string sourceRoot = Path.Combine(_root, "source");
        string localRoot = Path.Combine(_root, "local");
        string nasRoot = Path.Combine(_root, "nas");
        string[] relativePaths =
        [
            Path.Combine("DCIM", "clip-one.bin"),
            Path.Combine("PRIVATE", "clip-two.bin"),
        ];
        byte[][] contents =
        [
            Encoding.UTF8.GetBytes("verified-source-one"),
            Encoding.UTF8.GetBytes("verified-source-two"),
        ];
        var fixtureFiles = new List<FixtureFile>(relativePaths.Length);
        var journalFiles = new List<StandaloneFileJournal>(relativePaths.Length);
        var receiptFiles = new List<StandaloneCompletionFileFact>(relativePaths.Length);

        for (int index = 0; index < relativePaths.Length; index++)
        {
            string relativePath = relativePaths[index];
            byte[] content = contents[index];
            string sourcePath = Path.Combine(sourceRoot, relativePath);
            string localPath = Path.Combine(localRoot, relativePath);
            string nasPath = Path.Combine(nasRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(nasPath)!);
            await File.WriteAllBytesAsync(sourcePath, content);
            await File.WriteAllBytesAsync(localPath, content);
            await File.WriteAllBytesAsync(nasPath, content);

            string hash = ComputeSha256(content);
            Guid fileId = Guid.NewGuid();
            StandaloneTargetFileJournal localTarget = CreateTarget(
                "local", localPath, GetIdentity(localPath), hash, mode.RequiresLocal());
            StandaloneTargetFileJournal nasTarget = CreateTarget(
                "nas", nasPath, GetIdentity(nasPath), hash, mode.RequiresNas());
            journalFiles.Add(new StandaloneFileJournal
            {
                FileId = fileId,
                RelativePath = relativePath,
                Length = content.LongLength,
                SourceSha256 = hash,
                SourceFileIdentity = GetIdentity(sourcePath),
                State = StandaloneFileState.Verified,
                LocalTarget = localTarget,
                NasTarget = nasTarget,
            });
            receiptFiles.Add(new StandaloneCompletionFileFact
            {
                FileId = fileId,
                RelativePath = relativePath,
                Length = content.LongLength,
                SourceSha256 = hash,
                LocalFinalObjectId = mode.RequiresLocal() ? localTarget.FinalObjectIdentity! : string.Empty,
                LocalFinalSha256 = mode.RequiresLocal() ? hash : string.Empty,
                NasFinalObjectId = mode.RequiresNas() ? nasTarget.FinalObjectIdentity! : string.Empty,
                NasFinalSha256 = mode.RequiresNas() ? hash : string.Empty,
            });
            fixtureFiles.Add(new(relativePath, content, sourcePath, localPath, nasPath));
        }

        Guid taskId = Guid.NewGuid();
        Guid cardInstanceId = Guid.NewGuid();
        var journal = new StandaloneTaskJournal
        {
            TaskId = taskId,
            SourceIdentity = "source-identity",
            CardInstanceId = cardInstanceId,
            TargetMode = mode,
            ManifestHash = "manifest-hash",
            LocalTargetIdentity = mode.RequiresLocal() ? "local-target-identity" : string.Empty,
            LocalTargetRoot = mode.RequiresLocal() ? localRoot : string.Empty,
            NasTargetIdentity = mode.RequiresNas() ? "nas-target-identity" : string.Empty,
            NasTargetRoot = mode.RequiresNas() ? nasRoot : string.Empty,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LocalCompletionReceiptPersisted = true,
            Files = journalFiles,
        };
        var receipt = new StandaloneCompletionReceipt
        {
            TaskId = taskId,
            CardInstanceId = cardInstanceId,
            TargetMode = mode,
            ManifestHash = journal.ManifestHash,
            SourceIdentity = journal.SourceIdentity,
            LocalTargetIdentity = journal.LocalTargetIdentity,
            NasTargetIdentity = journal.NasTargetIdentity,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            SafeToRemoveCard = true,
            Files = receiptFiles,
        };
        var current = new RecoveryIdentitySnapshot(
            journal.SourceIdentity,
            journal.CardInstanceId,
            journal.ManifestHash,
            journal.LocalTargetIdentity,
            journal.NasTargetIdentity,
            journal.TargetMode,
            journal.InventoryManifestHash);
        return new(
            sourceRoot,
            localRoot,
            nasRoot,
            fixtureFiles,
            new StandaloneCompletedTaskCandidate(journal, receipt),
            current);
    }

    private static StandaloneTargetFileJournal CreateTarget(
        string role,
        string finalPath,
        string identity,
        string hash,
        bool required) => new()
        {
            TargetId = Guid.NewGuid(),
            TargetRole = role,
            TargetIdentity = required ? $"{role}-target-identity" : string.Empty,
            TemporaryPath = required ? finalPath + ".partial" : string.Empty,
            FinalPath = required ? finalPath : null,
            FinalObjectIdentity = required ? identity : null,
            ExpectedSha256 = required ? hash : null,
            FinalSha256 = required ? hash : null,
            State = required ? StandaloneTargetState.Verified : StandaloneTargetState.NotRequired,
            AtomicallyPublished = required,
            FullRereadSha256Passed = required,
        };

    private Task<FinalPublishedObjectLease> AcquireAsync(
        Fixture fixture,
        StandaloneCompletedTaskCandidate? candidate = null,
        RecoveryIdentitySnapshot? current = null,
        IReadOnlySet<string>? includedRelativePaths = null,
        CompletedTaskSourceRebindingAuthorization? sourceRebindingAuthorization = null) =>
        CompletedTaskEvidenceVerifier.AcquireVerifiedLeaseAsync(
            fixture.SourceRoot,
            candidate ?? fixture.Candidate,
            current ?? fixture.Current,
            fixture.LocalRoot,
            fixture.NasRoot,
            CancellationToken.None,
            includedRelativePaths,
            sourceRebindingAuthorization);

    private async Task<string> PersistAsync<T>(string fileName, T value)
    {
        string path = Path.Combine(_root, "persisted", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value));
        return path;
    }

    private static void AssertWriteBlocked(string path)
    {
        Assert.Throws<IOException>(() =>
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
    }

    private static byte[] SameLengthReplacement(byte[] original)
    {
        byte[] replacement = original.ToArray();
        replacement[0] ^= 0x5A;
        return replacement;
    }

    private static string GetIdentity(string path)
    {
        FileIdentity identity = FileIdentity.GetFileIdentity(path);
        return $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content));

    private sealed record Fixture(
        string SourceRoot,
        string LocalRoot,
        string NasRoot,
        IReadOnlyList<FixtureFile> Files,
        StandaloneCompletedTaskCandidate Candidate,
        RecoveryIdentitySnapshot Current);

    private sealed record FixtureFile(
        string RelativePath,
        byte[] Content,
        string SourcePath,
        string LocalPath,
        string NasPath);
}
