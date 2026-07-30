using System.Reflection;
using System.Text.Json;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Transfer;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class CompletedTaskReuseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AutoCardSync-CompletedReuse-{Guid.NewGuid():N}");

    [Fact]
    public void Matching_manifest_receipt_identities_and_roots_are_reusable()
    {
        (TaskManifest manifest, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();

        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.Evaluate(
            manifest,
            journal,
            receipt,
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot);

        Assert.True(result.CanReuse, string.Join(", ", result.Reasons));
    }

    [Fact]
    public void Persisted_completion_rebuild_accepts_matching_journal_receipt_identities_and_roots()
    {
        (_, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();

        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.EvaluatePersisted(
            journal,
            receipt,
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot);

        Assert.True(result.CanReuse, string.Join(", ", result.Reasons));
    }

    [Fact]
    public void Persisted_completion_rebuild_fails_closed_when_receipt_is_not_committed()
    {
        (_, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();

        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.EvaluatePersisted(
            journal,
            receipt with { SafeToRemoveCard = false },
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot);

        Assert.False(result.CanReuse);
        Assert.Contains("receipt_not_committed", result.Reasons);
    }

    [Fact]
    public void Runtime_completed_status_is_rebuilt_from_validated_persisted_candidate()
    {
        (_, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();
        MethodInfo method = typeof(AutoCardSync.Standalone.Services.StandaloneRuntimeService).GetMethod(
            "RestoreCompletedStatus",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing persisted completion status restorer.");

        object value = method.Invoke(null,
        [
            new StandaloneCompletedTaskCandidate(journal, receipt),
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot,
        ]) ?? throw new InvalidOperationException("Restored status was null.");
        JsonElement status = JsonSerializer.SerializeToElement(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("complete", status.GetProperty("view").GetString());
        Assert.Equal(journal.TaskId, status.GetProperty("taskId").GetGuid());
        Assert.Equal(journal.Files.Count, status.GetProperty("totalFiles").GetInt32());
        Assert.True(status.GetProperty("safeToRemoveCard").GetBoolean());
    }

    [Fact]
    public void Persisted_completion_rebuild_requires_journal_receipt_commit_fact()
    {
        (_, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();

        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.EvaluatePersisted(
            journal with { LocalCompletionReceiptPersisted = false },
            receipt,
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot);

        Assert.False(result.CanReuse);
        Assert.Contains("journal_completion_receipt_not_persisted", result.Reasons);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("source")]
    [InlineData("local-identity")]
    [InlineData("nas-identity")]
    [InlineData("local-root")]
    [InlineData("nas-root")]
    [InlineData("receipt-hash")]
    public void Reuse_rejects_any_changed_persisted_fact(string changedFact)
    {
        (TaskManifest manifest, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();
        RecoveryIdentitySnapshot current = CurrentSnapshot(journal);
        string localRoot = journal.LocalTargetRoot;
        string nasRoot = journal.NasTargetRoot;

        switch (changedFact)
        {
            case "manifest":
                journal = journal with { ManifestHash = new string('f', 64) };
                break;
            case "source":
                current = current with { SourceIdentity = "source-changed" };
                break;
            case "local-identity":
                current = current with { LocalTargetIdentity = "local-changed" };
                break;
            case "nas-identity":
                current = current with { NasTargetIdentity = "nas-changed" };
                break;
            case "local-root":
                localRoot += "-changed";
                break;
            case "nas-root":
                nasRoot += "-changed";
                break;
            case "receipt-hash":
                StandaloneCompletionFileFact original = Assert.Single(receipt.Files);
                receipt = receipt with
                {
                    Files = [original with { LocalFinalSha256 = new string('e', 64) }],
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedFact));
        }

        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.Evaluate(
            manifest,
            journal,
            receipt,
            current,
            localRoot,
            nasRoot);

        Assert.False(result.CanReuse);
    }

    [Fact]
    public async Task Targeted_completed_lookup_does_not_load_an_unrelated_corrupt_receipt()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Directory.CreateDirectory(paths.ReceiptsDirectory);
        (_, StandaloneTaskJournal corruptJournal, _) = CreateCompletedTask();
        (_, StandaloneTaskJournal validJournal, StandaloneCompletionReceipt validReceipt) = CreateCompletedTask();
        await new AtomicJsonFileStore<StandaloneTaskJournal>(paths.GetTaskJournalPath(corruptJournal.TaskId))
            .SaveAsync(corruptJournal, CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneTaskJournal>(paths.GetTaskJournalPath(validJournal.TaskId))
            .SaveAsync(validJournal, CancellationToken.None);
        await File.WriteAllTextAsync(
            paths.GetCompletionReceiptPath(corruptJournal.TaskId),
            "{not-json",
            CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneCompletionReceipt>(paths.GetCompletionReceiptPath(validJournal.TaskId))
            .SaveAsync(validReceipt, CancellationToken.None);
        var catalog = new StandaloneCompletedTaskCatalog(paths);

        StandaloneCompletedTaskCandidate? candidate = await catalog.FindCompletedCandidateAsync(
            validJournal.SourceIdentity,
            validJournal.TaskId,
            CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(validJournal.TaskId, candidate.Journal.TaskId);
    }

    [Fact]
    public async Task Receipt_bearing_task_is_selected_for_completed_revalidation_and_never_as_resume()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Directory.CreateDirectory(paths.ReceiptsDirectory);
        (_, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();
        await new AtomicJsonFileStore<StandaloneTaskJournal>(paths.GetTaskJournalPath(journal.TaskId))
            .SaveAsync(journal, CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneCompletionReceipt>(paths.GetCompletionReceiptPath(journal.TaskId))
            .SaveAsync(receipt, CancellationToken.None);
        string[] before = Directory.GetFiles(paths.TasksDirectory);
        var catalog = new StandaloneCompletedTaskCatalog(paths);

        IReadOnlyList<StandaloneCompletedTaskCandidate> completed =
            await catalog.FindCompletedCandidatesAsync(journal.SourceIdentity, CancellationToken.None);
        StandaloneTaskJournal? resume =
            await catalog.FindResumeCandidateAsync(journal.SourceIdentity, CancellationToken.None);

        StandaloneCompletedTaskCandidate candidate = Assert.Single(completed);
        Assert.Equal(journal.TaskId, candidate.Journal.TaskId);
        Assert.Null(resume);
        Assert.Equal(before, Directory.GetFiles(paths.TasksDirectory));
    }

    [Fact]
    public async Task Journal_without_receipt_remains_available_for_normal_resume()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        (_, StandaloneTaskJournal journal, _) = CreateCompletedTask();
        journal = journal with { LocalCompletionReceiptPersisted = false };
        await new AtomicJsonFileStore<StandaloneTaskJournal>(paths.GetTaskJournalPath(journal.TaskId))
            .SaveAsync(journal, CancellationToken.None);
        var catalog = new StandaloneCompletedTaskCatalog(paths);

        Assert.Empty(await catalog.FindCompletedCandidatesAsync(journal.SourceIdentity, CancellationToken.None));
        StandaloneTaskJournal? resume =
            await catalog.FindResumeCandidateAsync(journal.SourceIdentity, CancellationToken.None);

        Assert.NotNull(resume);
        Assert.Equal(journal.TaskId, resume.TaskId);
    }

    [Fact]
    public async Task Invalid_receipt_is_preserved_and_journal_returns_to_resume_path()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Directory.CreateDirectory(paths.ReceiptsDirectory);
        (TaskManifest manifest, StandaloneTaskJournal journal, StandaloneCompletionReceipt receipt) = CreateCompletedTask();
        receipt = receipt with { SafeToRemoveCard = false };
        await new AtomicJsonFileStore<StandaloneTaskJournal>(paths.GetTaskJournalPath(journal.TaskId))
            .SaveAsync(journal, CancellationToken.None);
        await new AtomicJsonFileStore<StandaloneCompletionReceipt>(paths.GetCompletionReceiptPath(journal.TaskId))
            .SaveAsync(receipt, CancellationToken.None);
        var catalog = new StandaloneCompletedTaskCatalog(paths);

        StandaloneCompletedTaskCandidate candidate = Assert.Single(
            await catalog.FindCompletedCandidatesAsync(journal.SourceIdentity, CancellationToken.None));
        CompletedTaskReuseEligibility result = CompletedTaskReuseGuard.Evaluate(
            manifest,
            candidate.Journal,
            candidate.Receipt,
            CurrentSnapshot(journal),
            journal.LocalTargetRoot,
            journal.NasTargetRoot);

        Assert.False(result.CanReuse);
        StandaloneTaskJournal? resume = await catalog.FindResumeCandidateAsync(
            journal.SourceIdentity, CancellationToken.None);
        Assert.NotNull(resume);
        Assert.Equal(journal.TaskId, resume!.TaskId);
        Assert.False(File.Exists(paths.GetCompletionReceiptPath(journal.TaskId)));
        Assert.Single(Directory.EnumerateFiles(
            paths.ReceiptsDirectory,
            $"{journal.TaskId:N}.json.corrupt-*.bak"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static RecoveryIdentitySnapshot CurrentSnapshot(StandaloneTaskJournal journal) => new(
        journal.SourceIdentity,
        journal.CardInstanceId,
        journal.ManifestHash,
        journal.LocalTargetIdentity,
        journal.NasTargetIdentity);

    private static (TaskManifest Manifest, StandaloneTaskJournal Journal, StandaloneCompletionReceipt Receipt)
        CreateCompletedTask()
    {
        Guid taskId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var manifest = new TaskManifest(taskId);
        manifest.AddEntry(new ManifestEntry
        {
            Id = fileId,
            RelativePath = @"XDROOT\Clip\sample.xml",
            FileSize = 7,
            LastModifiedUtc = DateTimeOffset.Parse("2026-07-24T00:00:00Z"),
            SourceHash = hash,
            SourceFileId = "source-object",
            SourceFileIdType = "test",
        });
        manifest.Freeze();
        var local = new StandaloneTargetFileJournal
        {
            TargetId = Guid.NewGuid(),
            TargetRole = "local",
            TargetIdentity = "local-volume",
            TemporaryPath = string.Empty,
            FinalPath = @"D:\target\task\XDROOT\Clip\sample.xml",
            FinalObjectIdentity = "local-object",
            ExpectedSha256 = hash,
            FinalSha256 = hash,
            State = StandaloneTargetState.Verified,
            AtomicallyPublished = true,
            FullRereadSha256Passed = true,
        };
        var nas = local with
        {
            TargetId = Guid.NewGuid(),
            TargetRole = "nas",
            TargetIdentity = "nas-share",
            FinalPath = @"\\nas\share\task\XDROOT\Clip\sample.xml",
            FinalObjectIdentity = "nas-object",
        };
        var journal = new StandaloneTaskJournal
        {
            TaskId = taskId,
            CardInstanceId = cardId,
            SourceIdentity = "source-volume",
            ManifestHash = manifest.ManifestHash,
            LocalTargetIdentity = "local-volume",
            LocalTargetRoot = @"D:\target\task",
            NasTargetIdentity = "nas-share",
            NasTargetRoot = @"\\nas\share\task",
            LocalCompletionReceiptPersisted = true,
            UpdatedAtUtc = DateTimeOffset.Parse("2026-07-24T00:01:00Z"),
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = fileId,
                    RelativePath = @"XDROOT\Clip\sample.xml",
                    Length = 7,
                    SourceSha256 = hash,
                    SourceFileIdentity = "source-object",
                    State = StandaloneFileState.Verified,
                    LocalTarget = local,
                    NasTarget = nas,
                },
            ],
        };
        var receipt = new StandaloneCompletionReceipt
        {
            TaskId = taskId,
            CardInstanceId = cardId,
            ManifestHash = manifest.ManifestHash,
            SourceIdentity = journal.SourceIdentity,
            LocalTargetIdentity = journal.LocalTargetIdentity,
            NasTargetIdentity = journal.NasTargetIdentity,
            CompletedAtUtc = DateTimeOffset.Parse("2026-07-24T00:02:00Z"),
            SafeToRemoveCard = true,
            Files =
            [
                new StandaloneCompletionFileFact
                {
                    FileId = fileId,
                    RelativePath = @"XDROOT\Clip\sample.xml",
                    Length = 7,
                    SourceSha256 = hash,
                    LocalFinalObjectId = "local-object",
                    LocalFinalSha256 = hash,
                    NasFinalObjectId = "nas-object",
                    NasFinalSha256 = hash,
                },
            ],
        };
        return (manifest, journal, receipt);
    }
}
