using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.Storage;

public sealed record NetworkStorageIdentity(
    string PhysicalUncPath,
    string PhysicalServer,
    string PhysicalShare,
    ulong VolumeSerialNumber,
    string RootFileId,
    uint Protocol,
    ushort ProtocolMajorVersion,
    ushort ProtocolMinorVersion,
    ushort ProtocolRevision,
    uint ProtocolFlags)
{
    public string StorageIdentity =>
        $"nas-v1:{PhysicalServer.ToLowerInvariant()}/{PhysicalShare.ToLowerInvariant()}:" +
        $"{VolumeSerialNumber:X16}:{RootFileId}:{Protocol:X8}:" +
        $"{ProtocolMajorVersion}.{ProtocolMinorVersion}.{ProtocolRevision}:{ProtocolFlags:X8}";

    public string PhysicalEndpoint => $@"\\{PhysicalServer}\{PhysicalShare}";
}

public interface INetworkStorageIdentityResolver
{
    NetworkStorageIdentity Capture(string uncPath);
}

[SupportedOSPlatform("windows")]
public sealed class NetworkStorageIdentityResolver : INetworkStorageIdentityResolver
{
    private const int FileRemoteProtocolInfo = 13;
    private const int FileIdInfo = 18;
    private const int FileNetworkPhysicalNameInformation = 49;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint RemoteProtocolFlagLoopback = 0x00000001;
    private const uint RemoteProtocolFlagOffline = 0x00000002;
    private const uint WnncNetSmb = 0x00020000;

    public NetworkStorageIdentity Capture(string uncPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Network storage identity requires Windows handle APIs.");

        string canonicalPath = SafePathResolver.ResolveUncPath(uncPath);
        using SafeFileHandle handle = CreateFileW(
            canonicalPath,
            FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Unable to open UNC target root '{canonicalPath}' for physical identity resolution.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        FileIdentity.EnsurePathStillNamesOpenedObject(handle, canonicalPath);

        string physicalPath = QueryPhysicalPath(handle, canonicalPath);
        (string physicalServer, string physicalShare) = ParsePhysicalEndpoint(physicalPath);
        (ulong volumeSerialNumber, string rootFileId) = QueryFileIdentity(handle, canonicalPath);
        (uint protocol, ushort major, ushort minor, ushort revision, uint flags) =
            QueryRemoteProtocol(handle, canonicalPath);

        ValidateRemoteProtocol(protocol, flags, canonicalPath);

        return new NetworkStorageIdentity(
            physicalPath,
            physicalServer,
            physicalShare,
            volumeSerialNumber,
            rootFileId,
            protocol,
            major,
            minor,
            revision,
            flags);
    }

    public void EnsureOpenedHandleMatches(
        SafeFileHandle handle,
        NetworkStorageIdentity expected,
        string displayPath)
        => EnsureOpenedHandleMatchesCore(handle, expected, displayPath, requireExactRoot: false);

    public void EnsureOpenedRootHandleMatches(
        SafeFileHandle handle,
        NetworkStorageIdentity expected,
        string displayPath)
        => EnsureOpenedHandleMatchesCore(handle, expected, displayPath, requireExactRoot: true);

    private static void EnsureOpenedHandleMatchesCore(
        SafeFileHandle handle,
        NetworkStorageIdentity expected,
        string displayPath,
        bool requireExactRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Network storage identity requires Windows handle APIs.");
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException($"Opened target handle is invalid for '{displayPath}'.");

        FileIdentity.EnsurePathStillNamesOpenedObject(handle, displayPath);

        string physicalPath = QueryPhysicalPath(handle, displayPath);
        (string physicalServer, string physicalShare) = ParsePhysicalEndpoint(physicalPath);
        (ulong volumeSerialNumber, string fileId) = QueryFileIdentity(handle, displayPath);
        (uint protocol, ushort major, ushort minor, ushort revision, uint flags) =
            QueryRemoteProtocol(handle, displayPath);
        ValidateRemoteProtocol(protocol, flags, displayPath);

        string expectedRoot = NormalizePhysicalPath(expected.PhysicalUncPath).TrimEnd('\\');
        string actualPath = NormalizePhysicalPath(physicalPath).TrimEnd('\\');
        bool pathMatches = requireExactRoot
            ? string.Equals(actualPath, expectedRoot, StringComparison.OrdinalIgnoreCase)
            : string.Equals(actualPath, expectedRoot, StringComparison.OrdinalIgnoreCase) ||
              actualPath.StartsWith(
                  expectedRoot + Path.DirectorySeparatorChar,
                  StringComparison.OrdinalIgnoreCase);
        if (!pathMatches ||
            !string.Equals(physicalServer, expected.PhysicalServer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(physicalShare, expected.PhysicalShare, StringComparison.OrdinalIgnoreCase) ||
            volumeSerialNumber != expected.VolumeSerialNumber ||
            (requireExactRoot && !string.Equals(fileId, expected.RootFileId, StringComparison.Ordinal)) ||
            protocol != expected.Protocol ||
            major != expected.ProtocolMajorVersion ||
            minor != expected.ProtocolMinorVersion ||
            revision != expected.ProtocolRevision ||
            flags != expected.ProtocolFlags)
        {
            string scope = requireExactRoot ? "at the frozen root" : $"under '{expectedRoot}'";
            throw new IOException(
                $"Opened target handle storage identity changed for '{displayPath}': " +
                $"expected '{expected.StorageIdentity}' {scope}, " +
                $"observed '{physicalServer}/{physicalShare}:{volumeSerialNumber:X16}:{fileId}:" +
                $"{protocol:X8}:{major}.{minor}.{revision}:{flags:X8}' at '{actualPath}'.");
        }
    }

    private static void ValidateRemoteProtocol(uint protocol, uint flags, string displayPath)
    {
        if (protocol != WnncNetSmb)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is not backed by the required SMB remote protocol (0x{protocol:X8}).");
        }
        if ((flags & RemoteProtocolFlagLoopback) != 0)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is a loopback remote protocol path; storage isolation cannot be proven.");
        }
        if ((flags & RemoteProtocolFlagOffline) != 0)
        {
            throw new InvalidOperationException(
                $"UNC target '{displayPath}' is served from an offline client cache; storage identity cannot be proven.");
        }
    }

    public static string MapLogicalPathToPhysicalRoot(
        string logicalRoot,
        string physicalRoot,
        string logicalPath)
    {
        string normalizedLogicalRoot = Path.GetFullPath(logicalRoot).TrimEnd('\\');
        string normalizedPhysicalRoot = Path.GetFullPath(physicalRoot).TrimEnd('\\');
        string fullLogicalPath = Path.GetFullPath(logicalPath);
        string relative = Path.GetRelativePath(normalizedLogicalRoot, fullLogicalPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
            return normalizedPhysicalRoot;
        return SafePathResolver.ResolveSafePath(normalizedPhysicalRoot, relative);
    }

    public static (string Server, string Share) ParsePhysicalEndpoint(string physicalPath)
    {
        string normalized = NormalizePhysicalPath(physicalPath);
        string[] parts = normalized.TrimStart('\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidDataException(
                $"Network physical name did not expose a server/share endpoint: '{physicalPath}'.");
        }

        return (parts[0].Trim().TrimEnd('.').ToLowerInvariant(), parts[1].Trim().ToLowerInvariant());
    }

    public static string NormalizePhysicalPath(string physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath))
            throw new InvalidDataException("Network physical name is empty.");

        string value = physicalPath.Trim().Replace('/', '\\');
        const string extendedUnc = @"\\?\UNC\";
        const string nativeUnc = @"\??\UNC\";
        const string mupPrefix = @"\Device\Mup\";
        if (value.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[extendedUnc.Length..];
        else if (value.StartsWith(nativeUnc, StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[nativeUnc.Length..];
        else if (value.StartsWith(mupPrefix, StringComparison.OrdinalIgnoreCase))
            value = @"\\" + value[mupPrefix.Length..].TrimStart('\\');
        else if (value.StartsWith(';'))
        {
            int driveSeparator = value.IndexOf(@":\", StringComparison.Ordinal);
            if (driveSeparator < 0 || driveSeparator + 2 >= value.Length)
            {
                throw new InvalidDataException(
                    $"Network physical name has an unsupported redirector prefix: '{physicalPath}'.");
            }
            value = @"\\" + value[(driveSeparator + 2)..].TrimStart('\\');
        }
        else if (value.StartsWith("\\", StringComparison.Ordinal) &&
                 !value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            value = "\\" + value;
        }

        return SafePathResolver.ResolveUncPath(value);
    }

    private static string QueryPhysicalPath(SafeFileHandle handle, string displayPath)
    {
        const int bufferSize = 131072;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int status = NtQueryInformationFile(
                handle, out _, buffer, bufferSize, FileNetworkPhysicalNameInformation);
            if (status < 0)
            {
                throw new IOException(
                    $"FileNetworkPhysicalNameInformation failed for '{displayPath}' with NTSTATUS 0x{status:X8}.");
            }

            int byteLength = Marshal.ReadInt32(buffer);
            if (byteLength <= 0 || (byteLength & 1) != 0 || byteLength > bufferSize - sizeof(int))
            {
                throw new InvalidDataException(
                    $"FileNetworkPhysicalNameInformation returned an invalid length for '{displayPath}'.");
            }

            string raw = Marshal.PtrToStringUni(IntPtr.Add(buffer, sizeof(int)), byteLength / 2)
                ?? throw new InvalidDataException(
                    $"FileNetworkPhysicalNameInformation returned no path for '{displayPath}'.");
            return NormalizePhysicalPath(raw);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (ulong VolumeSerialNumber, string RootFileId) QueryFileIdentity(
        SafeFileHandle handle,
        string displayPath)
    {
        byte[] buffer = new byte[24];
        if (GetFileInformationByHandleEx(handle, FileIdInfo, buffer, (uint)buffer.Length))
        {
            ulong volumeSerialNumber = BitConverter.ToUInt64(buffer, 0);
            if (volumeSerialNumber == 0)
                volumeSerialNumber = QueryVolumeSerialNumber(handle, displayPath);
            byte[] fileId = buffer[8..24];
            if (fileId.All(value => value == 0))
            {
                throw new InvalidDataException(
                    $"UNC target '{displayPath}' did not expose a stable volume and directory FileId.");
            }

            return (volumeSerialNumber, $"fileid128:{Convert.ToHexString(fileId)}");
        }

        int error = Marshal.GetLastWin32Error();
        if (error is not (1 or 50 or 87) ||
            !GetFileInformationByHandle(handle, out ByHandleFileInformation legacy))
        {
            int finalError = error is 1 or 50 or 87 ? Marshal.GetLastWin32Error() : error;
            throw new IOException(
                $"File identity failed for UNC target '{displayPath}'.",
                new Win32Exception(finalError));
        }

        if (legacy.FileIndexHigh == 0 && legacy.FileIndexLow == 0)
        {
            throw new InvalidDataException(
                $"UNC target '{displayPath}' did not expose a stable legacy directory FileId.");
        }

        ulong legacyVolumeSerial = legacy.VolumeSerialNumber != 0
            ? legacy.VolumeSerialNumber
            : QueryVolumeSerialNumber(handle, displayPath);
        return (
            legacyVolumeSerial,
            $"fileindex64:{legacy.FileIndexHigh:X8}{legacy.FileIndexLow:X8}");
    }

    private static ulong QueryVolumeSerialNumber(SafeFileHandle handle, string displayPath)
    {
        const int bufferSize = 4096;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int status = NtQueryVolumeInformationFile(
                handle, out _, buffer, bufferSize, 1);
            if (status < 0)
            {
                throw new IOException(
                    $"FileFsVolumeInformation failed for '{displayPath}' with NTSTATUS 0x{status:X8}.");
            }

            uint volumeSerialNumber = unchecked((uint)Marshal.ReadInt32(buffer, 8));
            if (volumeSerialNumber == 0)
            {
                throw new InvalidDataException(
                    $"UNC target '{displayPath}' did not expose a stable volume serial.");
            }
            return volumeSerialNumber;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (uint Protocol, ushort Major, ushort Minor, ushort Revision, uint Flags)
        QueryRemoteProtocol(SafeFileHandle handle, string displayPath)
    {
        byte[] buffer = new byte[256];
        if (!GetFileInformationByHandleEx(
                handle, FileRemoteProtocolInfo, buffer, (uint)buffer.Length))
        {
            throw new IOException(
                $"FileRemoteProtocolInfo failed for UNC target '{displayPath}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        ushort structureVersion = BitConverter.ToUInt16(buffer, 0);
        ushort structureSize = BitConverter.ToUInt16(buffer, 2);
        if (structureVersion == 0 || structureSize < 20 || structureSize > buffer.Length)
        {
            throw new InvalidDataException(
                $"UNC target '{displayPath}' returned invalid remote protocol information.");
        }

        return (
            BitConverter.ToUInt32(buffer, 4),
            BitConverter.ToUInt16(buffer, 8),
            BitConverter.ToUInt16(buffer, 10),
            BitConverter.ToUInt16(buffer, 12),
            BitConverter.ToUInt32(buffer, 16));
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
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        byte[] fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass);


    [DllImport("ntdll.dll")]
    private static extern int NtQueryVolumeInformationFile(
        SafeFileHandle file,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileSystemInformationClass);
}
