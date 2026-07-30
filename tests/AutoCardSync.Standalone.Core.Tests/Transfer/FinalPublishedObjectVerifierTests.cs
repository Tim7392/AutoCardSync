using System.Security.Cryptography;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Transfer;

public sealed class FinalPublishedObjectVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-FinalVerifier", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Acquire_holds_source_and_both_verified_targets_through_revalidation()
    {
        byte[] content = Encoding.UTF8.GetBytes("verified-source-local-and-nas-content");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);

        await using FinalPublishedObjectLease lease =
            await FinalPublishedObjectVerifier.AcquireVerifiedLeaseAsync(
                fixture.SourceRoot, journal, CancellationToken.None);

        AssertWriteBlocked(fixture.SourcePath);
        AssertWriteBlocked(fixture.LocalPath);
        AssertWriteBlocked(fixture.NasPath);

        string replacement = Path.Combine(_root, "replacement.bin");
        await File.WriteAllBytesAsync(replacement, content);
        Exception? replacementFailure = Record.Exception(
            () => File.Move(replacement, fixture.LocalPath, overwrite: true));
        Assert.True(
            replacementFailure is IOException or UnauthorizedAccessException,
            $"Expected the verified lease to block replacement, got {replacementFailure?.GetType().FullName ?? "no exception"}.");

        await lease.RevalidateAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Same_length_target_content_tampering_is_rejected()
    {
        byte[] original = Encoding.UTF8.GetBytes("ORIGINAL");
        Fixture fixture = await CreateFixtureAsync(original);
        StandaloneTaskJournal journal = CreateJournal(fixture, original);
        await File.WriteAllBytesAsync(fixture.LocalPath, Encoding.UTF8.GetBytes("TAMPERED"));

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Target_replacement_with_same_length_and_hash_is_rejected_by_identity()
    {
        byte[] content = Encoding.UTF8.GetBytes("same-content-new-target-object");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        string originalIdentity = GetIdentity(fixture.LocalPath);

        string replacement = Path.Combine(_root, "target-identity-replacement.bin");
        await File.WriteAllBytesAsync(replacement, content);
        File.Move(replacement, fixture.LocalPath, overwrite: true);
        Assert.NotEqual(originalIdentity, GetIdentity(fixture.LocalPath));

        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Target_length_change_is_rejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("expected-target-length");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        await File.AppendAllTextAsync(fixture.LocalPath, "-extra");

        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Empty_final_path_is_rejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("empty-path");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        StandaloneFileJournal file = Assert.Single(journal.Files);
        journal = journal with
        {
            Files =
            [
                file with
                {
                    LocalTarget = file.LocalTarget with { FinalPath = " " },
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Missing_final_object_is_rejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("missing-target-object");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        File.Delete(fixture.LocalPath);

        await Assert.ThrowsAnyAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Tampered_source_or_final_hash_fact_is_rejected(bool tamperSourceFact)
    {
        byte[] content = Encoding.UTF8.GetBytes("hash-fact");
        Fixture fixture = await CreateFixtureAsync(content);
        string actualHash = ComputeSha256(content);
        string falseHash = new('0', 64);
        StandaloneTaskJournal journal = CreateJournal(
            fixture,
            content,
            sourceSha256: tamperSourceFact ? falseHash : actualHash,
            localFinalSha256: tamperSourceFact ? actualHash : falseHash);

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Target_reparse_point_is_rejected_when_platform_allows_creation()
    {
        byte[] content = Encoding.UTF8.GetBytes("target-reparse");
        Fixture fixture = await CreateFixtureAsync(content);
        string realLocal = Path.Combine(_root, "real-local.bin");
        await File.WriteAllBytesAsync(realLocal, content);
        File.Delete(fixture.LocalPath);
        if (!TryCreateSymbolicLink(fixture.LocalPath, realLocal))
            return;

        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Same_length_source_content_change_is_rejected()
    {
        byte[] original = Encoding.UTF8.GetBytes("SOURCE-A");
        Fixture fixture = await CreateFixtureAsync(original);
        StandaloneTaskJournal journal = CreateJournal(fixture, original);
        await File.WriteAllBytesAsync(fixture.SourcePath, Encoding.UTF8.GetBytes("SOURCE-B"));

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Source_replacement_with_same_length_and_hash_is_rejected_by_identity()
    {
        byte[] content = Encoding.UTF8.GetBytes("same-content-new-source-object");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        string originalIdentity = GetIdentity(fixture.SourcePath);

        string replacement = Path.Combine(_root, "source-identity-replacement.bin");
        await File.WriteAllBytesAsync(replacement, content);
        File.Move(replacement, fixture.SourcePath, overwrite: true);
        Assert.NotEqual(originalIdentity, GetIdentity(fixture.SourcePath));

        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Source_length_change_is_rejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("expected-source-length");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        await File.AppendAllTextAsync(fixture.SourcePath, "-extra");

        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Missing_source_object_is_rejected()
    {
        byte[] content = Encoding.UTF8.GetBytes("missing-source-object");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        File.Delete(fixture.SourcePath);

        await Assert.ThrowsAnyAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Source_reparse_point_is_rejected_when_platform_allows_creation()
    {
        byte[] content = Encoding.UTF8.GetBytes("source-reparse");
        Fixture fixture = await CreateFixtureAsync(content);
        string realSource = Path.Combine(_root, "real-source.bin");
        await File.WriteAllBytesAsync(realSource, content);
        File.Delete(fixture.SourcePath);
        if (!TryCreateSymbolicLink(fixture.SourcePath, realSource))
            return;

        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        await Assert.ThrowsAsync<IOException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Final_object_outside_frozen_target_root_is_rejected_even_when_content_and_identity_match()
    {
        byte[] content = Encoding.UTF8.GetBytes("outside-frozen-root");
        Fixture fixture = await CreateFixtureAsync(content);
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        string outsidePath = Path.Combine(_root, "outside", "DCIM", "clip.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        await File.WriteAllBytesAsync(outsidePath, content);
        StandaloneFileJournal file = Assert.Single(journal.Files);
        journal = journal with
        {
            Files =
            [
                file with
                {
                    LocalTarget = file.LocalTarget with
                    {
                        FinalPath = outsidePath,
                        FinalObjectIdentity = GetIdentity(outsidePath),
                    },
                },
            ],
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => AcquireAsync(fixture, journal));
    }

    [Fact]
    public async Task Schema_two_local_only_revalidates_canonical_storage_identity()
    {
        byte[] content = Encoding.UTF8.GetBytes("schema-two-local-identity");
        Fixture fixture = await CreateFixtureAsync(content);
        string identity = new FaultDomainResolver().ResolveLocalDomain(fixture.LocalRoot).StorageIdentity!;
        StandaloneTaskJournal journal = CreateJournal(fixture, content);
        StandaloneFileJournal file = Assert.Single(journal.Files);
        journal = journal with
        {
            SchemaVersion = 2,
            TargetMode = StandaloneTargetMode.LocalOnly,
            LocalTargetIdentity = identity,
            NasTargetIdentity = string.Empty,
            NasTargetRoot = string.Empty,
            Files =
            [
                file with
                {
                    LocalTarget = file.LocalTarget with { TargetIdentity = identity },
                    NasTarget = file.NasTarget with
                    {
                        TargetIdentity = string.Empty,
                        TemporaryPath = string.Empty,
                        FinalPath = null,
                        FinalObjectIdentity = null,
                        FinalSha256 = null,
                        State = StandaloneTargetState.NotRequired,
                    },
                },
            ],
        };

        await using FinalPublishedObjectLease lease = await AcquireAsync(fixture, journal);
        await lease.RevalidateAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<Fixture> CreateFixtureAsync(byte[] content)
    {
        string sourceRoot = Path.Combine(_root, "source");
        string sourcePath = Path.Combine(sourceRoot, "DCIM", "clip.bin");
        string localRoot = Path.Combine(_root, "local");
        string nasRoot = Path.Combine(_root, "nas");
        string localPath = Path.Combine(localRoot, "DCIM", "clip.bin");
        string nasPath = Path.Combine(nasRoot, "DCIM", "clip.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nasPath)!);
        await File.WriteAllBytesAsync(sourcePath, content);
        await File.WriteAllBytesAsync(localPath, content);
        await File.WriteAllBytesAsync(nasPath, content);
        return new Fixture(sourceRoot, sourcePath, localRoot, localPath, nasRoot, nasPath);
    }

    private static Task<FinalPublishedObjectLease> AcquireAsync(
        Fixture fixture,
        StandaloneTaskJournal journal) =>
        FinalPublishedObjectVerifier.AcquireVerifiedLeaseAsync(
            fixture.SourceRoot, journal, CancellationToken.None);

    private static StandaloneTaskJournal CreateJournal(
        Fixture fixture,
        byte[] content,
        string? sourceSha256 = null,
        string? localFinalSha256 = null)
    {
        string actualHash = ComputeSha256(content);
        sourceSha256 ??= actualHash;
        localFinalSha256 ??= actualHash;

        return new StandaloneTaskJournal
        {
            TaskId = Guid.NewGuid(),
            SourceIdentity = "source-identity",
            CardInstanceId = Guid.NewGuid(),
            ManifestHash = "manifest-hash",
            LocalTargetIdentity = "local-target-identity",
            LocalTargetRoot = fixture.LocalRoot,
            NasTargetIdentity = "nas-target-identity",
            NasTargetRoot = fixture.NasRoot,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = Guid.NewGuid(),
                    RelativePath = Path.Combine("DCIM", "clip.bin"),
                    Length = content.LongLength,
                    SourceSha256 = sourceSha256,
                    SourceFileIdentity = GetIdentity(fixture.SourcePath),
                    State = StandaloneFileState.Verified,
                    LocalTarget = CreateTarget(
                        "local", fixture.LocalPath, GetIdentity(fixture.LocalPath), localFinalSha256),
                    NasTarget = CreateTarget(
                        "nas", fixture.NasPath, GetIdentity(fixture.NasPath), actualHash),
                },
            ],
        };
    }

    private static StandaloneTargetFileJournal CreateTarget(
        string role,
        string finalPath,
        string identity,
        string finalSha256) => new()
        {
            TargetId = Guid.NewGuid(),
            TargetRole = role,
            TargetIdentity = $"{role}-target-identity",
            TemporaryPath = finalPath + ".partial",
            FinalPath = finalPath,
            FinalObjectIdentity = identity,
            ExpectedSha256 = finalSha256,
            FinalSha256 = finalSha256,
            State = StandaloneTargetState.Verified,
            AtomicallyPublished = true,
            FullRereadSha256Passed = true,
        };

    private static void AssertWriteBlocked(string path)
    {
        Assert.Throws<IOException>(() =>
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        });
    }

    private static bool TryCreateSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
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
        string SourcePath,
        string LocalRoot,
        string LocalPath,
        string NasRoot,
        string NasPath);
}
