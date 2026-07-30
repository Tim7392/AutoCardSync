using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.FileSystem;

/// <summary>
/// Stable, volume-local Windows file identity. Uses the extended file ID when the
/// file system supports it and falls back to the legacy volume serial/file index tuple
/// for volumes such as FAT/exFAT and compatible SMB implementations.
/// </summary>
[SupportedOSPlatform("windows")]
public record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIndexHigh, ulong FileIndexLow)
{
    /// <summary>
    /// Queries the real Windows file identity for the given path using FileIdInfo,
    /// with a legacy handle-information fallback when that information class is unsupported.
    /// </summary>
    /// <param name="filePath">Absolute path to a file or directory.</param>
    /// <exception cref="PlatformNotSupportedException">Thrown on non-Windows platforms.</exception>
    /// <exception cref="IOException">
    /// Thrown when the native call fails (file not found, access denied, unsupported volume, etc.).
    /// </exception>
    public static FileIdentity GetFileIdentity(string filePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "FileIdentity requires Windows file identity APIs.");

        // Open with FILE_FLAG_BACKUP_SEMANTICS so directories work too.
        IntPtr hFile = NativeMethods.CreateFileW(
            filePath,
            0,                          // dwDesiredAccess = 0 (query only, no read/write)
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            NativeMethods.CreationDisposition.OPEN_EXISTING,
            NativeMethods.FileAttributesAndFlags.FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (hFile == NativeMethods.INVALID_HANDLE_VALUE)
        {
            int err = Marshal.GetLastWin32Error();
            throw new IOException(
                $"CreateFileW failed for '{filePath}' with Win32 error {err}.",
                new Win32Exception(err));
        }

        try
        {
            // FILE_ID_INFO: 8-byte volume serial number plus a 16-byte file ID.
            const int FILE_ID_INFO_SIZE = 24;
            byte[] buffer = new byte[FILE_ID_INFO_SIZE];

            bool ok = NativeMethods.GetFileInformationByHandleEx(
                hFile,
                NativeMethods.FileIdInfo,
                buffer,
                (uint)buffer.Length);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err is not (1 or 50 or 87))
                {
                    throw new IOException(
                        $"GetFileInformationByHandleEx(FileIdInfo) failed for '{filePath}' with Win32 error {err}.",
                        new Win32Exception(err));
                }

                if (!NativeMethods.GetFileInformationByHandle(hFile, out NativeMethods.ByHandleFileInformation info))
                {
                    int fallbackErr = Marshal.GetLastWin32Error();
                    throw new IOException(
                        $"GetFileInformationByHandle failed for '{filePath}' with Win32 error {fallbackErr}.",
                        new Win32Exception(fallbackErr));
                }

                return new FileIdentity(
                    info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
            }

            ulong volumeSerial = BitConverter.ToUInt64(buffer, 0);
            ulong fileIndexLow = BitConverter.ToUInt64(buffer, 8);
            ulong fileIndexHigh = BitConverter.ToUInt64(buffer, 16);

            return new FileIdentity(volumeSerial, fileIndexHigh, fileIndexLow);
        }
        finally
        {
            NativeMethods.CloseHandle(hFile);
        }
    }

    public static FileIdentity GetFileIdentity(
        SafeFileHandle handle,
        string displayPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "FileIdentity requires Windows file identity APIs.");
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException($"File handle is invalid for '{displayPath}'.");

        IntPtr nativeHandle = handle.DangerousGetHandle();
        const int fileIdInfoSize = 24;
        byte[] buffer = new byte[fileIdInfoSize];
        bool ok = NativeMethods.GetFileInformationByHandleEx(
            nativeHandle, NativeMethods.FileIdInfo, buffer, (uint)buffer.Length);
        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (1 or 50 or 87))
            {
                throw new IOException(
                    $"GetFileInformationByHandleEx(FileIdInfo) failed for '{displayPath}' " +
                    $"with Win32 error {error}.",
                    new Win32Exception(error));
            }

            if (!NativeMethods.GetFileInformationByHandle(
                    nativeHandle, out NativeMethods.ByHandleFileInformation legacy))
            {
                int fallbackError = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"GetFileInformationByHandle failed for '{displayPath}' " +
                    $"with Win32 error {fallbackError}.",
                    new Win32Exception(fallbackError));
            }

            return new FileIdentity(
                legacy.VolumeSerialNumber, legacy.FileIndexHigh, legacy.FileIndexLow);
        }

        return new FileIdentity(
            BitConverter.ToUInt64(buffer, 0),
            BitConverter.ToUInt64(buffer, 16),
            BitConverter.ToUInt64(buffer, 8));
    }

    public static void EnsurePathStillNamesOpenedObject(
        SafeFileHandle openedHandle,
        string displayPath,
        bool requireSingleLink = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "Opened-path continuity requires Windows file identity APIs.");
        ArgumentNullException.ThrowIfNull(openedHandle);
        if (openedHandle.IsInvalid || openedHandle.IsClosed)
            throw new IOException($"File handle is invalid for '{displayPath}'.");
        if (string.IsNullOrWhiteSpace(displayPath))
            throw new ArgumentException("A display path is required.", nameof(displayPath));

        using SafeFileHandle leafHandle = NativeMethods.CreateFileHandleW(
            Path.GetFullPath(displayPath),
            0x00000080,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            NativeMethods.CreationDisposition.OPEN_EXISTING,
            NativeMethods.FileAttributesAndFlags.FILE_FLAG_BACKUP_SEMANTICS |
            NativeMethods.FileAttributesAndFlags.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
        if (leafHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Unable to reopen target leaf '{displayPath}' without following reparse points.",
                new Win32Exception(error));
        }

        if (!NativeMethods.GetFileInformationByHandle(
                openedHandle.DangerousGetHandle(), out NativeMethods.ByHandleFileInformation openedLegacy) ||
            !NativeMethods.GetFileInformationByHandle(
                leafHandle.DangerousGetHandle(), out NativeMethods.ByHandleFileInformation leafLegacy))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Unable to query target leaf link identity for '{displayPath}'.",
                new Win32Exception(error));
        }

        byte[] tagInformation = new byte[8];
        uint attributes;
        uint reparseTag = 0;
        if (NativeMethods.GetFileInformationByHandleEx(
                leafHandle.DangerousGetHandle(), NativeMethods.FileAttributeTagInfo,
                tagInformation, (uint)tagInformation.Length))
        {
            attributes = BitConverter.ToUInt32(tagInformation, 0);
            reparseTag = BitConverter.ToUInt32(tagInformation, 4);
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ERROR_INVALID_PARAMETER)
            {
                throw new IOException(
                    $"Unable to query target leaf attributes for '{displayPath}'.",
                    new Win32Exception(error));
            }

            // exFAT can reject FileAttributeTagInfo with ERROR_INVALID_PARAMETER.
            // The leaf was still opened with FILE_FLAG_OPEN_REPARSE_POINT, so the
            // legacy handle attributes preserve the fail-closed reparse check.
            attributes = leafLegacy.FileAttributes;
        }

        if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Target leaf is a reparse point and is not allowed: '{displayPath}' " +
                $"(tag 0x{reparseTag:X8}).");
        }

        FileIdentity openedIdentity = GetFileIdentity(openedHandle, displayPath);
        FileIdentity leafIdentity = GetFileIdentity(leafHandle, displayPath);
        if (openedIdentity != leafIdentity)
        {
            throw new IOException(
                $"Target path no longer names the opened object for '{displayPath}'.");
        }

        if (requireSingleLink &&
            (openedLegacy.NumberOfLinks != 1 || leafLegacy.NumberOfLinks != 1))
        {
            throw new IOException(
                $"Temporary target must be a newly created independent object with one name: '{displayPath}' " +
                $"(opened links {openedLegacy.NumberOfLinks}, path links {leafLegacy.NumberOfLinks}).");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  P/Invoke declarations
    // ──────────────────────────────────────────────────────────────────────

    private static class NativeMethods
    {
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        /// <summary>FILE_INFORMATION_CLASS.FileIdInfo = 18</summary>
        public const int FileIdInfo = 18;
        public const int FileAttributeTagInfo = 9;
        public const int ERROR_INVALID_PARAMETER = 87;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFileHandleW(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            CreationDisposition dwCreationDisposition,
            FileAttributesAndFlags dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            CreationDisposition dwCreationDisposition,
            FileAttributesAndFlags dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandleEx(
            IntPtr hFile,
            int FileInformationClass,
            byte[] lpFileInformation,
            uint dwBufferSize);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct ByHandleFileInformation
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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            IntPtr hFile, out ByHandleFileInformation fileInformation);

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        // ── Enum wrappers (matching Windows SDK constants) ──

        public enum CreationDisposition : uint
        {
            OPEN_EXISTING = 3,
        }

        [Flags]
        public enum FileAttributesAndFlags : uint
        {
            FILE_FLAG_BACKUP_SEMANTICS = 0x02000000,
            FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000,
        }
    }
}
