using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.Storage;

/// <summary>
/// Identity captured from an already-opened local file or directory handle.
/// </summary>
public sealed record LocalStorageHandleIdentity(
    string FinalPath,
    string VolumeGuid,
    string PhysicalDiskId,
    ulong VolumeSerialNumber,
    FileIdentity FileIdentity);

/// <summary>
/// Fails closed unless an opened local handle remains on the frozen volume and
/// single physical-disk fault domain represented by a <see cref="FaultDomainInfo"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalStorageIdentityResolver
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint VolumeNameGuid = 0x00000001;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int FileAttributeTagInfo = 9;

    private static readonly Regex PhysicalDiskPattern = new(
        @"^\\\\\.\\PHYSICALDRIVE(?<number>[0-9]+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public LocalStorageHandleIdentity CaptureOpenedHandle(
        SafeFileHandle handle,
        string displayPath)
    {
        EnsureWindowsAndValidHandle(handle, displayPath);
        EnsureNotReparsePoint(handle, displayPath);

        FileIdentity fileIdentity = FileIdentity.GetFileIdentity(handle, displayPath);
        ByHandleFileInformation legacyIdentity = QueryLegacyIdentity(handle, displayPath);
        EnsureUsableFileIdentity(fileIdentity, legacyIdentity, displayPath);

        string finalPath = QueryFinalVolumePath(handle, displayPath);
        string volumeGuid = ExtractVolumeGuid(finalPath, displayPath);
        ulong volumeSerialNumber = QueryVolumeSerialNumber(volumeGuid, displayPath);
        string physicalDiskId = QuerySinglePhysicalDiskId(volumeGuid, displayPath);

        ulong handleVolumeSerial = unchecked((uint)fileIdentity.VolumeSerialNumber);
        if (handleVolumeSerial != volumeSerialNumber ||
            (legacyIdentity.VolumeSerialNumber != 0 &&
             legacyIdentity.VolumeSerialNumber != volumeSerialNumber))
        {
            throw new IOException(
                $"Opened local handle volume serial changed for '{displayPath}': " +
                $"handle '{fileIdentity.VolumeSerialNumber:X16}' " +
                $"(legacy '{legacyIdentity.VolumeSerialNumber:X8}'), " +
                $"volume '{volumeSerialNumber:X16}'.");
        }

        return new LocalStorageHandleIdentity(
            finalPath,
            volumeGuid,
            physicalDiskId,
            volumeSerialNumber,
            fileIdentity);
    }

    public void EnsureOpenedHandleMatches(
        SafeFileHandle handle,
        FaultDomainInfo expected,
        string displayPath)
    {
        FrozenLocalIdentity frozen = ParseFrozenIdentity(expected);
        LocalStorageHandleIdentity observed = CaptureOpenedHandle(handle, displayPath);

        EnsureDisplayPathStillNamesOpenedLeaf(handle, observed.FileIdentity, displayPath);

        if (!string.Equals(
                NormalizeVolumeGuid(observed.VolumeGuid),
                frozen.VolumeGuid,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                NormalizePhysicalDiskId(observed.PhysicalDiskId),
                frozen.PhysicalDiskId,
                StringComparison.OrdinalIgnoreCase) ||
            observed.VolumeSerialNumber != frozen.VolumeSerialNumber)
        {
            throw new IOException(
                $"Opened local target handle storage identity changed for '{displayPath}': " +
                $"expected '{expected.StorageIdentity}', observed " +
                $"'local-v1:{NormalizePhysicalDiskId(observed.PhysicalDiskId)}:" +
                $"{NormalizeVolumeGuid(observed.VolumeGuid)}:{observed.VolumeSerialNumber:X16}' " +
                $"at '{observed.FinalPath}'.");
        }
    }

    private static FrozenLocalIdentity ParseFrozenIdentity(FaultDomainInfo expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (!string.Equals(expected.StorageType, "Local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Frozen storage identity is not a local fault domain.");
        if (string.IsNullOrWhiteSpace(expected.StorageIdentity) ||
            string.IsNullOrWhiteSpace(expected.PhysicalDiskId) ||
            string.IsNullOrWhiteSpace(expected.VolumeGuid) ||
            !expected.VolumeSerialNumber.HasValue)
        {
            throw new InvalidDataException(
                "Frozen local fault domain is missing storage identity, physical disk, volume GUID, or volume serial.");
        }

        string[] parts = expected.StorageIdentity.Split(':', StringSplitOptions.None);
        if (parts.Length != 4 ||
            !string.Equals(parts[0], "local-v1", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(parts[3], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ulong storageSerial))
        {
            throw new InvalidDataException(
                $"Frozen local storage identity has an invalid format: '{expected.StorageIdentity}'.");
        }

        string physicalDiskId = NormalizePhysicalDiskId(expected.PhysicalDiskId);
        string volumeGuid = NormalizeVolumeGuid(expected.VolumeGuid);
        string identityDiskId = NormalizePhysicalDiskId(parts[1]);
        string identityVolumeGuid = NormalizeVolumeGuid(parts[2]);
        ulong volumeSerialNumber = expected.VolumeSerialNumber.Value;

        if (!string.Equals(identityDiskId, physicalDiskId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identityVolumeGuid, volumeGuid, StringComparison.OrdinalIgnoreCase) ||
            storageSerial != volumeSerialNumber)
        {
            throw new InvalidDataException(
                $"Frozen local storage identity fields are inconsistent: '{expected.StorageIdentity}'.");
        }

        if (!string.Equals(
                expected.FaultDomainId,
                $"local:{expected.PhysicalDiskId}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Frozen local fault domain ID is inconsistent with physical disk '{expected.PhysicalDiskId}'.");
        }

        if (!string.IsNullOrWhiteSpace(expected.PhysicalEndpoint) &&
            !string.Equals(
                NormalizeVolumeGuid(expected.PhysicalEndpoint),
                volumeGuid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Frozen local physical endpoint is inconsistent with volume GUID '{expected.VolumeGuid}'.");
        }

        return new FrozenLocalIdentity(physicalDiskId, volumeGuid, volumeSerialNumber);
    }

    private static void EnsureDisplayPathStillNamesOpenedLeaf(
        SafeFileHandle openedHandle,
        FileIdentity openedIdentity,
        string displayPath)
    {
        if (string.IsNullOrWhiteSpace(displayPath))
            throw new ArgumentException("A display path is required for local leaf validation.", nameof(displayPath));

        using SafeFileHandle leafHandle = CreateFileW(
            Path.GetFullPath(displayPath),
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (leafHandle.IsInvalid)
        {
            throw new IOException(
                $"Unable to reopen local target leaf '{displayPath}' without following reparse points.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        EnsureNotReparsePoint(leafHandle, displayPath);
        FileIdentity currentLeafIdentity = FileIdentity.GetFileIdentity(leafHandle, displayPath);
        if (currentLeafIdentity != openedIdentity)
        {
            throw new IOException(
                $"Local target path no longer names the opened object for '{displayPath}': " +
                $"opened '{FormatFileIdentity(openedIdentity)}', current leaf " +
                $"'{FormatFileIdentity(currentLeafIdentity)}'.");
        }

        ByHandleFileInformation openedLegacy = QueryLegacyIdentity(openedHandle, displayPath);
        ByHandleFileInformation leafLegacy = QueryLegacyIdentity(leafHandle, displayPath);
        if (openedLegacy.VolumeSerialNumber != leafLegacy.VolumeSerialNumber ||
            openedLegacy.FileIndexHigh != leafLegacy.FileIndexHigh ||
            openedLegacy.FileIndexLow != leafLegacy.FileIndexLow)
        {
            throw new IOException(
                $"Local target leaf identity changed while validating '{displayPath}'.");
        }
    }

    private static void EnsureWindowsAndValidHandle(SafeFileHandle handle, string displayPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Local storage identity requires Windows handle APIs.");
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException($"Opened local target handle is invalid for '{displayPath}'.");
        if (string.IsNullOrWhiteSpace(displayPath))
            throw new ArgumentException("A display path is required for local identity validation.", nameof(displayPath));
    }

    private static void EnsureNotReparsePoint(SafeFileHandle handle, string displayPath)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfo,
                out FileAttributeTagInformation information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
        {
            throw new IOException(
                $"Unable to query local target attributes for '{displayPath}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Local target leaf is a reparse point and is not allowed: '{displayPath}' " +
                $"(tag 0x{information.ReparseTag:X8}).");
        }
    }

    private static ByHandleFileInformation QueryLegacyIdentity(
        SafeFileHandle handle,
        string displayPath)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new IOException(
                $"Unable to query opened local target identity for '{displayPath}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return information;
    }

    private static void EnsureUsableFileIdentity(
        FileIdentity fileIdentity,
        ByHandleFileInformation legacyIdentity,
        string displayPath)
    {
        if ((legacyIdentity.FileIndexHigh == 0 && legacyIdentity.FileIndexLow == 0) ||
            fileIdentity.VolumeSerialNumber == 0 ||
            (fileIdentity.FileIndexHigh == 0 && fileIdentity.FileIndexLow == 0))
        {
            throw new InvalidDataException(
                $"Opened local target did not expose a stable volume serial and file ID for '{displayPath}': " +
                $"legacy={legacyIdentity.VolumeSerialNumber:X16}:" +
                $"{legacyIdentity.FileIndexHigh:X8}{legacyIdentity.FileIndexLow:X8}, " +
                $"extended={FormatFileIdentity(fileIdentity)}.");
        }
    }

    private static string QueryFinalVolumePath(SafeFileHandle handle, string displayPath)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(
                handle,
                buffer,
                (uint)buffer.Capacity,
                VolumeNameGuid);
            if (length == 0)
            {
                throw new IOException(
                    $"Unable to resolve the final volume path for opened local target '{displayPath}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (length < buffer.Capacity)
                return buffer.ToString();

            buffer = new StringBuilder(checked((int)length + 1));
        }
    }

    private static string ExtractVolumeGuid(string finalPath, string displayPath)
    {
        string normalized = finalPath.Trim().Replace('/', '\\');
        const string prefix = @"\\?\Volume{";
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Opened local target did not resolve to a volume GUID path for '{displayPath}': '{finalPath}'.");
        }

        int closingBrace = normalized.IndexOf('}', prefix.Length);
        if (closingBrace < 0)
        {
            throw new InvalidDataException(
                $"Opened local target returned an invalid volume GUID path for '{displayPath}': '{finalPath}'.");
        }

        return NormalizeVolumeGuid(normalized[..(closingBrace + 1)]);
    }

    private static ulong QueryVolumeSerialNumber(string volumeGuid, string displayPath)
    {
        string root = NormalizeVolumeGuid(volumeGuid) + Path.DirectorySeparatorChar;
        if (!GetVolumeInformationW(root, null, 0, out uint serialNumber,
                out _, out _, null, 0))
        {
            throw new IOException(
                $"Unable to query local volume serial for '{displayPath}' at '{volumeGuid}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (serialNumber == 0)
            throw new InvalidDataException($"Local volume serial is unavailable for '{displayPath}'.");
        return serialNumber;
    }

    private static string QuerySinglePhysicalDiskId(string volumeGuid, string displayPath)
    {
        using SafeFileHandle volumeHandle = CreateFileW(
            NormalizeVolumeGuid(volumeGuid),
            0,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (volumeHandle.IsInvalid)
        {
            throw new IOException(
                $"Unable to open local volume '{volumeGuid}' for '{displayPath}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        const int bufferSize = 65536;
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
                throw new IOException(
                    $"Unable to query physical disk extents for local volume '{volumeGuid}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            uint extentCount = unchecked((uint)Marshal.ReadInt32(buffer));
            if (extentCount == 0)
                throw new InvalidDataException($"Local volume '{volumeGuid}' has no physical disk extents.");

            int firstExtentOffset = Marshal.OffsetOf<VolumeDiskExtents>(
                nameof(VolumeDiskExtents.FirstDiskExtent)).ToInt32();
            int extentSize = Marshal.SizeOf<DiskExtent>();
            var diskNumbers = new HashSet<uint>();
            for (int index = 0; index < extentCount; index++)
            {
                long offset = firstExtentOffset + ((long)index * extentSize);
                if (offset < 0 || offset + extentSize > bufferSize)
                {
                    throw new InvalidDataException(
                        $"Local volume '{volumeGuid}' returned an invalid disk extent list.");
                }

                DiskExtent extent = Marshal.PtrToStructure<DiskExtent>(
                    IntPtr.Add(buffer, checked((int)offset)));
                diskNumbers.Add(extent.DiskNumber);
            }

            if (diskNumbers.Count != 1)
            {
                throw new InvalidDataException(
                    $"Local volume '{volumeGuid}' spans {diskNumbers.Count} physical disks; " +
                    "a single frozen local fault domain cannot be proven.");
            }

            return @"\\.\PHYSICALDRIVE" + diskNumbers.Single();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string NormalizePhysicalDiskId(string physicalDiskId)
    {
        string value = physicalDiskId.Trim().Replace('/', '\\');
        Match match = PhysicalDiskPattern.Match(value);
        if (!match.Success ||
            !uint.TryParse(match.Groups["number"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out uint diskNumber))
        {
            throw new InvalidDataException(
                $"Local physical disk identity has an invalid format: '{physicalDiskId}'.");
        }

        return @"\\.\physicaldrive" + diskNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeVolumeGuid(string volumeGuid)
    {
        string value = volumeGuid.Trim().Replace('/', '\\').TrimEnd('\\');
        const string prefix = @"\\?\Volume{";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith('}'))
        {
            throw new InvalidDataException(
                $"Local volume GUID has an invalid format: '{volumeGuid}'.");
        }

        string guidText = value[prefix.Length..^1];
        if (!Guid.TryParse(guidText, out Guid guid))
            throw new InvalidDataException($"Local volume GUID is invalid: '{volumeGuid}'.");

        return $@"\\?\volume{{{guid:D}}}";
    }

    private static string FormatFileIdentity(FileIdentity identity) =>
        $"{identity.VolumeSerialNumber:X16}:" +
        $"{identity.FileIndexHigh:X16}{identity.FileIndexLow:X16}";

    private sealed record FrozenLocalIdentity(
        string PhysicalDiskId,
        string VolumeGuid,
        ulong VolumeSerialNumber);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

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
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle fileHandle,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        out FileAttributeTagInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
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
