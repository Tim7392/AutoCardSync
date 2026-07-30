using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Core.Tests.Recovery;

public sealed class RecoveryEligibilityTests
{
    private static readonly Guid CardId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Allows_recovery_only_for_same_source_manifest_and_targets()
    {
        StandaloneTaskJournal journal = CreateJournal();
        var current = new RecoveryIdentitySnapshot(
            "source-A", CardId, "manifest-A", "local-A", "nas-A");

        RecoveryEligibility result = RecoveryGuard.Evaluate(journal, current);

        Assert.True(result.CanResume);
        Assert.Empty(result.Reasons);
    }

    [Theory]
    [InlineData("source-B", "manifest-A", "local-A", "nas-A", "source_identity_changed")]
    [InlineData("source-A", "manifest-B", "local-A", "nas-A", "manifest_changed")]
    [InlineData("source-A", "manifest-A", "local-B", "nas-A", "local_target_identity_changed")]
    [InlineData("source-A", "manifest-A", "local-A", "nas-B", "nas_target_identity_changed")]
    public void Rejects_recovery_when_any_frozen_identity_changes(
        string sourceIdentity,
        string manifestHash,
        string localIdentity,
        string nasIdentity,
        string expectedReason)
    {
        StandaloneTaskJournal journal = CreateJournal();
        var current = new RecoveryIdentitySnapshot(
            sourceIdentity, CardId, manifestHash, localIdentity, nasIdentity);

        RecoveryEligibility result = RecoveryGuard.Evaluate(journal, current);

        Assert.False(result.CanResume);
        Assert.Contains(expectedReason, result.Reasons);
    }

    [Fact]
    public void Rejects_recovery_when_card_binding_changes()
    {
        RecoveryEligibility result = RecoveryGuard.Evaluate(
            CreateJournal(),
            new RecoveryIdentitySnapshot(
                "source-A", Guid.NewGuid(), "manifest-A", "local-A", "nas-A"));

        Assert.False(result.CanResume);
        Assert.Contains("card_identity_changed", result.Reasons);
    }

    [Fact]
    public void Rejects_recovery_when_target_mode_changes()
    {
        StandaloneTaskJournal journal = CreateJournal();
        RecoveryEligibility result = RecoveryGuard.Evaluate(
            journal,
            new RecoveryIdentitySnapshot(
                "source-A", CardId, "manifest-A", "local-A", "nas-A",
                StandaloneTargetMode.NasOnly));

        Assert.False(result.CanResume);
        Assert.Contains("target_mode_changed", result.Reasons);
    }

    [Fact]
    public void Nas_only_ignores_unselected_local_identity_but_still_binds_nas()
    {
        StandaloneTaskJournal journal = CreateJournal() with
        {
            TargetMode = StandaloneTargetMode.NasOnly,
            LocalTargetIdentity = string.Empty,
        };

        RecoveryEligibility sameNas = RecoveryGuard.Evaluate(
            journal,
            new RecoveryIdentitySnapshot(
                "source-A", CardId, "manifest-A", "changed-local", "nas-A",
                StandaloneTargetMode.NasOnly));
        RecoveryEligibility changedNas = RecoveryGuard.Evaluate(
            journal,
            new RecoveryIdentitySnapshot(
                "source-A", CardId, "manifest-A", "changed-local", "nas-B",
                StandaloneTargetMode.NasOnly));

        Assert.True(sameNas.CanResume);
        Assert.False(changedNas.CanResume);
        Assert.Contains("nas_target_identity_changed", changedNas.Reasons);
    }
    [Fact]
    public void Target_state_numeric_values_remain_compatible_with_legacy_json_journals()
    {
        Assert.Equal(0, (int)StandaloneTargetState.Pending);
        Assert.Equal(1, (int)StandaloneTargetState.Copying);
        Assert.Equal(2, (int)StandaloneTargetState.Published);
        Assert.Equal(3, (int)StandaloneTargetState.Verified);
        Assert.Equal(4, (int)StandaloneTargetState.Failed);
        Assert.Equal(5, (int)StandaloneTargetState.NotRequired);
    }
    private static StandaloneTaskJournal CreateJournal() => new()
    {
        TaskId = Guid.NewGuid(),
        SourceIdentity = "source-A",
        CardInstanceId = CardId,
        ManifestHash = "manifest-A",
        LocalTargetIdentity = "local-A",
        LocalTargetRoot = @"C:\Local",
        NasTargetIdentity = "nas-A",
        NasTargetRoot = @"Z:\Nas",
        Files = [],
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}
