using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class StandaloneCompletionReceiptValidatorTests
{
    [Fact]
    public void Atomic_receipt_is_valid_even_before_journal_cache_flag_is_updated()
    {
        StandaloneTaskJournal journal = CreateJournal(receiptPersisted: false);
        StandaloneCompletionReceipt receipt = CreateReceipt(journal);

        StandaloneCompletionReceiptValidation result =
            StandaloneCompletionReceiptValidator.Evaluate(receipt, journal);

        Assert.True(result.IsValid, string.Join(", ", result.Reasons));
    }

    [Fact]
    public void Fully_verified_existing_targets_are_valid_without_false_atomic_publish_evidence()
    {
        StandaloneTaskJournal original = CreateJournal(receiptPersisted: true);
        StandaloneTaskJournal journal = original with
        {
            Files = [original.Files[0] with
            {
                LocalTarget = original.Files[0].LocalTarget with
                {
                    AtomicallyPublished = false,
                    ReusedExisting = true,
                },
                NasTarget = original.Files[0].NasTarget with
                {
                    AtomicallyPublished = false,
                    ReusedExisting = true,
                },
            }],
        };

        StandaloneCompletionReceiptValidation result =
            StandaloneCompletionReceiptValidator.Evaluate(CreateReceipt(journal), journal);

        Assert.True(result.IsValid, string.Join(", ", result.Reasons));
    }

    [Theory]
    [InlineData("safe")]
    [InlineData("manifest")]
    [InlineData("local_hash")]
    [InlineData("nas_object")]
    [InlineData("target_state")]
    public void Missing_or_inconsistent_completion_fact_fails_closed(string mutation)
    {
        StandaloneTaskJournal journal = CreateJournal(receiptPersisted: true);
        StandaloneCompletionReceipt receipt = CreateReceipt(journal);
        if (mutation == "safe")
            receipt = receipt with { SafeToRemoveCard = false };
        else if (mutation == "manifest")
            receipt = receipt with { ManifestHash = "different" };
        else if (mutation == "local_hash")
            receipt = receipt with
            {
                Files = [receipt.Files[0] with { LocalFinalSha256 = new string('b', 64) }],
            };
        else if (mutation == "nas_object")
            receipt = receipt with
            {
                Files = [receipt.Files[0] with { NasFinalObjectId = "different" }],
            };
        else if (mutation == "target_state")
            journal = journal with
            {
                Files = [journal.Files[0] with
                {
                    NasTarget = journal.Files[0].NasTarget with { State = StandaloneTargetState.Published },
                }],
            };

        Assert.False(StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).IsValid);
    }

    [Fact]
    public void Nas_only_receipt_ignores_unselected_local_fields_but_binds_target_mode()
    {
        StandaloneTaskJournal dual = CreateJournal(receiptPersisted: true);
        StandaloneTaskJournal journal = dual with
        {
            TargetMode = StandaloneTargetMode.NasOnly,
            LocalTargetIdentity = string.Empty,
            LocalTargetRoot = string.Empty,
            Files = [dual.Files[0] with
            {
                LocalTarget = dual.Files[0].LocalTarget with
                {
                    TargetIdentity = string.Empty,
                    FinalPath = null,
                    FinalObjectIdentity = null,
                    ExpectedSha256 = null,
                    FinalSha256 = null,
                    State = StandaloneTargetState.NotRequired,
                    AtomicallyPublished = false,
                    FullRereadSha256Passed = false,
                },
            }],
        };
        StandaloneCompletionReceipt receipt = CreateReceipt(journal) with
        {
            TargetMode = StandaloneTargetMode.NasOnly,
            LocalTargetIdentity = string.Empty,
            Files = [CreateReceipt(journal).Files[0] with
            {
                LocalFinalObjectId = string.Empty,
                LocalFinalSha256 = string.Empty,
            }],
        };

        Assert.True(StandaloneCompletionReceiptValidator.Evaluate(receipt, journal).IsValid);
        Assert.Contains(
            "receipt_target_mode_mismatch",
            StandaloneCompletionReceiptValidator.Evaluate(
                receipt with { TargetMode = StandaloneTargetMode.LocalAndNas }, journal).Reasons);
    }
    [Fact]
    public void Empty_completed_task_is_never_a_valid_receipt()
    {
        StandaloneTaskJournal journal = CreateJournal(receiptPersisted: true) with { Files = [] };
        StandaloneCompletionReceipt receipt = CreateReceipt(journal);

        StandaloneCompletionReceiptValidation result =
            StandaloneCompletionReceiptValidator.Evaluate(receipt, journal);

        Assert.False(result.IsValid);
        Assert.Contains("receipt_file_count_zero", result.Reasons);
    }
    private static StandaloneTaskJournal CreateJournal(bool receiptPersisted)
    {
        Guid taskId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        string hash = new string('a', 64);
        return new StandaloneTaskJournal
        {
            TaskId = taskId,
            CardInstanceId = Guid.NewGuid(),
            SourceIdentity = "source",
            ManifestHash = "manifest",
            LocalTargetIdentity = "local",
            LocalTargetRoot = @"D:\local",
            NasTargetIdentity = "nas",
            NasTargetRoot = @"\\nas\share\task",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LocalCompletionReceiptPersisted = receiptPersisted,
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = fileId,
                    RelativePath = @"DCIM\sample.jpg",
                    Length = 10,
                    SourceSha256 = hash,
                    State = StandaloneFileState.Verified,
                    LocalTarget = CreateTarget("local", hash),
                    NasTarget = CreateTarget("nas", hash),
                },
            ],
        };
    }

    private static StandaloneTargetFileJournal CreateTarget(string role, string hash) => new()
    {
        TargetId = Guid.NewGuid(),
        TargetRole = role,
        TargetIdentity = role,
        TemporaryPath = string.Empty,
        FinalPath = role + "-final",
        FinalObjectIdentity = role + "-object",
        ExpectedSha256 = hash,
        FinalSha256 = hash,
        State = StandaloneTargetState.Verified,
        AtomicallyPublished = true,
        FullRereadSha256Passed = true,
    };

    private static StandaloneCompletionReceipt CreateReceipt(StandaloneTaskJournal journal) => new()
    {
        TargetMode = journal.TargetMode,
        TaskId = journal.TaskId,
        CardInstanceId = journal.CardInstanceId,
        ManifestHash = journal.ManifestHash,
        SourceIdentity = journal.SourceIdentity,
        LocalTargetIdentity = journal.LocalTargetIdentity,
        NasTargetIdentity = journal.NasTargetIdentity,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        SafeToRemoveCard = true,
        Files = journal.Files.Select(file => new StandaloneCompletionFileFact
        {
            FileId = file.FileId,
            RelativePath = file.RelativePath,
            Length = file.Length,
            SourceSha256 = file.SourceSha256,
            LocalFinalObjectId = file.LocalTarget.FinalObjectIdentity!,
            LocalFinalSha256 = file.LocalTarget.FinalSha256!,
            NasFinalObjectId = file.NasTarget.FinalObjectIdentity!,
            NasFinalSha256 = file.NasTarget.FinalSha256!,
        }).ToArray(),
    };
}
