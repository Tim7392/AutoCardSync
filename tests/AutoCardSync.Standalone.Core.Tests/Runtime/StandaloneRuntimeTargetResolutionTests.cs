using System.Reflection;
using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;
using AutoCardSync.Standalone.Core.Safety;
using AutoCardSync.Standalone.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoCardSync.Standalone.Core.Tests.Runtime;

public sealed class StandaloneRuntimeTargetResolutionTests
{
    [Fact]
    public void Secondary_target_requires_a_mapped_drive_and_never_falls_back_to_local_storage()
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "ResolveSecondaryTarget",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing secondary target resolver.");

        var invocation = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [new FaultDomainResolver(), @"\\server\share", @"\\server\share\task", false]));

        Assert.IsType<ArgumentException>(invocation.InnerException);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("local")]
    [InlineData("nas")]
    public void Safety_rejects_any_final_volume_identity_change(string changedIdentity)
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(new ManifestEntry
        {
            RelativePath = @"DCIM\sample.jpg",
            FileSize = 1,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SourceHash = new string('a', 64),
            SourceFileId = "source-file",
            SourceFileIdType = "test",
        });
        manifest.Freeze();
        StandaloneTaskJournal journal = CreateVerifiedJournal(manifest);
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "BuildSafety",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing safety builder.");

        string source = changedIdentity == "source" ? "changed" : journal.SourceIdentity;
        string local = changedIdentity == "local" ? "changed" : journal.LocalTargetIdentity;
        string nas = changedIdentity == "nas" ? "changed" : journal.NasTargetIdentity;
        var safety = (StandaloneSafetyResult)(method.Invoke(
            null,
            [manifest, journal, source, local, nas, true]) ??
            throw new InvalidOperationException("Safety result was null."));

        Assert.False(safety.SafeToRemoveCard);
    }

    [Fact]
    public void Safety_rejects_same_count_journal_when_manifest_file_facts_do_not_match()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.AddEntry(new ManifestEntry
        {
            RelativePath = @"DCIM\sample.jpg",
            FileSize = 1,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            SourceHash = new string('a', 64),
            SourceFileId = "source-file",
            SourceFileIdType = "test",
        });
        manifest.Freeze();
        StandaloneTaskJournal original = CreateVerifiedJournal(manifest);
        StandaloneTaskJournal tampered = original with
        {
            Files =
            [
                original.Files[0] with
                {
                    RelativePath = @"DCIM\different.jpg",
                },
            ],
        };
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "BuildSafety",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing safety builder.");

        var safety = (StandaloneSafetyResult)(method.Invoke(
            null,
            [manifest, tampered, tampered.SourceIdentity, tampered.LocalTargetIdentity, tampered.NasTargetIdentity, true]) ??
            throw new InvalidOperationException("Safety result was null."));

        Assert.False(safety.SafeToRemoveCard);
        Assert.Contains("ALL_INCLUDED_FILES_ACCOUNTED_FOR", safety.UnmetConditions);
    }
    [Fact]
    public void Mounted_volume_probe_skips_wrong_external_drive_until_an_approved_file_exists()
    {
        string root = Path.Combine(Path.GetTempPath(), $"AutoCardSync-Candidate-{Guid.NewGuid():N}");
        string approved = Path.Combine(root, "XDROOT", "Clip");
        Directory.CreateDirectory(approved);
        try
        {
            File.WriteAllText(Path.Combine(approved, "ignored.txt"), "not approved");
            var configuration = new StandaloneConfiguration
            {
                ApprovedSourceDirectories = [@"XDROOT\Clip"],
                ApprovedExtensions = [".mxf", ".xml"],
                LocalTargetPath = @"D:\local",
                NasMappedTargetPath = @"Z:\nas",
                TargetNamingRule = TargetNamingRule.ImportDate,
                AutoStartOnLogin = false,
            };
            MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
                "ContainsApprovedCandidateFile",
                BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: [typeof(string), typeof(StandaloneConfiguration)],
                modifiers: null) ??
                throw new InvalidOperationException("Missing mounted-volume candidate probe.");

            Assert.False((bool)(method.Invoke(null, [root, configuration]) ?? true));

            File.WriteAllText(Path.Combine(approved, "clip.MXF"), "approved");
            Assert.True((bool)(method.Invoke(null, [root, configuration]) ?? false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Removing_a_safely_completed_volume_clears_same_guid_suppression()
    {
        string root = Path.Combine(Path.GetTempPath(), $"AutoCardSync-Removal-{Guid.NewGuid():N}");
        var paths = new AutoCardSync.Standalone.Core.StandaloneDataPaths(root);
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
        var runtime = new StandaloneRuntimeService(
            new StandaloneConfigurationService(store, new TestLoginAutoStartService()),
            store,
            paths,
            NullLogger<StandaloneRuntimeService>.Instance);
        var volume = new VolumeEventArgs(@"E:\", "volume-guid", "exFAT", 1, DateTimeOffset.UtcNow);
        try
        {
            GetSafeCompletedVolumes(runtime).Add(GetVolumeKey(volume));
            SetPrivateField(runtime, "_activeVolumeKey", GetVolumeKey(volume));
            SetPrivateField(runtime, "_lastArrivedVolume", volume);
            MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
                "OnVolumeRemoved",
                BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("Missing volume removal handler.");

            method.Invoke(runtime, [null, volume]);

            Assert.DoesNotContain(GetVolumeKey(volume), GetSafeCompletedVolumes(runtime));
            Assert.Null(GetPrivateField(runtime, "_activeVolumeKey"));
            Assert.Null(GetPrivateField(runtime, "_lastArrivedVolume"));
        }
        finally
        {
            await runtime.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Empty_manifest_is_rejected_before_any_copy_can_start()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.Freeze();
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "EnsureManifestContainsIncludedFiles",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing empty-manifest guard.");

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [manifest]));

        InvalidDataException failure = Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("没有符合已批准文件类型", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Safety_rejects_an_empty_manifest_even_when_all_vacuous_checks_are_true()
    {
        var manifest = new TaskManifest(Guid.NewGuid());
        manifest.Freeze();
        var journal = new StandaloneTaskJournal
        {
            TaskId = manifest.TaskId,
            CardInstanceId = Guid.NewGuid(),
            SourceIdentity = "source",
            ManifestHash = manifest.ManifestHash,
            LocalTargetIdentity = "local",
            LocalTargetRoot = @"D:\local",
            NasTargetIdentity = "nas",
            NasTargetRoot = @"\\nas\share\task",
            LocalCompletionReceiptPersisted = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files = [],
        };
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "BuildSafety",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing safety builder.");

        var safety = (StandaloneSafetyResult)(method.Invoke(
            null,
            [manifest, journal, "source", "local", "nas", true]) ??
            throw new InvalidOperationException("Safety result was null."));

        Assert.False(safety.SafeToRemoveCard);
        Assert.Contains("ALL_INCLUDED_FILES_ACCOUNTED_FOR", safety.UnmetConditions);
    }
    private static void SetPrivateField(object instance, string name, object? value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException($"Missing private field {name}.");
        field.SetValue(instance, value);
    }

    private static string GetVolumeKey(VolumeEventArgs volume)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "VolumeKey", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing mounted volume key method.");
        return Assert.IsType<string>(method.Invoke(null, [volume]));
    }
    private static HashSet<string> GetSafeCompletedVolumes(object instance) =>
        Assert.IsType<HashSet<string>>(GetPrivateField(instance, "_safeCompletedVolumeKeys"));

    private static object? GetPrivateField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException($"Missing private field {name}.");
        return field.GetValue(instance);
    }

    private static StandaloneTaskJournal CreateVerifiedJournal(TaskManifest manifest)
    {
        ManifestEntry entry = Assert.Single(manifest.Entries);
        string hash = entry.SourceHash!;
        return new StandaloneTaskJournal
        {
            TaskId = manifest.TaskId,
            CardInstanceId = Guid.NewGuid(),
            SourceIdentity = "source",
            ManifestHash = manifest.ManifestHash,
            LocalTargetIdentity = "local",
            LocalTargetRoot = @"D:\local",
            NasTargetIdentity = "nas",
            NasTargetRoot = @"\\nas\share\task",
            LocalCompletionReceiptPersisted = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new StandaloneFileJournal
                {
                    FileId = entry.Id,
                    RelativePath = entry.RelativePath,
                    Length = entry.FileSize,
                    SourceSha256 = hash,
                    State = StandaloneFileState.Verified,
                    LocalTarget = VerifiedTarget("local", hash),
                    NasTarget = VerifiedTarget("nas", hash),
                },
            ],
        };
    }

    private static StandaloneTargetFileJournal VerifiedTarget(string role, string hash) => new()
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
}
