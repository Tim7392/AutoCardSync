using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Safety;

namespace AutoCardSync.Standalone.Core.Tests.Safety;

public sealed class StandaloneSafetyDecisionTests
{
    [Fact]
    public void Returns_safe_only_when_every_required_fact_is_satisfied()
    {
        StandaloneSafetyResult result = StandaloneSafetyDecision.Evaluate(AllSatisfied());

        Assert.True(result.SafeToRemoveCard);
        Assert.Empty(result.UnmetConditions);
    }

    [Theory]
    [InlineData(nameof(StandaloneSafetyFacts.ManifestFrozen))]
    [InlineData(nameof(StandaloneSafetyFacts.SourceReadOnly))]
    [InlineData(nameof(StandaloneSafetyFacts.AllIncludedFilesAccountedFor))]
    [InlineData(nameof(StandaloneSafetyFacts.LocalTargetFullRereadSha256Passed))]
    [InlineData(nameof(StandaloneSafetyFacts.NasTargetFullRereadSha256Passed))]
    [InlineData(nameof(StandaloneSafetyFacts.FinalObjectsSafelyPublishedOrReused))]
    [InlineData(nameof(StandaloneSafetyFacts.SourceIdentityUnchanged))]
    [InlineData(nameof(StandaloneSafetyFacts.TargetIdentitiesUnchanged))]
    [InlineData(nameof(StandaloneSafetyFacts.LocalCompletionReceiptPersisted))]
    public void Any_missing_required_fact_fails_closed(string propertyName)
    {
        StandaloneSafetyFacts facts = AllSatisfied();
        typeof(StandaloneSafetyFacts).GetProperty(propertyName)!.SetValue(facts, false);

        StandaloneSafetyResult result = StandaloneSafetyDecision.Evaluate(facts);

        Assert.False(result.SafeToRemoveCard);
        Assert.NotEmpty(result.UnmetConditions);
    }

    [Theory]
    [InlineData(nameof(StandaloneSafetyFacts.FailedIncludedFiles), 1)]
    [InlineData(nameof(StandaloneSafetyFacts.PendingIncludedFiles), 1)]
    public void Any_failed_or_pending_file_fails_closed(string propertyName, int value)
    {
        StandaloneSafetyFacts facts = AllSatisfied();
        typeof(StandaloneSafetyFacts).GetProperty(propertyName)!.SetValue(facts, value);

        StandaloneSafetyResult result = StandaloneSafetyDecision.Evaluate(facts);

        Assert.False(result.SafeToRemoveCard);
    }

    [Fact]
    public void Nas_only_does_not_require_an_unselected_local_hash()
    {
        StandaloneSafetyFacts facts = AllSatisfied();
        facts.TargetMode = StandaloneTargetMode.NasOnly;
        facts.LocalTargetFullRereadSha256Passed = false;

        Assert.True(StandaloneSafetyDecision.Evaluate(facts).SafeToRemoveCard);
    }

    [Fact]
    public void Local_only_does_not_require_an_unselected_nas_hash()
    {
        StandaloneSafetyFacts facts = AllSatisfied();
        facts.TargetMode = StandaloneTargetMode.LocalOnly;
        facts.NasTargetFullRereadSha256Passed = false;

        Assert.True(StandaloneSafetyDecision.Evaluate(facts).SafeToRemoveCard);
    }

    [Fact]
    public void Dual_target_still_requires_both_full_rereads()
    {
        StandaloneSafetyFacts facts = AllSatisfied();
        facts.TargetMode = StandaloneTargetMode.LocalAndNas;
        facts.NasTargetFullRereadSha256Passed = false;

        StandaloneSafetyResult result = StandaloneSafetyDecision.Evaluate(facts);

        Assert.False(result.SafeToRemoveCard);
        Assert.Contains("NAS_TARGET_FULL_REREAD_SHA256", result.UnmetConditions);
    }
    private static StandaloneSafetyFacts AllSatisfied() => new()
    {
        ManifestFrozen = true,
        SourceReadOnly = true,
        AllIncludedFilesAccountedFor = true,
        LocalTargetFullRereadSha256Passed = true,
        NasTargetFullRereadSha256Passed = true,
        FinalObjectsSafelyPublishedOrReused = true,
        SourceIdentityUnchanged = true,
        TargetIdentitiesUnchanged = true,
        FailedIncludedFiles = 0,
        PendingIncludedFiles = 0,
        LocalCompletionReceiptPersisted = true,
    };
}
