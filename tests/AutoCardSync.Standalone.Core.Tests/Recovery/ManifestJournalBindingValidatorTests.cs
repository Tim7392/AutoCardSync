using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class ManifestJournalBindingValidatorTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly Guid FileA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Validate_accepts_exact_non_excluded_set_with_windows_path_and_hash_normalization()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files =
            [
                journal.Files[1] with
                {
                    RelativePath = "SECOND/CLIP-B.MP4",
                    SourceSha256 = HashB.ToUpperInvariant(),
                },
                journal.Files[0] with
                {
                    RelativePath = "camera/clip-a.mxf",
                    SourceSha256 = HashA.ToUpperInvariant(),
                },
            ],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        Assert.True(result.IsValid);
        Assert.Empty(result.Reasons);
        ManifestJournalBindingValidator.EnsureValid(manifest, journal);
    }

    [Fact]
    public void Validate_rejects_unfrozen_manifest()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(Entry(FileA, @"Camera\Clip-A.mxf", 10, HashA));

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, CreateJournal(manifest));

        AssertReason(result, "manifest_not_frozen");
    }

    [Fact]
    public void Validate_rejects_task_and_manifest_hash_mismatch()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest) with
        {
            TaskId = Guid.NewGuid(),
            ManifestHash = HashC,
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, "task_id_mismatch");
        AssertReason(result, "manifest_hash_mismatch");
    }
    [Fact]
    public void Validate_rejects_duplicate_manifest_file_id()
    {
        TaskManifest manifest = CreateManifest(
            Entry(FileA, @"Camera\Clip-A.mxf", 10, HashA),
            Entry(FileA, @"Second\Clip-B.mp4", 20, HashB));

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, CreateJournal(manifest));

        AssertReason(result, "manifest_duplicate_file_id:");
    }

    [Fact]
    public void Validate_rejects_duplicate_journal_file_id()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files = [journal.Files[0], journal.Files[1] with { FileId = FileA }],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, "journal_duplicate_file_id:");
    }

    [Fact]
    public void Validate_rejects_duplicate_manifest_relative_path_after_normalization()
    {
        TaskManifest manifest = CreateManifest(
            Entry(FileA, @"Camera\Clip-A.mxf", 10, HashA),
            Entry(FileB, "camera/CLIP-A.MXF", 20, HashB));

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, CreateJournal(manifest));

        AssertReason(result, "manifest_duplicate_relative_path:");
    }

    [Fact]
    public void Validate_rejects_duplicate_journal_relative_path_after_normalization()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files = [journal.Files[0], journal.Files[1] with { RelativePath = "CAMERA/CLIP-A.MXF" }],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, "journal_duplicate_relative_path:");
    }

    [Fact]
    public void Validate_rejects_missing_journal_file()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest) with
        {
            Files = [CreateJournal(manifest).Files[0]],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, $"journal_file_missing:{FileB:N}");
    }

    [Fact]
    public void Validate_rejects_extra_journal_file()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files =
            [
                .. journal.Files,
                FileJournal(Guid.NewGuid(), @"Extra\Clip-C.mov", 30, HashC),
            ],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, "journal_file_extra:");
    }

    [Fact]
    public void Validate_rejects_file_id_tampering()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files = [journal.Files[0] with { FileId = Guid.NewGuid() }, journal.Files[1]],
        };

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, journal);

        AssertReason(result, $"journal_file_missing:{FileA:N}");
        AssertReason(result, "journal_file_extra:");
    }

    [Fact]
    public void Validate_rejects_relative_path_tampering()
    {
        ManifestJournalBindingValidationResult result = ValidateWithFirstFile(
            file => file with { RelativePath = @"Camera\Other.mxf" });

        AssertReason(result, $"relative_path_mismatch:{FileA:N}");
    }

    [Fact]
    public void Validate_rejects_length_tampering()
    {
        ManifestJournalBindingValidationResult result = ValidateWithFirstFile(
            file => file with { Length = file.Length + 1 });

        AssertReason(result, $"length_mismatch:{FileA:N}");
    }

    [Fact]
    public void Validate_rejects_source_hash_tampering()
    {
        ManifestJournalBindingValidationResult result = ValidateWithFirstFile(
            file => file with { SourceSha256 = HashC });

        AssertReason(result, $"source_sha256_mismatch:{FileA:N}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Validate_rejects_empty_or_invalid_journal_source_hash(string invalidHash)
    {
        ManifestJournalBindingValidationResult result = ValidateWithFirstFile(
            file => file with { SourceSha256 = invalidHash });

        AssertReason(result, $"journal_invalid_source_sha256:{FileA:N}");
    }

    [Fact]
    public void Validate_rejects_invalid_manifest_source_hash()
    {
        TaskManifest manifest = CreateManifest(
            Entry(FileA, @"Camera\Clip-A.mxf", 10, "not-a-sha256"),
            Entry(FileB, @"Second\Clip-B.mp4", 20, HashB));

        ManifestJournalBindingValidationResult result =
            ManifestJournalBindingValidator.Validate(manifest, CreateJournal(manifest));

        AssertReason(result, $"manifest_invalid_source_sha256:{FileA:N}");
    }

    [Fact]
    public void EnsureValid_throws_invalid_data_exception_with_reasons()
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest) with { Files = [] };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ManifestJournalBindingValidator.EnsureValid(manifest, journal));

        Assert.Contains("journal_file_missing", exception.Message, StringComparison.Ordinal);
    }

    private static ManifestJournalBindingValidationResult ValidateWithFirstFile(
        Func<StandaloneFileJournal, StandaloneFileJournal> mutate)
    {
        TaskManifest manifest = CreateManifest();
        StandaloneTaskJournal journal = CreateJournal(manifest);
        journal = journal with
        {
            Files = [mutate(journal.Files[0]), journal.Files[1]],
        };
        return ManifestJournalBindingValidator.Validate(manifest, journal);
    }

    private static TaskManifest CreateManifest(params ManifestEntry[] entries)
    {
        var manifest = new TaskManifest(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        ManifestEntry[] effectiveEntries = entries.Length == 0
            ?
            [
                Entry(FileA, @"Camera\Clip-A.mxf", 10, HashA),
                Entry(FileB, @"Second\Clip-B.mp4", 20, HashB),
                Entry(Guid.NewGuid(), @"Excluded\Thumb.tmp", 5, null, excluded: true),
            ]
            : entries;
        foreach (ManifestEntry entry in effectiveEntries)
            manifest.AddEntry(entry);
        manifest.Freeze();
        return manifest;
    }

    private static ManifestEntry Entry(
        Guid id,
        string relativePath,
        long length,
        string? sourceHash,
        bool excluded = false) => new()
        {
            Id = id,
            RelativePath = relativePath,
            FileSize = length,
            LastModifiedUtc = DateTimeOffset.Parse("2026-07-24T00:00:00Z"),
            SourceHash = sourceHash,
            Excluded = excluded,
            ExclusionRule = excluded ? ManifestExclusionRules.ExtensionNotIncluded : null,
        };

    private static StandaloneTaskJournal CreateJournal(TaskManifest manifest) => new()
    {
        TaskId = manifest.TaskId,
        SourceIdentity = "source",
        CardInstanceId = Guid.NewGuid(),
        ManifestHash = manifest.ManifestHash,
        LocalTargetIdentity = "local",
        LocalTargetRoot = @"D:\Local",
        NasTargetIdentity = "nas",
        NasTargetRoot = @"Y:\Nas",
        Files = manifest.Entries
            .Where(entry => !entry.Excluded)
            .Select(entry => FileJournal(
                entry.Id,
                entry.RelativePath,
                entry.FileSize,
                entry.SourceHash ?? string.Empty))
            .ToArray(),
        UpdatedAtUtc = DateTimeOffset.Parse("2026-07-24T00:00:00Z"),
    };

    private static StandaloneFileJournal FileJournal(
        Guid fileId,
        string relativePath,
        long length,
        string hash) => new()
        {
            FileId = fileId,
            RelativePath = relativePath,
            Length = length,
            SourceSha256 = hash,
            LocalTarget = Target("local"),
            NasTarget = Target("nas"),
        };

    private static StandaloneTargetFileJournal Target(string role) => new()
    {
        TargetId = Guid.NewGuid(),
        TargetRole = role,
        TargetIdentity = role,
        TemporaryPath = $@"D:\Temp\{role}.partial",
    };

    private static void AssertReason(
        ManifestJournalBindingValidationResult result,
        string expectedPrefix)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Reasons, reason =>
            reason.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }
}
