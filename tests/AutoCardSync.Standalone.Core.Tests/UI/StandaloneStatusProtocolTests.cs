using System.Reflection;
using System.Text.Json;
using AutoCardSync.Application.Copying;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Safety;
using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneStatusProtocolTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Metadata_baseline_alone_does_not_claim_verified_backup()
    {
        JsonElement status = Invoke("BaselineReadyStatus", "没有发现新增素材", "元数据与基线一致。");
        Assert.False(status.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.False(status.GetProperty("safeToClear").GetBoolean());
        Assert.Equal("none", status.GetProperty("verificationScope").GetString());
    }

    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(0, 0, false)]
    public void Card_conclusion_requires_current_evidence_for_every_included_file(int included, int verified, bool expected)
    {
        object task = new { taskId = Guid.NewGuid(), totalFiles = 1, targetMode = "nas-only" };
        JsonElement value = Invoke("WithVerificationScope", task, included, verified, true,
            expected ? "verified-original-targets" : "incomplete");
        Assert.Equal(expected, value.GetProperty("safeToClear").GetBoolean());
        Assert.Equal(expected, value.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.True(value.GetProperty("taskVerified").GetBoolean());
        Assert.Equal("nas-only", value.GetProperty("targetMode").GetString());
        Assert.Equal(expected ? "current-inventory" : "task", value.GetProperty("verificationScope").GetString());
        Assert.Equal("PASS", value.GetProperty("taskSafety").GetProperty("nasTargetFullRereadSha256").GetString());
        Assert.Equal("NOT_REQUIRED", value.GetProperty("taskSafety").GetProperty("localTargetFullRereadSha256").GetString());
    }

    [Fact]
    public void Waiting_and_failure_statuses_match_the_web_contract()
    {
        JsonElement waiting = Invoke("WaitingStatus", false, "请先完成首次设置", null);
        Assert.Equal("waiting", waiting.GetProperty("view").GetString());
        Assert.False(waiting.GetProperty("configured").GetBoolean());
        Assert.Equal("waiting", waiting.GetProperty("phase").GetString());

        JsonElement failure = InvokeInstance(
            "FailureStatus", "复制未完成", "目标不可用", false, false, false, false, null, null);
        Assert.Equal("failure", failure.GetProperty("view").GetString());
        Assert.Equal("复制未完成", failure.GetProperty("failure").GetProperty("title").GetString());
        Assert.Equal("目标不可用", failure.GetProperty("failure").GetProperty("what").GetString());
        Assert.False(failure.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.False(failure.GetProperty("failure").GetProperty("canRestartFresh").GetBoolean());
        Assert.False(failure.GetProperty("failure").GetProperty("canReinitializeCard").GetBoolean());
        Assert.False(failure.GetProperty("failure").GetProperty("canReassociateCard").GetBoolean());
    }

    [Fact]
    public void Copying_status_reports_two_valid_targets_and_required_numbers()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(new ManifestEntry
        {
            RelativePath = "DCIM\\sample.jpg",
            FileSize = 1024,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SourceHash = new string('a', 64),
            SourceFileId = "source-file",
            SourceFileIdType = "test",
        });
        manifest.Freeze();
        var local = new TargetCopyStatus
        {
            BytesCopied = 512,
            TotalBytes = 1024,
            TotalFiles = 1,
            FilesVerified = 0,
            Phase = CopyPhase.Copying,
            CurrentFile = "DCIM\\sample.jpg",
        };
        var secondary = local with { BytesCopied = 256 };
        var progress = new StandaloneTransferStatusSnapshot(
            1,
            manifest.TaskId,
            local,
            secondary,
            37.5,
            "DCIM\\sample.jpg",
            128,
            96,
            8,
            0,
            manifest.TotalFiles);

        JsonElement status = Invoke("ToStatus", progress, new { safeToRemoveCard = false });
        Assert.Equal("copying", status.GetProperty("view").GetString());
        Assert.True(status.GetProperty("overallPercent").GetDouble() >= 0);
        Assert.True(status.GetProperty("etaSeconds").GetDouble() >= 0);
        Assert.Equal(0, status.GetProperty("completedFiles").GetInt32());
        Assert.Equal(1, status.GetProperty("totalFiles").GetInt32());
        JsonElement.ArrayEnumerator targets = status.GetProperty("targets").EnumerateArray();
        JsonElement[] values = targets.ToArray();
        Assert.Equal(2, values.Length);
        Assert.Contains(values, target => target.GetProperty("kind").GetString() == "local");
        Assert.Contains(values, target => target.GetProperty("kind").GetString() == "mappedNas");
        Assert.All(values, target => Assert.Equal("copying", target.GetProperty("state").GetString()));
    }

    [Fact]
    public void Verification_status_reports_observed_bytes_speed_eta_and_current_file()
    {
        Guid taskId = Guid.NewGuid();
        var local = new TargetCopyStatus
        {
            TaskId = taskId,
            TargetId = Guid.NewGuid(),
            BytesCopied = 2048,
            BytesTransferred = 2048,
            TemporaryBytesVerified = 1024,
            FinalBytesVerified = 512,
            TotalBytes = 2048,
            TotalFiles = 1,
            CurrentFile = "DCIM\\sample.mov",
            Phase = CopyPhase.FinalVerifying,
        };
        var progress = new StandaloneTransferStatusSnapshot(
            1,
            taskId,
            local,
            new TargetCopyStatus(),
            75,
            "DCIM\\sample.mov",
            0,
            0,
            20,
            0,
            1)
        {
            LocalVerificationBytesPerSecond = 128,
        };

        JsonElement payload = Invoke(
            "ToStatusForMode",
            progress,
            new { safeToRemoveCard = false },
            StandaloneTargetMode.LocalOnly,
            Guid.NewGuid());
        JsonElement target = Assert.Single(payload.GetProperty("targets").EnumerateArray());

        Assert.Equal(37.5, target.GetProperty("verificationPercent").GetDouble());
        Assert.Equal(1536, target.GetProperty("verificationBytes").GetInt64());
        Assert.Equal(4096, target.GetProperty("verificationTotalBytes").GetDouble());
        Assert.Equal(128, target.GetProperty("verificationBytesPerSecond").GetDouble());
        Assert.Equal(20, target.GetProperty("verificationEtaSeconds").GetDouble());
        Assert.Equal("DCIM\\sample.mov", target.GetProperty("currentFile").GetString());
        Assert.Equal("verifying-final", payload.GetProperty("phase").GetString());
    }
    [Fact]
    public void Hundred_percent_realtime_parameters_do_not_create_safe_remove_result()
    {
        Guid taskId = Guid.NewGuid();
        var local = new TargetCopyStatus
        {
            TaskId = taskId,
            TargetId = Guid.NewGuid(),
            BytesCopied = 1024,
            BytesTransferred = 1024,
            TotalBytes = 1024,
            TotalFiles = 1,
            FilesVerified = 1,
            Phase = CopyPhase.Verifying,
        };
        var nas = local with { TargetId = Guid.NewGuid() };
        var progress = new StandaloneTransferStatusSnapshot(
            2,
            taskId,
            local,
            nas,
            100,
            string.Empty,
            256,
            128,
            0,
            1,
            1);

        JsonElement status = Invoke("ToStatus", progress, new { safeToRemoveCard = false });

        Assert.Equal(100, status.GetProperty("overallPercent").GetDouble());
        Assert.Equal("copying", status.GetProperty("view").GetString());
        Assert.False(status.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.False(status.GetProperty("safety").GetProperty("safeToRemoveCard").GetBoolean());
    }

    [Fact]
    public void Complete_status_requires_complete_target_contract()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(new ManifestEntry
        {
            RelativePath = "DCIM\\sample.jpg",
            FileSize = 1,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SourceHash = new string('b', 64),
            SourceFileId = "source-file",
            SourceFileIdType = "test",
        });
        manifest.Freeze();

        JsonElement status = Invoke("CompleteStatus", manifest, new StandaloneSafetyResult(true, []));
        Assert.False(status.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.True(status.GetProperty("taskVerified").GetBoolean());
        status = Invoke("WithVerificationScope", status, 1, 1, true, "verified-original-targets");
        Assert.Equal("complete", status.GetProperty("view").GetString());
        Assert.True(status.GetProperty("safeToRemoveCard").GetBoolean());
        Assert.Equal(1, status.GetProperty("completedFiles").GetInt32());
        Assert.All(status.GetProperty("targets").EnumerateArray(), target =>
        {
            Assert.Equal("complete", target.GetProperty("state").GetString());
            Assert.Equal(100, target.GetProperty("verificationPercent").GetInt32());
            Assert.Equal(0, target.GetProperty("bytesPerSecond").GetDouble());
            Assert.Equal(0, target.GetProperty("verificationBytesPerSecond").GetDouble());
            Assert.Equal(0, target.GetProperty("verificationEtaSeconds").GetDouble());
        });
    }

    [Theory]
    [InlineData(StandaloneTargetMode.NasOnly, "nas-only", "NOT_REQUIRED", "PASS", "mappedNas")]
    [InlineData(StandaloneTargetMode.LocalOnly, "local-only", "PASS", "NOT_REQUIRED", "local")]
    public void Single_target_completion_marks_unselected_target_not_required(
        StandaloneTargetMode mode,
        string expectedMode,
        string expectedLocalSafety,
        string expectedNasSafety,
        string expectedKind)
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(new ManifestEntry
        {
            RelativePath = "DCIM\\sample.jpg",
            FileSize = 1,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SourceHash = new string('c', 64),
            SourceFileId = "source-file",
            SourceFileIdType = "test",
        });
        manifest.Freeze();

        JsonElement status = Invoke(
            "CompleteStatusForMode",
            manifest,
            new StandaloneSafetyResult(true, []),
            mode);

        Assert.False(status.GetProperty("safeToClear").GetBoolean());
        status = Invoke("WithVerificationScope", status, 1, 1, true, "verified-original-targets");

        Assert.Equal(expectedMode, status.GetProperty("targetMode").GetString());
        Assert.Equal(
            expectedLocalSafety,
            status.GetProperty("safety").GetProperty("localTargetFullRereadSha256").GetString());
        Assert.Equal(
            expectedNasSafety,
            status.GetProperty("safety").GetProperty("nasTargetFullRereadSha256").GetString());
        JsonElement target = Assert.Single(status.GetProperty("targets").EnumerateArray());
        Assert.Equal(expectedKind, target.GetProperty("kind").GetString());
        Assert.Equal(0, target.GetProperty("bytesPerSecond").GetDouble());
        Assert.True(status.GetProperty("safeToRemoveCard").GetBoolean());
    }

    [Theory]
    [InlineData(StandaloneTargetMode.NasOnly, "nas-only", "mappedNas")]
    [InlineData(StandaloneTargetMode.LocalOnly, "local-only", "local")]
    public void Single_target_status_omits_the_unselected_target(
        StandaloneTargetMode mode,
        string expectedMode,
        string expectedKind)
    {
        Guid taskId = Guid.NewGuid();
        var target = new TargetCopyStatus
        {
            TaskId = taskId,
            TargetId = Guid.NewGuid(),
            BytesCopied = 512,
            BytesTransferred = 512,
            TotalBytes = 1024,
            TotalFiles = 1,
            Phase = CopyPhase.Copying,
        };
        var progress = new StandaloneTransferStatusSnapshot(
            1,
            taskId,
            mode == StandaloneTargetMode.LocalOnly ? target : new TargetCopyStatus(),
            mode == StandaloneTargetMode.NasOnly ? target : new TargetCopyStatus(),
            25,
            "DCIM\\sample.jpg",
            128,
            96,
            8,
            0,
            1);

        JsonElement status = Invoke(
            "ToStatusForMode",
            progress,
            new { safeToRemoveCard = false },
            mode,
            Guid.NewGuid());
        JsonElement[] targets = status.GetProperty("targets").EnumerateArray().ToArray();

        Assert.Equal(expectedMode, status.GetProperty("targetMode").GetString());
        JsonElement only = Assert.Single(targets);
        Assert.Equal(expectedKind, only.GetProperty("kind").GetString());
        Assert.False(status.GetProperty("safeToRemoveCard").GetBoolean());
    }
    [Fact]
    public void Nas_only_global_phase_uses_the_selected_nas_verification_stage()
    {
        Guid taskId = Guid.NewGuid();
        var nas = new TargetCopyStatus
        {
            TaskId = taskId,
            TargetId = Guid.NewGuid(),
            BytesCopied = 1024,
            BytesTransferred = 1024,
            TotalBytes = 1024,
            TotalFiles = 1,
            Phase = CopyPhase.TemporaryVerifying,
        };
        var progress = new StandaloneTransferStatusSnapshot(
            1,
            taskId,
            new TargetCopyStatus { Phase = CopyPhase.Copying },
            nas,
            50,
            "DCIM\\sample.jpg",
            0,
            0,
            0,
            0,
            1);

        JsonElement status = Invoke(
            "ToStatusForMode",
            progress,
            new { safeToRemoveCard = false },
            StandaloneTargetMode.NasOnly,
            Guid.NewGuid());

        Assert.Equal("verifying-temporary", status.GetProperty("phase").GetString());
    }
    private static JsonElement InvokeInstance(string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException($"Missing status method {methodName}.");
        object instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(StandaloneRuntimeService));
        object value = method.Invoke(instance, arguments) ?? throw new InvalidOperationException("Status was null.");
        return JsonSerializer.SerializeToElement(value, WebJson);
    }

    private static JsonElement Invoke(string methodName, params object?[] arguments)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException($"Missing status method {methodName}.");
        object value = method.Invoke(null, arguments) ?? throw new InvalidOperationException("Status was null.");
        return JsonSerializer.SerializeToElement(value, WebJson);
    }
}
