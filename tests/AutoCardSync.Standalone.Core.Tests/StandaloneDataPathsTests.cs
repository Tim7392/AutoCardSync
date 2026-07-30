using AutoCardSync.Standalone.Core;

namespace AutoCardSync.Standalone.Core.Tests;

public sealed class StandaloneDataPathsTests
{
    [Fact]
    public void Default_root_is_scoped_to_the_standalone_product()
    {
        var paths = new StandaloneDataPaths();

        Assert.EndsWith(
            Path.Combine("AutoCardSync", "Standalone"),
            paths.Root,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(paths.Root, "WebView2"), paths.WebViewDataDirectory);
    }

    [Fact]
    public void Explicit_root_keeps_all_state_under_the_requested_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "AutoCardSync-V1-Paths", Guid.NewGuid().ToString("N"));
        var paths = new StandaloneDataPaths(root);

        Assert.Equal(Path.GetFullPath(root), paths.Root);
        Assert.Equal(Path.Combine(paths.Root, "configuration.json"), paths.ConfigurationFile);
        Assert.Equal(Path.Combine(paths.Root, "card-identities.json"), paths.CardIdentityFile);
        Assert.Equal(Path.Combine(paths.Root, "tasks"), paths.TasksDirectory);
        Assert.Equal(Path.Combine(paths.Root, "receipts"), paths.ReceiptsDirectory);
        Assert.Equal(Path.Combine(paths.Root, "WebView2"), paths.WebViewDataDirectory);
    }
}
