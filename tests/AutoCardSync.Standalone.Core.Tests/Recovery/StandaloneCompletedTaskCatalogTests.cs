using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class StandaloneCompletedTaskCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-Catalog", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Corrupt_or_precommit_sharded_journals_do_not_block_a_valid_resume_candidate()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);

        string precommit = Path.Combine(paths.TasksDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(precommit, "files"));
        await File.WriteAllTextAsync(Path.Combine(precommit, "manifest.json"), "{}");
        Assert.False(File.Exists(Path.Combine(precommit, "task.json")));

        string corrupt = Path.Combine(paths.TasksDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, "task.json"), "{}");

        Guid validTaskId = Guid.NewGuid();
        var validStore = new StandaloneTaskJournalStore(paths.GetTaskJournalPath(validTaskId));
        await validStore.InitializeAsync(new StandaloneTaskJournal
        {
            TaskId = validTaskId,
            SourceIdentity = "source-A",
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalAndNas,
            ManifestHash = "manifest-A",
            LocalTargetIdentity = "local-A",
            LocalTargetRoot = @"C:\Local",
            NasTargetIdentity = "nas-A",
            NasTargetRoot = @"Z:\Nas",
            Files = [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        var catalog = new StandaloneCompletedTaskCatalog(paths);
        StandaloneTaskJournal? candidate = await catalog.FindResumeCandidateAsync(
            "source-A", CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(validTaskId, candidate.TaskId);
    }

    [Fact]
    public async Task Explicitly_abandoned_journal_is_preserved_but_never_resumed()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Guid taskId = Guid.NewGuid();
        var journalStore = new StandaloneTaskJournalStore(paths.GetTaskJournalPath(taskId));
        await journalStore.InitializeAsync(new StandaloneTaskJournal
        {
            TaskId = taskId,
            SourceIdentity = "source-A",
            CardInstanceId = Guid.NewGuid(),
            TargetMode = StandaloneTargetMode.LocalOnly,
            ManifestHash = "manifest-A",
            LocalTargetIdentity = "local-A",
            LocalTargetRoot = @"C:\Local",
            NasTargetIdentity = string.Empty,
            NasTargetRoot = string.Empty,
            Files = [],
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await new StandaloneAbandonedTaskStore(paths.AbandonedTasksFile).AbandonAsync(
            taskId,
            "source-A",
            "user_confirmed_restart_fresh",
            CancellationToken.None);

        StandaloneTaskJournal? candidate = await new StandaloneCompletedTaskCatalog(paths)
            .FindResumeCandidateAsync("source-A", CancellationToken.None);

        Assert.Null(candidate);
        Assert.True(File.Exists(paths.GetTaskJournalPath(taskId)));
        Assert.Contains(taskId, await new StandaloneAbandonedTaskStore(paths.AbandonedTasksFile)
            .GetTaskIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_abandoned_index_is_preserved_and_does_not_permanently_block_resume()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Guid taskId = Guid.NewGuid();
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(taskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = taskId,
                SourceIdentity = "source-A",
                CardInstanceId = Guid.NewGuid(),
                TargetMode = StandaloneTargetMode.LocalOnly,
                ManifestHash = "manifest-A",
                LocalTargetIdentity = "local-A",
                LocalTargetRoot = @"C:\Local",
                NasTargetIdentity = string.Empty,
                NasTargetRoot = string.Empty,
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await File.WriteAllTextAsync(paths.AbandonedTasksFile, "{not-json");

        StandaloneTaskJournal? candidate = await new StandaloneCompletedTaskCatalog(paths)
            .FindResumeCandidateAsync("source-A", CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(taskId, candidate!.TaskId);
        Assert.Single(Directory.EnumerateFiles(_root, "abandoned-tasks.json.corrupt-*.bak"));
        Assert.Empty(await new StandaloneAbandonedTaskStore(paths.AbandonedTasksFile)
            .GetTaskIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Corrupt_receipt_is_preserved_and_does_not_hide_resume_candidate()
    {
        var paths = new StandaloneDataPaths(_root);
        Directory.CreateDirectory(paths.TasksDirectory);
        Directory.CreateDirectory(paths.ReceiptsDirectory);
        Guid taskId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        await new StandaloneTaskJournalStore(paths.GetTaskJournalPath(taskId)).InitializeAsync(
            new StandaloneTaskJournal
            {
                TaskId = taskId,
                SourceIdentity = "reader-old",
                CardInstanceId = cardId,
                TargetMode = StandaloneTargetMode.LocalOnly,
                ManifestHash = "manifest-A",
                LocalTargetIdentity = "local-A",
                LocalTargetRoot = @"C:\Local",
                NasTargetIdentity = string.Empty,
                NasTargetRoot = string.Empty,
                Files = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await File.WriteAllTextAsync(paths.GetCompletionReceiptPath(taskId), "{not-json");

        var catalog = new StandaloneCompletedTaskCatalog(paths);
        StandaloneTaskJournal? bySource = await catalog.FindResumeCandidateAsync(
            "reader-old", CancellationToken.None);
        StandaloneTaskJournal? byCard = await catalog.FindResumeCandidateByCardInstanceIdAsync(
            cardId, CancellationToken.None);

        Assert.NotNull(bySource);
        Assert.Equal(taskId, bySource!.TaskId);
        Assert.NotNull(byCard);
        Assert.Equal(taskId, byCard!.TaskId);
        Assert.False(File.Exists(paths.GetCompletionReceiptPath(taskId)));
        Assert.Single(Directory.EnumerateFiles(
            paths.ReceiptsDirectory,
            $"{taskId:N}.json.corrupt-*.bak"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
