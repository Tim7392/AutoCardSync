using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class CompletedInventoryEvidencePlannerTests
{
    private static readonly Guid CardInstanceId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Create_groups_files_across_original_candidate_target_modes()
    {
        TaskManifest inventory = Inventory(
            Entry(@"DCIM\A.mov", 10, "source-a"),
            Entry(@"DCIM\B.mov", 20, "source-b"),
            Entry(@"PRIVATE\ignored.tmp", 30, "excluded", excluded: true));
        StandaloneCompletedTaskCandidate newerNas = Candidate(
            StandaloneTargetMode.NasOnly,
            [JournalFile(@"DCIM\B.mov", 20, "source-b")]);
        StandaloneCompletedTaskCandidate olderLocal = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [newerNas, olderLocal]);

        Assert.Equal(2, plan.IncludedFileCount);
        Assert.Equal(0, plan.UncoveredFileCount);
        Assert.Collection(
            plan.Groups,
            group =>
            {
                Assert.Same(newerNas, group.Candidate);
                Assert.Equal(StandaloneTargetMode.NasOnly, group.Candidate.Journal.TargetMode);
                Assert.Equal([@"DCIM\B.mov"], group.RelativePaths);
            },
            group =>
            {
                Assert.Same(olderLocal, group.Candidate);
                Assert.Equal(StandaloneTargetMode.LocalOnly, group.Candidate.Journal.TargetMode);
                Assert.Equal([@"DCIM\A.mov"], group.RelativePaths);
            });
    }

    [Fact]
    public void Newer_target_mode_does_not_backfill_file_absent_from_that_candidate()
    {
        TaskManifest inventory = Inventory(
            Entry(@"DCIM\A.mov", 10, "source-a"),
            Entry(@"DCIM\B.mov", 20, "source-b"));
        StandaloneCompletedTaskCandidate newerNas = Candidate(
            StandaloneTargetMode.NasOnly,
            [JournalFile(@"DCIM\B.mov", 20, "source-b")]);
        StandaloneCompletedTaskCandidate olderLocal = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [newerNas, olderLocal]);

        CompletedInventoryEvidenceGroup groupForA = Assert.Single(
            plan.Groups,
            group => group.RelativePaths.Contains(@"DCIM\A.mov"));
        Assert.Same(olderLocal, groupForA.Candidate);
    }

    [Fact]
    public void Candidate_covering_only_one_file_leaves_the_other_uncovered()
    {
        TaskManifest inventory = Inventory(
            Entry(@"DCIM\A.mov", 10, "source-a"),
            Entry(@"DCIM\B.mov", 20, "source-b"));
        StandaloneCompletedTaskCandidate candidate = Candidate(
            StandaloneTargetMode.NasOnly,
            [JournalFile(@"DCIM\B.mov", 20, "source-b")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [candidate]);

        Assert.Equal(2, plan.IncludedFileCount);
        Assert.Equal(1, plan.UncoveredFileCount);
        Assert.Equal([@"DCIM\B.mov"], Assert.Single(plan.Groups).RelativePaths);
    }

    [Theory]
    [InlineData("changed-object")]
    [InlineData("")]
    public void Changed_or_missing_current_source_object_identity_is_uncovered(
        string currentSourceIdentity)
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, currentSourceIdentity));
        StandaloneCompletedTaskCandidate candidate = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [candidate]);

        Assert.Empty(plan.Groups);
        Assert.Equal(1, plan.IncludedFileCount);
        Assert.Equal(1, plan.UncoveredFileCount);
    }

    [Fact]
    public void Same_path_and_identity_with_different_length_is_uncovered()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 11, "source-a"));
        StandaloneCompletedTaskCandidate candidate = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [candidate]);

        Assert.Empty(plan.Groups);
        Assert.Equal(1, plan.IncludedFileCount);
        Assert.Equal(1, plan.UncoveredFileCount);
    }

    [Fact]
    public void Duplicate_current_paths_are_rejected_case_insensitively()
    {
        TaskManifest inventory = Inventory(
            Entry(@"DCIM\A.mov", 10, "source-a"),
            Entry(@"dcim\a.MOV", 10, "source-a"));

        Assert.Throws<InvalidDataException>(() => CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            []));
    }

    [Fact]
    public void Duplicate_journal_paths_are_rejected_case_insensitively()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate candidate = Candidate(
            StandaloneTargetMode.LocalOnly,
            [
                JournalFile(@"DCIM\A.mov", 10, "source-a"),
                JournalFile(@"dcim\a.MOV", 10, "source-a"),
            ]);

        Assert.Throws<InvalidDataException>(() => CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [candidate]));
    }

    [Fact]
    public void Empty_journal_path_is_rejected()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate candidate = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(" ", 10, "source-a")]);

        Assert.Throws<InvalidDataException>(() => CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [candidate]));
    }

    [Fact]
    public void Candidates_for_different_card_or_source_are_excluded()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate wrongCard = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")],
            cardInstanceId: Guid.NewGuid());
        StandaloneCompletedTaskCandidate wrongSource = Candidate(
            StandaloneTargetMode.NasOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")],
            sourceIdentity: "another-source");

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [wrongCard, wrongSource]);

        Assert.Empty(plan.Groups);
        Assert.Equal(1, plan.IncludedFileCount);
        Assert.Equal(1, plan.UncoveredFileCount);
    }

    [Fact]
    public void Explicit_rebinding_authorization_includes_same_card_at_old_source_endpoint()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate oldEndpoint = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")],
            sourceIdentity: "old-source-endpoint");
        var authorization = new CompletedTaskSourceRebindingAuthorization(
            CardInstanceId,
            "current-source-endpoint");

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "current-source-endpoint",
            [oldEndpoint],
            authorization);

        CompletedInventoryEvidenceGroup group = Assert.Single(plan.Groups);
        Assert.Same(oldEndpoint, group.Candidate);
        Assert.Equal(1, plan.IncludedFileCount);
        Assert.Equal(0, plan.UncoveredFileCount);
    }

    [Fact]
    public void Rebinding_authorization_cannot_include_a_different_card()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate otherCard = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")],
            cardInstanceId: Guid.NewGuid(),
            sourceIdentity: "old-source-endpoint");
        var authorization = new CompletedTaskSourceRebindingAuthorization(
            CardInstanceId,
            "current-source-endpoint");

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "current-source-endpoint",
            [otherCard],
            authorization);

        Assert.Empty(plan.Groups);
        Assert.Equal(1, plan.UncoveredFileCount);
    }

    [Fact]
    public void First_matching_candidate_wins_without_duplicate_coverage()
    {
        TaskManifest inventory = Inventory(Entry(@"DCIM\A.mov", 10, "source-a"));
        StandaloneCompletedTaskCandidate newer = Candidate(
            StandaloneTargetMode.NasOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);
        StandaloneCompletedTaskCandidate older = Candidate(
            StandaloneTargetMode.LocalOnly,
            [JournalFile(@"DCIM\A.mov", 10, "source-a")]);

        CompletedInventoryEvidencePlan plan = CompletedInventoryEvidencePlanner.Create(
            inventory,
            CardInstanceId,
            "source-card",
            [newer, older]);

        CompletedInventoryEvidenceGroup group = Assert.Single(plan.Groups);
        Assert.Same(newer, group.Candidate);
        Assert.Equal(1, plan.IncludedFileCount);
        Assert.Equal(0, plan.UncoveredFileCount);
    }

    private static TaskManifest Inventory(params ManifestEntry[] entries)
    {
        var manifest = new TaskManifest(Guid.NewGuid()) { FilterRuleVersion = "metadata-only" };
        foreach (ManifestEntry entry in entries)
            manifest.AddEntry(entry);
        manifest.Freeze();
        return manifest;
    }

    private static ManifestEntry Entry(
        string relativePath,
        long length,
        string sourceFileId,
        bool excluded = false) => new()
        {
            RelativePath = relativePath,
            FileSize = length,
            LastModifiedUtc = DateTimeOffset.Parse("2026-09-09T00:00:00Z"),
            SourceFileId = sourceFileId,
            SourceFileIdType = "ntfs",
            Excluded = excluded,
            ExclusionRule = excluded ? ManifestExclusionRules.ExtensionNotIncluded : null,
        };

    private static StandaloneCompletedTaskCandidate Candidate(
        StandaloneTargetMode mode,
        IReadOnlyList<StandaloneFileJournal> files,
        Guid? cardInstanceId = null,
        string sourceIdentity = "source-card")
    {
        Guid taskId = Guid.NewGuid();
        Guid cardId = cardInstanceId ?? CardInstanceId;
        var journal = new StandaloneTaskJournal
        {
            TaskId = taskId,
            CardInstanceId = cardId,
            SourceIdentity = sourceIdentity,
            TargetMode = mode,
            ManifestHash = $"manifest-{taskId:N}",
            LocalTargetIdentity = mode.RequiresLocal() ? "local" : string.Empty,
            LocalTargetRoot = mode.RequiresLocal() ? @"D:\Local" : string.Empty,
            NasTargetIdentity = mode.RequiresNas() ? "nas" : string.Empty,
            NasTargetRoot = mode.RequiresNas() ? @"Y:\Nas" : string.Empty,
            Files = files,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LocalCompletionReceiptPersisted = true,
        };
        var receipt = new StandaloneCompletionReceipt
        {
            TaskId = taskId,
            CardInstanceId = cardId,
            SourceIdentity = sourceIdentity,
            TargetMode = mode,
            ManifestHash = journal.ManifestHash,
            LocalTargetIdentity = journal.LocalTargetIdentity,
            NasTargetIdentity = journal.NasTargetIdentity,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Files = [],
            SafeToRemoveCard = true,
        };
        return new(journal, receipt);
    }

    private static StandaloneFileJournal JournalFile(
        string relativePath,
        long length,
        string sourceFileIdentity) => new()
        {
            FileId = Guid.NewGuid(),
            RelativePath = relativePath,
            Length = length,
            SourceSha256 = new string('a', 64),
            SourceFileIdentity = sourceFileIdentity,
            State = StandaloneFileState.Verified,
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
}
