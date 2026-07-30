using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.Storage;

/// <summary>
/// Resolves storage fault domains per FR-007 and design baseline section 9.1.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FaultDomainResolver
{
    private const uint RemoteProtocolFlagLoopback = 0x00000001;
    private const uint RemoteProtocolFlagOffline = 0x00000002;
    private const uint WnncNetSmb = 0x00020000;
    private const string CoRiskDisclaimer =
        "FaultDomainId represents storage fault domain only. Remaining co-risks: shared host, power supply, network switch, site, and administrator permissions.";
    private readonly INetworkStorageIdentityResolver _networkStorageIdentityResolver;
    private readonly Func<string, IPAddress[]> _dnsResolver;
    private readonly Func<string, ulong> _localVolumeSerialResolver;

    public FaultDomainResolver(
        INetworkStorageIdentityResolver? networkStorageIdentityResolver = null,
        Func<string, IPAddress[]>? dnsResolver = null,
        Func<string, ulong>? localVolumeSerialResolver = null)
    {
        _networkStorageIdentityResolver = networkStorageIdentityResolver
            ?? new NetworkStorageIdentityResolver();
        _dnsResolver = dnsResolver ?? Dns.GetHostAddresses;
        _localVolumeSerialResolver = localVolumeSerialResolver ?? GetVolumeSerialNumberForPath;
    }

    /// <summary>
    /// Resolves a local file path to its storage fault domain using the physical disk ID
    /// derived from the volume mount point and disk extent information.
    /// </summary>
    public FaultDomainInfo ResolveLocalDomain(string path)
    {
        var volumeGuid = GetVolumeGuidForPath(path);
        var physicalDiskId = GetPhysicalDiskIdForVolume(volumeGuid);
        if (string.IsNullOrWhiteSpace(physicalDiskId))
        {
            throw new InvalidOperationException(
                $"Unable to resolve a physical disk identity for local volume '{volumeGuid}'.");
        }

        ulong volumeSerialNumber = _localVolumeSerialResolver(path);
        string storageIdentity =
            $"local-v1:{physicalDiskId.Trim().ToLowerInvariant()}:" +
            $"{volumeGuid.Trim().ToLowerInvariant()}:{volumeSerialNumber:X16}";
        return new FaultDomainInfo(
            FaultDomainId: $"local:{physicalDiskId}",
            StorageType: "Local",
            PhysicalDiskId: physicalDiskId,
            VolumeGuid: volumeGuid,
            NasSystemId: null,
            Disclaimer: CoRiskDisclaimer,
            StorageIdentity: storageIdentity,
            VolumeSerialNumber: volumeSerialNumber,
            PhysicalEndpoint: volumeGuid);
    }

    /// <summary>
    /// Resolves a local fault domain from an already-opened directory handle so
    /// reparse points cannot redirect target preparation before identity checks.
    /// </summary>
    public FaultDomainInfo ResolveLocalDomain(SafeFileHandle handle, string displayPath)
    {
        LocalStorageHandleIdentity observed = new LocalStorageIdentityResolver()
            .CaptureOpenedHandle(handle, displayPath);
        string physicalDiskId = observed.PhysicalDiskId.Trim();
        string volumeGuid = observed.VolumeGuid.Trim();
        string storageIdentity =
            $"local-v1:{physicalDiskId.ToLowerInvariant()}:" +
            $"{volumeGuid.ToLowerInvariant()}:{observed.VolumeSerialNumber:X16}";
        return new FaultDomainInfo(
            FaultDomainId: $"local:{physicalDiskId}",
            StorageType: "Local",
            PhysicalDiskId: physicalDiskId,
            VolumeGuid: volumeGuid,
            NasSystemId: null,
            Disclaimer: CoRiskDisclaimer,
            StorageIdentity: storageIdentity,
            VolumeSerialNumber: observed.VolumeSerialNumber,
            PhysicalEndpoint: volumeGuid);
    }

    /// <summary>
    /// Resolves a UNC path from an opened target-root handle. The physical UNC
    /// endpoint, directory FileId, volume serial and remote protocol are bound
    /// into StorageIdentity before any write probe is allowed.
    /// </summary>
    public FaultDomainInfo ResolveNasDomain(string uncPath, string? declaredNasSystemId)
    {
        if (string.IsNullOrWhiteSpace(declaredNasSystemId))
            throw new ArgumentException(
                "A stable NAS system identity is required.", nameof(declaredNasSystemId));

        // Reject an explicitly local logical UNC endpoint before attempting any
        // target access. DFS and aliases still require the opened physical endpoint
        // checks below; this is only an early fail-closed guard.
        (string logicalServer, _) = NetworkStorageIdentityResolver.ParsePhysicalEndpoint(uncPath);
        EnsureNasServerIsRemote(logicalServer);

        NetworkStorageIdentity observed = _networkStorageIdentityResolver.Capture(uncPath);
        EnsureObservedRemoteSmb(observed, uncPath);
        string observedNasSystemId = NormalizeServerName(observed.PhysicalServer);
        string expectedNasSystemId = declaredNasSystemId.Trim();
        if (expectedNasSystemId.StartsWith("nas:", StringComparison.OrdinalIgnoreCase))
            expectedNasSystemId = expectedNasSystemId[4..];
        expectedNasSystemId = NormalizeServerName(expectedNasSystemId);
        if (!string.Equals(expectedNasSystemId, observedNasSystemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configured NAS identity '{declaredNasSystemId}' does not match opened physical SMB server '{observedNasSystemId}'.");
        }

        EnsureNasServerIsRemote(observedNasSystemId);

        return new FaultDomainInfo(
            FaultDomainId: $"nas:{observedNasSystemId}",
            StorageType: "Nas",
            PhysicalDiskId: null,
            VolumeGuid: null,
            NasSystemId: observedNasSystemId,
            Disclaimer: CoRiskDisclaimer,
            StorageIdentity: observed.StorageIdentity,
            VolumeSerialNumber: observed.VolumeSerialNumber,
            PhysicalEndpoint: observed.PhysicalEndpoint);
    }

    private void EnsureNasServerIsRemote(string server)
    {
        string normalizedServer = NormalizeServerName(server);
        HashSet<string> localNames = GetLocalHostNames();
        if (localNames.Contains(normalizedServer))
        {
            throw new InvalidOperationException(
                $"UNC server '{server}' identifies this local host; storage isolation cannot be proven.");
        }

        IPAddress[] resolvedAddresses;
        try
        {
            resolvedAddresses = IPAddress.TryParse(normalizedServer, out IPAddress? directAddress)
                ? [directAddress]
                : _dnsResolver(normalizedServer);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // DNS is advisory here. Windows can open a validated remote SMB handle
            // through NetBIOS or an existing SMB mapping even when DNS resolution is
            // unavailable. Capture() and EnsureObservedRemoteSmb() provide the
            // authoritative fail-closed loopback/offline/protocol proof.
            _ = ex;
            return;
        }

        if (resolvedAddresses.Length == 0)
            return;

        HashSet<string> localAddresses = GetLocalAddressKeys();
        foreach (IPAddress resolvedAddress in resolvedAddresses)
        {
            IPAddress address = NormalizeAddress(resolvedAddress);
            if (IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.None) ||
                address.Equals(IPAddress.Broadcast) ||
                localAddresses.Contains(GetAddressKey(address)))
            {
                throw new InvalidOperationException(
                    $"UNC server '{server}' resolves to this local host; storage isolation cannot be proven.");
            }
        }
    }

    private static void EnsureObservedRemoteSmb(NetworkStorageIdentity observed, string displayPath)
    {
        if (observed.Protocol != WnncNetSmb)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is not an SMB remote protocol path; storage isolation cannot be proven.");
        }

        if ((observed.ProtocolFlags & RemoteProtocolFlagLoopback) != 0)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is a loopback remote protocol path; storage isolation cannot be proven.");
        }

        if ((observed.ProtocolFlags & RemoteProtocolFlagOffline) != 0)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is offline; storage isolation cannot be proven.");
        }
    }

    private static HashSet<string> GetLocalHostNames()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "localhost",
                ".",
                NormalizeServerName(Environment.MachineName),
            };

            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            string hostName = NormalizeServerName(properties.HostName);
            names.Add(hostName);
            if (!string.IsNullOrWhiteSpace(properties.DomainName))
                names.Add(NormalizeServerName($"{hostName}.{properties.DomainName}"));

            return names;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "Local host names could not be enumerated; storage isolation cannot be proven.", ex);
        }
    }

    private static HashSet<string> GetLocalAddressKeys()
    {
        try
        {
            var addresses = new HashSet<string>(StringComparer.Ordinal)
            {
                GetAddressKey(IPAddress.Loopback),
                GetAddressKey(IPAddress.IPv6Loopback),
            };

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation unicastAddress in
                         networkInterface.GetIPProperties().UnicastAddresses)
                {
                    addresses.Add(GetAddressKey(unicastAddress.Address));
                }
            }

            return addresses;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "Local network addresses could not be enumerated; storage isolation cannot be proven.", ex);
        }
    }

    private static string NormalizeServerName(string server) =>
        server.Trim().TrimEnd('.').ToLowerInvariant();

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string GetAddressKey(IPAddress address)
    {
        IPAddress normalizedAddress = NormalizeAddress(address);
        return $"{normalizedAddress.AddressFamily}:{Convert.ToHexString(normalizedAddress.GetAddressBytes())}";
    }

    /// <summary>
    /// Returns true when both fault domain infos belong to the same storage fault domain.
    /// </summary>
    public bool AreSameFaultDomain(FaultDomainInfo a, FaultDomainInfo b)
    {
        if (string.Equals(
                a.FaultDomainId.Trim(),
                b.FaultDomainId.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(a.StorageIdentity) &&
            !string.IsNullOrWhiteSpace(b.StorageIdentity) &&
            string.Equals(a.StorageIdentity.Trim(), b.StorageIdentity.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (a.VolumeSerialNumber is > 0 && b.VolumeSerialNumber is > 0 &&
            a.VolumeSerialNumber.Value == b.VolumeSerialNumber.Value)
        {
            return true;
        }

        if (IsLocal(a) && IsLocal(b) &&
            !string.IsNullOrWhiteSpace(a.PhysicalDiskId) &&
            !string.IsNullOrWhiteSpace(b.PhysicalDiskId))
        {
            return string.Equals(
                a.PhysicalDiskId.Trim(),
                b.PhysicalDiskId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (IsNas(a) && IsNas(b) &&
            !string.IsNullOrWhiteSpace(a.NasSystemId) &&
            !string.IsNullOrWhiteSpace(b.NasSystemId))
        {
            return string.Equals(
                a.NasSystemId.Trim(),
                b.NasSystemId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Validates that primary and backup targets reside in different fault domains.
    /// </summary>
    public DualTargetValidationResult ValidateDualTargetIndependence(
        FaultDomainInfo primary, FaultDomainInfo backup)
    {
        return ValidateIndependence(primary, "Primary", backup, "backup");
    }

    public DualTargetValidationResult ValidateIndependence(
        FaultDomainInfo first,
        string firstLabel,
        FaultDomainInfo second,
        string secondLabel)
    {
        string? identityFailure = GetIdentityFailure(first, firstLabel)
            ?? GetIdentityFailure(second, secondLabel);
        if (identityFailure is not null)
            return new DualTargetValidationResult(IsValid: false, Reason: identityFailure);

        if (IsNas(first) && IsNas(second))
        {
            return new DualTargetValidationResult(
                IsValid: false,
                Reason: "Independence between two NAS targets cannot be proven automatically; " +
                        "use a local physical target plus NAS or provide an approved storage attestation.");
        }

        if (IsLocal(first) && IsLocal(second) &&
            string.Equals(
                first.VolumeGuid!.Trim(),
                second.VolumeGuid!.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return new DualTargetValidationResult(
                IsValid: false,
                Reason: $"{firstLabel} and {secondLabel} share the same volume '{first.VolumeGuid}'.");
        }

        if (AreSameFaultDomain(first, second))
        {
            string sharedIdentity = IsLocal(first) && IsLocal(second)
                ? $"physical disk '{first.PhysicalDiskId}'"
                : $"fault domain '{first.FaultDomainId}'";
            return new DualTargetValidationResult(
                IsValid: false,
                Reason: $"{firstLabel} and {secondLabel} share the same {sharedIdentity}. " +
                        "Isolation requires independent storage fault domains.");
        }

        return new DualTargetValidationResult(IsValid: true, Reason: null);
    }

    private static string? GetIdentityFailure(FaultDomainInfo domain, string label)
    {
        if (string.IsNullOrWhiteSpace(domain.FaultDomainId))
            return $"{label} fault domain identity is missing; isolation cannot be proven.";
        if (string.IsNullOrWhiteSpace(domain.StorageIdentity))
            return $"{label} opened storage identity is missing; isolation cannot be proven.";
        if (!domain.VolumeSerialNumber.HasValue)
            return $"{label} volume serial identity is missing; isolation cannot be proven.";

        if (IsLocal(domain))
        {
            if (string.IsNullOrWhiteSpace(domain.VolumeGuid))
                return $"{label} local volume identity is missing; isolation cannot be proven.";
            if (string.IsNullOrWhiteSpace(domain.PhysicalDiskId))
                return $"{label} local physical disk identity is missing; isolation cannot be proven.";
            return null;
        }

        if (IsNas(domain))
        {
            if (string.IsNullOrWhiteSpace(domain.NasSystemId))
                return $"{label} NAS identity is missing or unverified; isolation cannot be proven.";
            return null;
        }

        return $"{label} storage type '{domain.StorageType}' is unknown; isolation cannot be proven.";
    }

    private static bool IsLocal(FaultDomainInfo domain) =>
        string.Equals(domain.StorageType, "Local", StringComparison.OrdinalIgnoreCase);

    private static bool IsNas(FaultDomainInfo domain) =>
        string.Equals(domain.StorageType, "Nas", StringComparison.OrdinalIgnoreCase);

    // -----------------------------------------------------------------------
    // Native interop helpers
    // -----------------------------------------------------------------------

    private static string GetVolumeGuidForPath(string path)
    {
        // Resolve the root path (e.g. "D:\folder\file.txt" -> "D:\").
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null)
            throw new ArgumentException($"Cannot determine volume root for path: {path}", nameof(path));

        // Use a buffer large enough for a volume name string (\\?\Volume{...}\).
        var sb = new StringBuilder(260);
        if (!GetVolumeNameForVolumeMountPointW(root, sb, (uint)sb.Capacity))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"GetVolumeNameForVolumeMountPoint failed for '{root}' (Win32 error {error}).");
        }

        return sb.ToString().TrimEnd('\\');
    }


    private static ulong GetVolumeSerialNumberForPath(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException($"Cannot determine volume root for path: {path}", nameof(path));
        if (!GetVolumeInformationW(root, null, 0, out uint serialNumber,
                out _, out _, null, 0))
        {
            throw new InvalidOperationException(
                $"GetVolumeInformation failed for '{root}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        // FAT/FAT32 media can legitimately expose a zero volume serial while the
        // volume GUID and physical disk extent remain available. Preserve zero in
        // the frozen storage identity; card-evidence code treats it as absent.
        return serialNumber;
    }

    private static string? GetPhysicalDiskIdForVolume(string volumeGuid)
    {
        using SafeFileHandle volumeHandle = CreateFileW(
            volumeGuid,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            flagsAndAttributes: 0,
            IntPtr.Zero);

        if (volumeHandle.IsInvalid)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to open local volume '{volumeGuid}' for disk extent discovery.");

        const int bufferSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (!DeviceIoControl(
                    volumeHandle,
                    IoctlVolumeGetVolumeDiskExtents,
                    IntPtr.Zero,
                    0,
                    buffer,
                    bufferSize,
                    out _,
                    IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Failed to resolve physical disk extents for local volume '{volumeGuid}'.");
            }

            uint extentCount = unchecked((uint)Marshal.ReadInt32(buffer));
            if (extentCount == 0)
                return null;

            int firstExtentOffset = Marshal.OffsetOf<VolumeDiskExtents>(
                nameof(VolumeDiskExtents.FirstDiskExtent)).ToInt32();
            int extentSize = Marshal.SizeOf<DiskExtent>();
            var diskNumbers = new HashSet<uint>();
            for (int index = 0; index < extentCount; index++)
            {
                long offset = firstExtentOffset + ((long)index * extentSize);
                if (offset + extentSize > bufferSize)
                    return null;

                var extent = Marshal.PtrToStructure<DiskExtent>(IntPtr.Add(buffer, checked((int)offset)));
                diskNumbers.Add(extent.DiskNumber);
            }

            return diskNumbers.Count == 1
                ? @"\\.\PHYSICALDRIVE" + diskNumbers.Single()
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeDiskExtents
    {
        public uint NumberOfDiskExtents;
        public DiskExtent FirstDiskExtent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskExtent
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        int inputBufferSize,
        IntPtr outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        uint fileSystemNameSize);
}

/// <summary>
/// Represents a resolved storage fault domain.
/// </summary>
public sealed record FaultDomainInfo(
    string FaultDomainId,
    string StorageType,
    string? PhysicalDiskId,
    string? VolumeGuid,
    string? NasSystemId,
    string Disclaimer,
    string? StorageIdentity = null,
    ulong? VolumeSerialNumber = null,
    string? PhysicalEndpoint = null);

/// <summary>
/// Result of validating dual-target independence across fault domains.
/// </summary>
public sealed record DualTargetValidationResult(
    bool IsValid,
    string? Reason);
