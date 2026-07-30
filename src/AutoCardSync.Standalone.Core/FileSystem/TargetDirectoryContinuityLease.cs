using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.FileSystem;

[SupportedOSPlatform("windows")]
public sealed class TargetDirectoryContinuityLease : IDisposable
{
    private const int FileIdInfo = 18;
    private readonly List<DirectoryLease> _directories;
    private bool _disposed;

    private TargetDirectoryContinuityLease(List<DirectoryLease> directories) => _directories = directories;

    public static TargetDirectoryContinuityLease Acquire(
        string targetRoot,
        string finalPath,
        Action<SafeFileHandle, string>? openedRootHandleCheck = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Target directory continuity requires Windows handles.");

        string root = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar);
        string final = SafePathResolver.ResolveSafePath(root, Path.GetRelativePath(root, Path.GetFullPath(finalPath)));
        string parent = Path.GetDirectoryName(final) ?? throw new InvalidDataException("Final path has no parent directory.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var leases = new List<DirectoryLease>();
        try
        {
            AcquireOne(root, leases);
            if (openedRootHandleCheck is not null)
                openedRootHandleCheck(leases[0].Handle, leases[0].Path);
            string relativeParent = Path.GetRelativePath(root, parent);
            if (relativeParent != ".")
            {
                string current = root;
                foreach (string segment in relativeParent.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    if (!Directory.Exists(current))
                        Directory.CreateDirectory(current);
                    AcquireOne(current, leases);
                }
            }

            var result = new TargetDirectoryContinuityLease(leases);
            result.EnsureContinuous();
            if (openedRootHandleCheck is not null)
                result.EnsureRootHandle(openedRootHandleCheck);
            return result;
        }
        catch
        {
            foreach (DirectoryLease lease in leases)
                lease.Handle.Dispose();
            throw;
        }
    }

    public void EnsureContinuous()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (DirectoryLease lease in _directories)
        {
            if (!Directory.Exists(lease.Path) ||
                (File.GetAttributes(lease.Path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Target directory chain changed: '{lease.Path}'.");
            string final = JunctionResolver.GetFinalTarget(lease.Path);
            DirectoryIdentity currentIdentity = CaptureIdentityForPath(lease.Path, forceLegacy: false);
            if (!string.Equals(final.TrimEnd(Path.DirectorySeparatorChar),
                    lease.CanonicalPath, StringComparison.OrdinalIgnoreCase) ||
                currentIdentity != lease.Identity)
                throw new IOException($"Target directory identity changed: '{lease.Path}'.");
        }
    }

    public void EnsureRootHandle(Action<SafeFileHandle, string> openedRootHandleCheck)
    {
        ArgumentNullException.ThrowIfNull(openedRootHandleCheck);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_directories.Count == 0)
            throw new IOException("Target directory continuity lease has no frozen root handle.");

        DirectoryLease root = _directories[0];
        openedRootHandleCheck(root.Handle, root.Path);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int index = _directories.Count - 1; index >= 0; index--)
            _directories[index].Handle.Dispose();
    }

    private static void AcquireOne(string path, List<DirectoryLease> leases)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Target directory cannot be a reparse point: '{full}'.");
        string canonical = JunctionResolver.GetFinalTarget(full).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(canonical, full, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Target directory resolves elsewhere: '{full}'.");

        SafeFileHandle handle = OpenDirectoryHandle(full, FileShare.Read | FileShare.Write);
        try
        {
            DirectoryIdentity identity = CaptureIdentity(handle, full, forceLegacy: false);
            if (!string.Equals(JunctionResolver.GetFinalTarget(full).TrimEnd(Path.DirectorySeparatorChar),
                    canonical, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Target directory changed while acquiring continuity lock: '{full}'.");
            leases.Add(new DirectoryLease(full, canonical, identity, handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static DirectoryIdentity CaptureIdentityForPath(string path, bool forceLegacy)
    {
        using SafeFileHandle handle = OpenDirectoryHandle(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
            FileShare.Read | FileShare.Write | FileShare.Delete);
        return CaptureIdentity(handle, path, forceLegacy);
    }

    private static DirectoryIdentity CaptureIdentity(
        SafeFileHandle handle, string displayPath, bool forceLegacy)
    {
        if (!forceLegacy)
        {
            byte[] buffer = new byte[24];
            if (GetFileInformationByHandleEx(handle, FileIdInfo, buffer, (uint)buffer.Length))
            {
                return new DirectoryIdentity(
                    "FileId128",
                    BitConverter.ToUInt64(buffer, 0),
                    BitConverter.ToUInt64(buffer, 16),
                    BitConverter.ToUInt64(buffer, 8));
            }

            int error = Marshal.GetLastWin32Error();
            if (error is not (1 or 50 or 87))
            {
                throw new IOException(
                    $"GetFileInformationByHandleEx(FileIdInfo) failed for '{displayPath}' with Win32 error {error}.",
                    new Win32Exception(error));
            }
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"GetFileInformationByHandle failed for '{displayPath}' with Win32 error {error}.",
                new Win32Exception(error));
        }

        return new DirectoryIdentity(
            "FileIndex64", info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    private static SafeFileHandle OpenDirectoryHandle(string path, FileShare shareMode)
    {
        SafeFileHandle handle = CreateFileW(path, 0, shareMode, IntPtr.Zero,
            3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException($"Unable to lock target directory '{path}' (Win32 {error}).",
            new Win32Exception(error));
    }

    private sealed record DirectoryLease(
        string Path, string CanonicalPath, DirectoryIdentity Identity, SafeFileHandle Handle);

    private sealed record DirectoryIdentity(
        string Kind, ulong VolumeSerialNumber, ulong FileIdHigh, ulong FileIdLow);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file, int fileInformationClass, byte[] fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
