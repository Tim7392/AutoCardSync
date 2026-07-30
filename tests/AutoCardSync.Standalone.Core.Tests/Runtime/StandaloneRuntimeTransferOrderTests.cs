namespace AutoCardSync.Standalone.Core.Tests.Runtime;

public sealed class StandaloneRuntimeTransferOrderTests
{
    [Fact]
    public void Initial_and_delta_inventory_scan_is_metadata_only_and_contains_no_prehash_path()
    {
        string source = ReadRuntimeSource();
        Assert.Contains("SourceHashPolicy.MetadataOnly", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceHashPolicy.Sha256DuringScan", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FinalPublishedObjectVerifier", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_baseline_returns_before_any_target_resolution_or_directory_creation()
    {
        string source = ReadRuntimeSource();
        int saveInitial = source.IndexOf("await baselineStore.SaveInitialAsync(", StringComparison.Ordinal);
        int baselineReturn = source.IndexOf("return;", saveInitial, StringComparison.Ordinal);
        int resolveTargets = source.IndexOf("ResolvedTargetRoots targetRoots = ResolveTargetRoots(", StringComparison.Ordinal);
        int prepareTargetDirectory = source.IndexOf("localDomain = PrepareLocalTargetRoot(", StringComparison.Ordinal);
        int resolveNas = source.IndexOf("secondaryTarget = ResolveSecondaryTarget(", StringComparison.Ordinal);

        Assert.True(saveInitial >= 0);
        Assert.True(baselineReturn > saveInitial);
        Assert.True(resolveTargets > baselineReturn);
        Assert.True(prepareTargetDirectory > baselineReturn);
        Assert.True(resolveNas > baselineReturn);
    }

    [Fact]
    public void Active_fresh_and_frozen_paths_do_not_call_legacy_per_target_source_copy()
    {
        string source = ReadRuntimeSource();
        Assert.Contains("new FreshTransferCoordinator()", source, StringComparison.Ordinal);
        Assert.Contains("new FrozenTransferFinalizer()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new DualTargetCopyCoordinator()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".CopyToTargetAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_advances_only_after_completion_safety_returns()
    {
        string source = ReadRuntimeSource();
        int freshCompletion = source.IndexOf("safety = await PersistCompletionAsync(", StringComparison.Ordinal);
        int frozenCompletion = source.IndexOf("safety = await PersistCompletionAsync(", freshCompletion + 1, StringComparison.Ordinal);
        int advanceBaseline = source.IndexOf("await baselineStore.AdvanceAfterCompletionAsync(", StringComparison.Ordinal);

        Assert.True(freshCompletion >= 0);
        Assert.True(frozenCompletion > freshCompletion);
        Assert.True(advanceBaseline > frozenCompletion);
    }

    private static string ReadRuntimeSource()
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "src",
            "AutoCardSync.Standalone",
            "Services",
            "StandaloneRuntimeService.cs"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AutoCardSync.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
