using System.Diagnostics;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Standalone.Core.Tests.FileSystem;

public sealed class TargetWriteAccessProbeTests
{
    [Fact]
    public void Verify_rejects_selected_path_replacement_or_completes_on_unchanged_bound_directory()
    {
        using var fixture = new ProbeFixture();
        Directory.CreateDirectory(fixture.SelectedDirectory);
        FileIdentity openedIdentity = FileIdentity.GetFileIdentity(fixture.SelectedDirectory);
        string sourceSentinel = fixture.CreateSimulatedSourceSentinel();
        bool validatorCalled = false;
        bool observedInBoundDirectory = false;
        bool observedInReplacementDirectory = false;
        Exception? exchangeFailure = null;

        Exception? verifyFailure = Record.Exception(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                fixture.SelectedDirectory,
                (handle, path) =>
                {
                    AssertOpenedDirectory(handle, path, fixture.SelectedDirectory);
                    validatorCalled = true;
                },
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterDirectoryValidated = () =>
                    {
                        exchangeFailure = TryMoveDirectory(
                            fixture.SelectedDirectory,
                            fixture.OpenedDirectory);
                        if (exchangeFailure is null)
                            Directory.CreateDirectory(fixture.SelectedDirectory);
                    },
                    AfterProbeOpened = probeName =>
                    {
                        string boundDirectory = exchangeFailure is null
                            ? fixture.OpenedDirectory
                            : fixture.SelectedDirectory;
                        observedInBoundDirectory = ContainsEntry(boundDirectory, probeName);
                        observedInReplacementDirectory = exchangeFailure is null &&
                            ContainsEntry(fixture.SelectedDirectory, probeName);
                    },
                }));

        Assert.True(validatorCalled);
        Assert.False(observedInReplacementDirectory);
        if (exchangeFailure is null)
        {
            Assert.IsAssignableFrom<IOException>(verifyFailure);
            Assert.False(observedInBoundDirectory);
            Assert.Empty(Directory.EnumerateFiles(fixture.OpenedDirectory));
            Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
        }
        else
        {
            Assert.Null(verifyFailure);
            Assert.True(exchangeFailure is IOException or UnauthorizedAccessException);
            Assert.True(observedInBoundDirectory);
            Assert.Equal(openedIdentity, FileIdentity.GetFileIdentity(fixture.SelectedDirectory));
            Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
        }
        fixture.AssertSimulatedSourceUnchanged(sourceSentinel);
    }

    [Fact]
    public void Verify_rejects_parent_path_replacement_or_completes_on_unchanged_bound_directory()
    {
        using var fixture = new ProbeFixture();
        string selectedParent = Path.Combine(fixture.Root, "selected-parent");
        string openedParent = Path.Combine(fixture.Root, "opened-parent");
        string selectedDirectory = Path.Combine(selectedParent, "target");
        string openedDirectory = Path.Combine(openedParent, "target");
        Directory.CreateDirectory(selectedDirectory);
        FileIdentity openedIdentity = FileIdentity.GetFileIdentity(selectedDirectory);
        string sourceSentinel = fixture.CreateSimulatedSourceSentinel();
        bool observedInBoundDirectory = false;
        bool observedInReplacementDirectory = false;
        Exception? exchangeFailure = null;

        Exception? verifyFailure = Record.Exception(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                selectedDirectory,
                static (_, _) => { },
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterDirectoryValidated = () =>
                    {
                        exchangeFailure = TryMoveDirectory(selectedParent, openedParent);
                        if (exchangeFailure is null)
                            Directory.CreateDirectory(selectedDirectory);
                    },
                    AfterProbeOpened = probeName =>
                    {
                        string boundDirectory = exchangeFailure is null
                            ? openedDirectory
                            : selectedDirectory;
                        observedInBoundDirectory = ContainsEntry(boundDirectory, probeName);
                        observedInReplacementDirectory = exchangeFailure is null &&
                            ContainsEntry(selectedDirectory, probeName);
                    },
                }));

        Assert.False(observedInReplacementDirectory);
        if (exchangeFailure is null)
        {
            Assert.IsAssignableFrom<IOException>(verifyFailure);
            Assert.False(observedInBoundDirectory);
            Assert.Empty(Directory.EnumerateFiles(openedDirectory));
            Assert.Empty(Directory.EnumerateFiles(selectedDirectory));
        }
        else
        {
            Assert.Null(verifyFailure);
            Assert.True(exchangeFailure is IOException or UnauthorizedAccessException);
            Assert.True(observedInBoundDirectory);
            Assert.Equal(openedIdentity, FileIdentity.GetFileIdentity(selectedDirectory));
            Assert.Empty(Directory.EnumerateFiles(selectedDirectory));
        }
        fixture.AssertSimulatedSourceUnchanged(sourceSentinel);
    }

    [Fact]
    public void Verify_cleanup_deletes_opened_probe_object_without_following_replaced_path()
    {
        using var fixture = new ProbeFixture();
        Directory.CreateDirectory(fixture.SelectedDirectory);
        FileIdentity openedIdentity = FileIdentity.GetFileIdentity(fixture.SelectedDirectory);
        string simulatedSource = fixture.CreateSimulatedSourceDirectory();
        string? probeName = null;
        byte[] decoy = [0x41, 0x43, 0x53];
        string? decoyPath = null;
        Exception? exchangeFailure = null;

        Exception? verifyFailure = Record.Exception(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                fixture.SelectedDirectory,
                static (_, _) => { },
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterProbeOpened = openedProbeName =>
                    {
                        probeName = openedProbeName;
                        exchangeFailure = TryMoveDirectory(
                            fixture.SelectedDirectory,
                            fixture.OpenedDirectory);
                        if (exchangeFailure is null)
                        {
                            Directory.CreateDirectory(fixture.SelectedDirectory);
                            decoyPath = Path.Combine(fixture.SelectedDirectory, openedProbeName);
                        }
                        else
                        {
                            decoyPath = Path.Combine(simulatedSource, openedProbeName);
                        }
                        File.WriteAllBytes(decoyPath, decoy);
                    },
                }));

        Assert.NotNull(probeName);
        Assert.NotNull(decoyPath);
        if (exchangeFailure is null)
        {
            Assert.IsAssignableFrom<IOException>(verifyFailure);
            Assert.False(File.Exists(Path.Combine(fixture.OpenedDirectory, probeName)));
        }
        else
        {
            Assert.Null(verifyFailure);
            Assert.True(exchangeFailure is IOException or UnauthorizedAccessException);
            Assert.Equal(openedIdentity, FileIdentity.GetFileIdentity(fixture.SelectedDirectory));
            Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
        }
        Assert.Equal(decoy, File.ReadAllBytes(decoyPath));
    }

    [Fact]
    public void Verify_stops_before_probe_creation_when_opened_directory_validator_rejects_source()
    {
        using var fixture = new ProbeFixture();
        Directory.CreateDirectory(fixture.SelectedDirectory);
        bool probeOpened = false;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                fixture.SelectedDirectory,
                static (_, _) => throw new InvalidOperationException("opened directory is mounted source"),
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterProbeOpened = _ => probeOpened = true,
                }));

        Assert.Contains("mounted source", exception.Message, StringComparison.Ordinal);
        Assert.False(probeOpened);
        Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
    }

    [Fact]
    public void Verify_delete_on_close_cleans_probe_when_write_stage_faults()
    {
        using var fixture = new ProbeFixture();
        Directory.CreateDirectory(fixture.SelectedDirectory);
        string sourceSentinel = fixture.CreateSimulatedSourceSentinel();
        string? probeName = null;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                fixture.SelectedDirectory,
                static (_, _) => { },
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterProbeOpened = openedProbeName =>
                    {
                        probeName = openedProbeName;
                        throw new InvalidOperationException("synthetic write-stage failure");
                    },
                }));

        Assert.Contains("write-stage failure", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(probeName);
        Assert.False(ContainsEntry(fixture.SelectedDirectory, probeName));
        Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
        fixture.AssertSimulatedSourceUnchanged(sourceSentinel);
    }

    [Fact]
    public void EnsureNoReparsePointComponents_rejects_initial_junction_component()
    {
        using var fixture = new ProbeFixture();
        string parent = Path.Combine(fixture.Root, "junction-parent");
        string selectedDirectory = Path.Combine(parent, "target");
        Directory.CreateDirectory(selectedDirectory);
        var inspected = new List<string>();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            TargetWriteAccessProbe.EnsureNoReparsePointComponents(
                selectedDirectory,
                path =>
                {
                    inspected.Add(path);
                    return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
                        ? FileAttributes.Directory | FileAttributes.ReparsePoint
                        : FileAttributes.Directory;
                }));

        Assert.Contains("reparse-point", exception.Message, StringComparison.Ordinal);
        Assert.Contains(parent, inspected, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(selectedDirectory, inspected, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(selectedDirectory));
    }

    [Fact]
    public void Verify_rejects_real_junction_inserted_between_initial_scan_and_directory_open()
    {
        using var fixture = new ProbeFixture();
        string selectedParent = Path.Combine(fixture.Root, "selected-parent");
        string openedParent = Path.Combine(fixture.Root, "opened-parent");
        string selectedDirectory = Path.Combine(selectedParent, "target");
        string openedDirectory = Path.Combine(openedParent, "target");
        string simulatedSource = fixture.CreateSimulatedSourceDirectory();
        string redirectedDirectory = Path.Combine(simulatedSource, "target");
        Directory.CreateDirectory(selectedDirectory);
        Directory.CreateDirectory(redirectedDirectory);
        string sourceSentinel = fixture.CreateSimulatedSourceSentinel();
        Exception? junctionFailure = null;
        bool probeOpened = false;

        Exception? exception;
        try
        {
            exception = Record.Exception(() =>
                TargetWriteAccessProbe.VerifyForTesting(
                    selectedDirectory,
                    static (_, _) => { },
                    new TargetWriteAccessProbeFaultInjection
                    {
                        AfterInitialPathValidation = () =>
                        {
                            Directory.Move(selectedParent, openedParent);
                            junctionFailure = TryCreateJunction(selectedParent, simulatedSource);
                        },
                        AfterProbeOpened = _ => probeOpened = true,
                    }));

            Assert.NotNull(exception);
            Assert.False(probeOpened);
            if (junctionFailure is null)
            {
                Assert.IsType<InvalidDataException>(exception);
                Assert.Contains("reparse-point", exception.Message, StringComparison.Ordinal);
            }
            else
            {
                Console.WriteLine($"JUNCTION_CREATION_BLOCKED: {junctionFailure.Message}");
                Assert.True(junctionFailure is IOException or UnauthorizedAccessException);
                Assert.IsAssignableFrom<IOException>(exception);
            }

            Assert.Empty(Directory.EnumerateFiles(openedDirectory));
            Assert.Empty(Directory.EnumerateFiles(redirectedDirectory));
            fixture.AssertSimulatedSourceUnchanged(sourceSentinel);
        }
        finally
        {
            DeleteJunctionIfPresent(selectedParent);
        }
    }

    [Fact]
    public void Network_identity_validator_rejects_local_directory_before_probe_creation()
    {
        using var fixture = new ProbeFixture();
        Directory.CreateDirectory(fixture.SelectedDirectory);
        bool probeOpened = false;
        var expectedNetwork = new NetworkStorageIdentity(
            @"\\remote-server\media",
            "remote-server",
            "media",
            1,
            "fileid128:00000000000000000000000000000001",
            0x00020000,
            3,
            1,
            1,
            0);

        Exception? exception = Record.Exception(() =>
            TargetWriteAccessProbe.VerifyForTesting(
                fixture.SelectedDirectory,
                (handle, path) => new NetworkStorageIdentityResolver()
                    .EnsureOpenedRootHandleMatches(handle, expectedNetwork, path),
                new TargetWriteAccessProbeFaultInjection
                {
                    AfterProbeOpened = _ => probeOpened = true,
                }));

        Assert.NotNull(exception);
        Assert.True(exception is IOException or InvalidOperationException);
        Assert.False(probeOpened);
        Assert.Empty(Directory.EnumerateFiles(fixture.SelectedDirectory));
    }

    [Fact]
    public void MainWindow_binds_each_storage_purpose_to_opened_physical_identity_before_probe()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AutoCardSync.Standalone",
            "MainWindow.xaml.cs"));
        int methodStart = source.IndexOf(
            "private void RejectMountedCardOverlap(",
            StringComparison.Ordinal);
        int methodEnd = source.IndexOf(
            "private static void RejectReparsePointPath",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        string validation = source[methodStart..methodEnd];

        Assert.Contains(
            "\"localTarget\" => faultDomains.ResolveLocalDomain(openedTargetDirectory, selectedPath)",
            validation,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"nasMappedTarget\" => ResolveOpenedMappedNasTarget(",
            validation,
            StringComparison.Ordinal);
        Assert.Contains("faultDomains.ResolveNasDomain(", validation, StringComparison.Ordinal);
        Assert.Contains("EnsureOpenedRootHandleMatches(", validation, StringComparison.Ordinal);
        Assert.Contains("faultDomains.AreSameFaultDomain(", validation, StringComparison.Ordinal);
    }

    private static void AssertOpenedDirectory(
        SafeFileHandle handle,
        string displayPath,
        string expectedPath)
    {
        Assert.False(handle.IsInvalid);
        Assert.False(handle.IsClosed);
        Assert.Equal(Path.GetFullPath(expectedPath), Path.GetFullPath(displayPath));
        Assert.Equal(
            FileIdentity.GetFileIdentity(expectedPath),
            FileIdentity.GetFileIdentity(handle, displayPath));
    }

    private static bool ContainsEntry(string directory, string fileName) =>
        Directory.EnumerateFileSystemEntries(directory)
            .Any(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal));

    private static Exception? TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    private static Exception? TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            string commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = commandProcessor,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"mklink /J \"{linkPath}\" \"{targetPath}\"");
            using Process process = Process.Start(startInfo) ??
                throw new IOException("The junction helper process did not start.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return new IOException(
                    $"Windows rejected synthetic junction creation with exit code {process.ExitCode}: " +
                    $"{standardOutput} {standardError}".Trim());
            }
            if (!Directory.Exists(linkPath) ||
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) == 0)
            {
                return new IOException("Windows reported junction success without a reparse-point directory.");
            }
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return exception;
        }
    }

    private static void DeleteJunctionIfPresent(string path)
    {
        if (!Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path, recursive: false);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AutoCardSync.Standalone.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }

    private sealed class ProbeFixture : IDisposable
    {
        public ProbeFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"autocardsync-write-probe-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string SelectedDirectory => Path.Combine(Root, "selected");
        public string OpenedDirectory => Path.Combine(Root, "opened");

        public string CreateSimulatedSourceDirectory()
        {
            string path = Path.Combine(Root, "simulated-source");
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateSimulatedSourceSentinel()
        {
            string path = Path.Combine(CreateSimulatedSourceDirectory(), "source.bin");
            File.WriteAllBytes(path, [0x53, 0x4F, 0x55, 0x52, 0x43, 0x45]);
            return path;
        }

        public void AssertSimulatedSourceUnchanged(string sentinelPath) =>
            Assert.Equal([0x53, 0x4F, 0x55, 0x52, 0x43, 0x45], File.ReadAllBytes(sentinelPath));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
