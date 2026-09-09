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
    public async Task Save_retries_a_transient_destination_lock_without_losing_the_original()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "config.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        StandaloneConfiguration first = Create(@"C:\First");
        StandaloneConfiguration second = Create(@"C:\Second");
        await store.SaveAsync(first, CancellationToken.None);

        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        Task save = store.SaveAsync(second, CancellationToken.None);
        await Task.Delay(175);
        locked.Dispose();
        await save;

        AssertConfigurationEqual(second, (await store.LoadAsync(CancellationToken.None))!);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Persistent_destination_lock_preserves_original_and_cleans_failed_temporary_file()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "config.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        StandaloneConfiguration first = Create(@"C:\First");
        await store.SaveAsync(first, CancellationToken.None);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                store.SaveAsync(Create(@"C:\Blocked"), CancellationToken.None));
        }

        AssertConfigurationEqual(first, (await store.LoadAsync(CancellationToken.None))!);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Load_removes_only_stale_guid_temporary_files_for_its_own_store()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "config.json");
        string stale = Path.Combine(_root, $".config.json.{Guid.NewGuid():N}.tmp");
        string recent = Path.Combine(_root, $".config.json.{Guid.NewGuid():N}.tmp");
        string unrelated = Path.Combine(_root, $".other.json.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(recent, "recent");
        await File.WriteAllTextAsync(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));

        Assert.Null(await new AtomicJsonFileStore<StandaloneConfiguration>(path)
            .LoadAsync(CancellationToken.None));

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
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
