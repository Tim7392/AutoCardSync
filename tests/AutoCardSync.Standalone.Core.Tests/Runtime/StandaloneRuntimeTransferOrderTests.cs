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
    public void New_card_boundary_is_persisted_before_target_resolution_and_empty_cards_return_without_target_io()
    {
        string source = ReadRuntimeSource();
        int newCardBranch = source.IndexOf("if (resumeCandidate is null && baseline is null)", StringComparison.Ordinal);
        int persistPending = source.IndexOf("await baselineStore.ReinitializePendingAsync(", newCardBranch, StringComparison.Ordinal);
        int commitBoundary = source.IndexOf("await baselineStore.CommitInitializationAsync(", persistPending, StringComparison.Ordinal);
        int emptyCardBranch = source.IndexOf("if (fullInventory.TotalFiles == 0)", commitBoundary, StringComparison.Ordinal);
        int emptyCardReturn = source.IndexOf("return;", emptyCardBranch, StringComparison.Ordinal);
        int resolveTargets = source.IndexOf("ResolvedTargetRoots targetRoots = ResolveTargetRoots(", emptyCardReturn, StringComparison.Ordinal);
        int prepareTargetDirectory = source.IndexOf("localDomain = PrepareLocalTargetRoot(", resolveTargets, StringComparison.Ordinal);
        int resolveNas = source.IndexOf("secondaryTarget = ResolveSecondaryTarget(", resolveTargets, StringComparison.Ordinal);

        Assert.True(newCardBranch >= 0);
        Assert.True(persistPending > newCardBranch);
        Assert.True(commitBoundary > persistPending);
        Assert.True(emptyCardBranch > commitBoundary);
        Assert.True(emptyCardReturn > emptyCardBranch);
        Assert.True(resolveTargets > emptyCardReturn);
        Assert.True(prepareTargetDirectory > resolveTargets);
        Assert.True(resolveNas > resolveTargets);
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
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AutoCardSync.Standalone.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
