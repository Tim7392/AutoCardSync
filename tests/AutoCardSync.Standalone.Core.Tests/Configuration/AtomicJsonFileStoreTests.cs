using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Tests.Configuration;

public sealed class AtomicJsonFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AutoCardSync-V1-Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_and_load_round_trip_without_leaving_temporary_objects()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "config.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        StandaloneConfiguration expected = new()
        {
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".jpg", ".mp4"],
            LocalTargetPath = @"C:\AutoCardSync\Local",
            NasMappedTargetPath = @"Z:\AutoCardSync",
            TargetNamingRule = TargetNamingRule.ImportDate,
            AutoStartOnLogin = true,
        };

        await store.SaveAsync(expected, CancellationToken.None);
        StandaloneConfiguration? actual = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(actual);
        AssertConfigurationEqual(expected, actual!);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Save_replaces_existing_configuration_atomically()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "config.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        StandaloneConfiguration first = Create(@"C:\First");
        StandaloneConfiguration second = Create(@"C:\Second");

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);

        StandaloneConfiguration? actual = await store.LoadAsync(CancellationToken.None);
        AssertConfigurationEqual(second, actual!);
    }

    [Fact]
    public void Corrupt_directory_is_preserved_for_audit_before_regeneration()
    {
        string profile = Path.Combine(_root, "WebView2");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "stale-cache.dat"), "cache");

        string backup = CorruptStateFileRecovery.PreserveDirectory(profile);

        Assert.False(Directory.Exists(profile));
        Assert.True(Directory.Exists(backup));
        Assert.True(File.Exists(Path.Combine(backup, "stale-cache.dat")));
        Assert.StartsWith(profile + ".corrupt-", backup, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".bak", backup, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertConfigurationEqual(
        StandaloneConfiguration expected,
        StandaloneConfiguration actual)
    {
        Assert.Equal(expected.ApprovedSourceDirectories, actual.ApprovedSourceDirectories);
        Assert.Equal(expected.ApprovedExtensions, actual.ApprovedExtensions);
        Assert.Equal(expected.LocalTargetPath, actual.LocalTargetPath);
        Assert.Equal(expected.NasMappedTargetPath, actual.NasMappedTargetPath);
        Assert.Equal(expected.TargetNamingRule, actual.TargetNamingRule);
        Assert.Equal(expected.AutoStartOnLogin, actual.AutoStartOnLogin);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static StandaloneConfiguration Create(string localTarget) => new()
    {
        ApprovedSourceDirectories = ["DCIM"],
        ApprovedExtensions = [".jpg"],
        LocalTargetPath = localTarget,
        NasMappedTargetPath = @"Z:\AutoCardSync",
        TargetNamingRule = TargetNamingRule.PreserveRelativePath,
        AutoStartOnLogin = false,
    };
}
