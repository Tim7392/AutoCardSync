using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Storage;

namespace AutoCardSync.Standalone.Core.Tests.Storage;

public sealed class MappedNetworkTargetResolverTests
{
    [Fact]
    public void Capture_resolves_drive_to_unc_and_freezes_physical_identity()
    {
        var mapping = new MutableMapping(@"\\nas-a\media");
        var identity = new FakeNetworkIdentityResolver(@"\\nas-physical\media\Project");
        var resolver = new MappedNetworkTargetResolver(mapping, identity);

        MappedNetworkTargetIdentity result = resolver.Capture(@"Z:\Project");

        Assert.Equal("Z:", result.MappedDriveName);
        Assert.Equal(@"\\nas-a\media\Project", result.LogicalUncPath);
        Assert.Equal(@"\\nas-physical\media\Project", result.PhysicalUncPath);
        Assert.Equal(@"\\nas-physical\media", result.PhysicalEndpoint);
        Assert.Equal(identity.LastIdentity!.StorageIdentity, result.StorageIdentity);
    }

    [Fact]
    public void EnsureUnchanged_rejects_drive_remap()
    {
        var mapping = new MutableMapping(@"\\nas-a\media");
        var identity = new FakeNetworkIdentityResolver(@"\\nas-physical\media\Project");
        var resolver = new MappedNetworkTargetResolver(mapping, identity);
        MappedNetworkTargetIdentity frozen = resolver.Capture(@"Z:\Project");
        mapping.RemoteRoot = @"\\nas-b\media";

        Assert.Throws<IOException>(() => resolver.EnsureUnchanged(frozen));
    }

    [Fact]
    public void EnsureUnchanged_rejects_physical_storage_change()
    {
        var mapping = new MutableMapping(@"\\nas-a\media");
        var identity = new FakeNetworkIdentityResolver(@"\\nas-physical\media\Project");
        var resolver = new MappedNetworkTargetResolver(mapping, identity);
        MappedNetworkTargetIdentity frozen = resolver.Capture(@"Z:\Project");
        identity.VolumeSerialNumber = 43;

        Assert.Throws<IOException>(() => resolver.EnsureUnchanged(frozen));
    }

    [Fact]
    public void Capture_rejects_unc_and_unmapped_drive()
    {
        var resolver = new MappedNetworkTargetResolver(
            new ThrowingMapping(),
            new FakeNetworkIdentityResolver(@"\\nas-physical\media"));

        Assert.Throws<ArgumentException>(() => resolver.Capture(@"\\nas-a\media"));
        Assert.Throws<IOException>(() => resolver.Capture(@"C:\Local"));
    }

    [Fact]
    public void CaptureBoundPath_reconstructs_frozen_subdirectory_from_same_mapping()
    {
        var mapping = new MutableMapping(@"\\nas-a\media");
        var identity = new FakeNetworkIdentityResolver(@"\\nas-physical\media\Project\20260723-task");
        var resolver = new MappedNetworkTargetResolver(mapping, identity);

        MappedNetworkTargetIdentity result = resolver.CaptureBoundPath(
            @"Z:\Project",
            @"\\nas-a\media\Project\20260723-task");

        Assert.Equal(@"Z:\Project\20260723-task", result.ConfiguredPath);
        Assert.Equal(@"\\nas-a\media\Project\20260723-task", result.LogicalUncPath);
    }

    [Fact]
    public void CaptureBoundPath_rejects_frozen_root_after_drive_remap()
    {
        var mapping = new MutableMapping(@"\\nas-b\media");
        var resolver = new MappedNetworkTargetResolver(
            mapping,
            new FakeNetworkIdentityResolver(@"\\nas-b\media\Project"));

        Assert.Throws<IOException>(() => resolver.CaptureBoundPath(
            @"Z:\Project",
            @"\\nas-a\media\Project\20260723-task"));
    }

    private sealed class MutableMapping(string remoteRoot) : IMappedDriveConnectionResolver
    {
        public string RemoteRoot { get; set; } = remoteRoot;
        public string ResolveRemoteRoot(string driveName) => RemoteRoot;
    }

    private sealed class ThrowingMapping : IMappedDriveConnectionResolver
    {
        public string ResolveRemoteRoot(string driveName) =>
            throw new IOException("Drive is not mapped.");
    }

    private sealed class FakeNetworkIdentityResolver(string physicalPath) : INetworkStorageIdentityResolver
    {
        public ulong VolumeSerialNumber { get; set; } = 42;
        public NetworkStorageIdentity? LastIdentity { get; private set; }

        public NetworkStorageIdentity Capture(string uncPath)
        {
            LastIdentity = new NetworkStorageIdentity(
                physicalPath,
                "nas-physical",
                "media",
                VolumeSerialNumber,
                "root-file-id",
                0x00020000,
                3,
                1,
                1,
                0);
            return LastIdentity;
        }

        public void EnsureOpenedHandleMatches(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            NetworkStorageIdentity expected,
            string displayPath) => throw new NotSupportedException();

        public void EnsureOpenedRootHandleMatches(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            NetworkStorageIdentity expected,
            string displayPath) => throw new NotSupportedException();
    }
}
