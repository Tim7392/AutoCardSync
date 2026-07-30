namespace AutoCardSync.Standalone.Core;

public sealed class StandaloneDataPaths
{
    public StandaloneDataPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoCardSync",
            "Standalone"));
    }

    public string Root { get; }
    public string ConfigurationFile => Path.Combine(Root, "configuration.json");
    public string CardIdentityFile => Path.Combine(Root, "card-identities.json");
    public string CardInventoryBaselineFile => Path.Combine(Root, "card-inventory-baselines.json");
    public string TasksDirectory => Path.Combine(Root, "tasks");
    public string ReceiptsDirectory => Path.Combine(Root, "receipts");
    public string AbandonedTasksFile => Path.Combine(Root, "abandoned-tasks.json");
    public string WebViewDataDirectory => Path.Combine(Root, "WebView2");
    public string GetTaskJournalPath(Guid taskId) => Path.Combine(TasksDirectory, $"{taskId:N}.json");
    public string GetTaskJournalDirectory(Guid taskId) => Path.Combine(TasksDirectory, taskId.ToString("N"));
    public string GetCompletionReceiptPath(Guid taskId) => Path.Combine(ReceiptsDirectory, $"{taskId:N}.json");
}
