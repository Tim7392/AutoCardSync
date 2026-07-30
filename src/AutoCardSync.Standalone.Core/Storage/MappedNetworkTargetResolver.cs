using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;

namespace AutoCardSync.Standalone.Core.Storage;

public interface IMappedDriveConnectionResolver
{
    string ResolveRemoteRoot(string driveName);
}

public sealed class WindowsMappedDriveConnectionResolver : IMappedDriveConnectionResolver
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;

    public string ResolveRemoteRoot(string driveName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Mapped drive resolution requires Windows.");
        if (string.IsNullOrWhiteSpace(driveName) || driveName.Length != 2 ||
            !char.IsLetter(driveName[0]) || driveName[1] != ':')
        {
            throw new ArgumentException("A mapped drive name such as 'Z:' is required.", nameof(driveName));
        }

        int capacity = 512;
        while (capacity <= 32 * 1024)
        {
            var remote = new StringBuilder(capacity);
            int length = remote.Capacity;
            int result = WNetGetConnection(driveName, remote, ref length);
            if (result == NoError)
                return SafePathResolver.ResolveUncPath(remote.ToString());
            if (result != ErrorMoreData)
                throw new IOException(
                    $"Mapped drive '{driveName}' could not be resolved to a network target.",
                    new Win32Exception(result));
            capacity = Math.Max(capacity * 2, length + 1);
        }

        throw new IOException($"Mapped drive '{driveName}' returned an invalid network target.");
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(
        string localName,
        StringBuilder remoteName,
        ref int length);
}

public sealed record MappedNetworkTargetIdentity
{
    public required string ConfiguredPath { get; init; }
    public required string MappedDriveName { get; init; }
    public required string LogicalUncPath { get; init; }
    public required string PhysicalUncPath { get; init; }
    public required string PhysicalEndpoint { get; init; }
    public required string PhysicalServer { get; init; }
    public required string StorageIdentity { get; init; }
}

[SupportedOSPlatform("windows")]
public sealed class MappedNetworkTargetResolver
{
    private readonly IMappedDriveConnectionResolver _connectionResolver;
    private readonly INetworkStorageIdentityResolver _identityResolver;

    public MappedNetworkTargetResolver(
        IMappedDriveConnectionResolver? connectionResolver = null,
        INetworkStorageIdentityResolver? identityResolver = null)
    {
        _connectionResolver = connectionResolver ?? new WindowsMappedDriveConnectionResolver();
        _identityResolver = identityResolver ?? new NetworkStorageIdentityResolver();
    }

    public MappedNetworkTargetIdentity Capture(string configuredPath)
    {
        string normalized = ValidateMappedDrivePath(configuredPath);
        string root = Path.GetPathRoot(normalized)!;
        string driveName = root[..2];
        string remoteRoot = _connectionResolver.ResolveRemoteRoot(driveName);
        string relative = Path.GetRelativePath(root, normalized);
        string logicalUncPath = relative == "."
            ? remoteRoot
            : SafePathResolver.ResolveUncPath(Path.Combine(remoteRoot, relative));
        NetworkStorageIdentity identity = _identityResolver.Capture(logicalUncPath);
        return new MappedNetworkTargetIdentity
        {
            ConfiguredPath = normalized,
            MappedDriveName = driveName,
            LogicalUncPath = logicalUncPath,
            PhysicalUncPath = identity.PhysicalUncPath,
            PhysicalEndpoint = identity.PhysicalEndpoint,
            PhysicalServer = identity.PhysicalServer,
            StorageIdentity = identity.StorageIdentity,
        };
    }

    public void EnsureUnchanged(MappedNetworkTargetIdentity frozen)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        MappedNetworkTargetIdentity current = Capture(frozen.ConfiguredPath);
        if (!string.Equals(current.MappedDriveName, frozen.MappedDriveName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.LogicalUncPath, frozen.LogicalUncPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.PhysicalUncPath, frozen.PhysicalUncPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.PhysicalEndpoint, frozen.PhysicalEndpoint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.StorageIdentity, frozen.StorageIdentity, StringComparison.Ordinal))
        {
            throw new IOException("The mapped network target identity changed after it was frozen.");
        }
    }

    public MappedNetworkTargetIdentity CaptureBoundPath(
        string configuredBasePath,
        string expectedLogicalUncPath)
    {
        MappedNetworkTargetIdentity currentBase = Capture(configuredBasePath);
        string expected = SafePathResolver.ResolveUncPath(expectedLogicalUncPath);
        if (!IsSameOrChild(expected, currentBase.LogicalUncPath))
        {
            throw new IOException(
                "The mapped drive no longer resolves to the frozen network target.");
        }

        string relative = Path.GetRelativePath(currentBase.LogicalUncPath, expected);
        string configuredBoundPath = relative == "."
            ? currentBase.ConfiguredPath
            : Path.Combine(currentBase.ConfiguredPath, relative);
        MappedNetworkTargetIdentity current = Capture(configuredBoundPath);
        if (!string.Equals(current.LogicalUncPath, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The mapped drive path no longer resolves to the frozen network target path.");
        }

        return current;
    }

    private static bool IsSameOrChild(string child, string parent)
    {
        string normalizedChild = Path.TrimEndingDirectorySeparator(child);
        string normalizedParent = Path.TrimEndingDirectorySeparator(parent);
        return string.Equals(normalizedChild, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
            normalizedChild.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateMappedDrivePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new ArgumentException("A mapped network target path is required.", nameof(configuredPath));
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath));
        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 ||
            !char.IsLetter(root[0]) || root[1] != ':' || normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("The network target must be configured through a mapped drive letter.", nameof(configuredPath));
        }
        return normalized;
    }
}

