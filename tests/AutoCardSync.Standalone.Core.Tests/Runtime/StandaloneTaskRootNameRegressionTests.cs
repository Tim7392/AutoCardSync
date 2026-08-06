using System.Reflection;
using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests.Runtime;

/// <summary>
/// Regression contract for deterministic new-task root selection shared by local and NAS targets.
/// </summary>
public sealed class StandaloneTaskRootNameRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-TaskRootName", Guid.NewGuid().ToString("N"));

    [Fact]
    public void New_task_roots_use_sanitized_card_name_minute_format_and_a_shared_collision_suffix()
    {
        string localBase = Path.Combine(_root, "local");
        string nasBase = Path.Combine(_root, "nas");
        const string expectedBaseName = "素材卡 8.4-16：30";
        Directory.CreateDirectory(Path.Combine(localBase, expectedBaseName));
        Directory.CreateDirectory(Path.Combine(nasBase, expectedBaseName));
        var localClock = new DateTime(2026, 8, 4, 16, 30, 0, DateTimeKind.Unspecified);

        (string localRoot, string nasRoot) = ResolveNewTaskRoots(
            localBase,
            nasBase,
            "素材:卡?*",
            new DateTimeOffset(localClock, TimeZoneInfo.Local.GetUtcOffset(localClock)));

        Assert.Equal(Path.Combine(localBase, expectedBaseName + "（2）"), localRoot);
        Assert.Equal(Path.Combine(nasBase, expectedBaseName + "（2）"), nasRoot);
        Assert.DoesNotContain(':', Path.GetFileName(localRoot));
        Assert.DoesNotContain('?', Path.GetFileName(localRoot));
        Assert.DoesNotContain('*', Path.GetFileName(localRoot));
    }

    private static (string LocalRoot, string NasRoot) ResolveNewTaskRoots(
        string localBase,
        string nasBase,
        string cardName,
        DateTimeOffset startedAt)
    {
        Type planner = typeof(StandaloneRuntimeService);
        MethodInfo buildName = planner.GetMethod(
            "BuildTargetFolderName",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(DateTimeOffset)],
            modifiers: null) ??
            throw new MissingMethodException(planner.FullName, "BuildTargetFolderName");
        MethodInfo resolveAvailable = planner.GetMethod(
            "ResolveAvailableTargetFolderName",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string), typeof(string)],
            modifiers: null) ??
            throw new MissingMethodException(planner.FullName, "ResolveAvailableTargetFolderName");

        string baseName = Assert.IsType<string>(buildName.Invoke(null, [cardName, startedAt]));
        string folderName = Assert.IsType<string>(resolveAvailable.Invoke(
            null,
            [baseName, localBase, nasBase]));
        return (Path.Combine(localBase, folderName), Path.Combine(nasBase, folderName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
