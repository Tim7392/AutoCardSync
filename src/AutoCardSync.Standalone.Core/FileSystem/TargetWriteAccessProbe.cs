using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("AutoCardSync.Standalone.Core.Tests")]

namespace AutoCardSync.Infrastructure.FileSystem;

/// <summary>
/// Creates a disposable write probe relative to an already-opened target directory.
/// The probe is marked for deletion on close, so cleanup remains bound to the opened
/// object even if the display path is replaced after validation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TargetWriteAccessProbe
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileTraverse = 0x00000020;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileCreate = 2;
    private const uint FileAttributeHidden = 0x00000002;
    private const uint FileShareAll = 0x00000007;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileDeleteOnClose = 0x00001000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;

    public static void Verify(
        string targetDirectory,
        Action<SafeFileHandle, string> openedDirectoryValidator)
        => VerifyCore(targetDirectory, openedDirectoryValidator, faultInjection: null);

    internal static void VerifyForTesting(
        string targetDirectory,
        Action<SafeFileHandle, string> openedDirectoryValidator,
        TargetWriteAccessProbeFaultInjection faultInjection)
        => VerifyCore(targetDirectory, openedDirectoryValidator, faultInjection);

    private static void VerifyCore(
        string targetDirectory,
        Action<SafeFileHandle, string> openedDirectoryValidator,
        TargetWriteAccessProbeFaultInjection? faultInjection)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Target write probing requires Windows handles.");
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("A target directory is required.", nameof(targetDirectory));
        ArgumentNullException.ThrowIfNull(openedDirectoryValidator);

        string displayPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        EnsureNoReparsePointComponents(displayPath);
        faultInjection?.AfterInitialPathValidation?.Invoke();
        using SafeFileHandle directoryHandle = OpenDirectory(displayPath);
        EnsureDirectoryPathStillNamesHandle(directoryHandle, displayPath);
        openedDirectoryValidator(directoryHandle, displayPath);
        faultInjection?.AfterDirectoryValidated?.Invoke();
        EnsureDirectoryPathStillNamesHandle(directoryHandle, displayPath);

        string probeName = $".autocardsync-write-probe-{Guid.NewGuid():N}.tmp";
        using SafeFileHandle probeHandle = CreateDeleteOnCloseFile(directoryHandle, probeName, displayPath);
        faultInjection?.AfterProbeOpened?.Invoke(probeName);
        EnsureDirectoryPathStillNamesHandle(directoryHandle, displayPath);
        openedDirectoryValidator(directoryHandle, displayPath);
        FileIdentity directoryIdentity = FileIdentity.GetFileIdentity(directoryHandle, displayPath);
        FileIdentity probeIdentity = FileIdentity.GetFileIdentity(probeHandle, probeName);
        if (directoryIdentity.VolumeSerialNumber == 0 ||
            probeIdentity.VolumeSerialNumber == 0 ||
            directoryIdentity.VolumeSerialNumber != probeIdentity.VolumeSerialNumber)
        {
            throw new IOException(
                $"Bound write probe volume identity differs from target directory '{displayPath}'.");
        }

        byte[] content = Guid.NewGuid().ToByteArray();
        RandomAccess.Write(probeHandle, content, fileOffset: 0);
        RandomAccess.FlushToDisk(probeHandle);
    }

    private static void EnsureDirectoryPathStillNamesHandle(
        SafeFileHandle directoryHandle,
        string displayPath)
    {
        FileIdentity.EnsurePathStillNamesOpenedObject(directoryHandle, displayPath);
        EnsureNoReparsePointComponents(displayPath);
    }

    internal static void EnsureNoReparsePointComponents(
        string targetDirectory,
        Func<string, FileAttributes>? getAttributes = null)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("A target directory is required.", nameof(targetDirectory));

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        string root = Path.GetPathRoot(fullPath) ??
            throw new InvalidDataException("The target directory has no filesystem root.");
        string current = root;
        Func<string, FileAttributes> readAttributes = getAttributes ?? File.GetAttributes;
        foreach (string segment in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((readAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Target directory cannot contain a reparse-point component: '{current}'.");
            }
        }
    }

    private static SafeFileHandle OpenDirectory(string displayPath)
    {
        SafeFileHandle handle = CreateFileW(
            displayPath,
            FileListDirectory | FileTraverse | FileReadAttributes,
            FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException(
            $"Unable to open target directory '{displayPath}' for a bound write probe.",
            new System.ComponentModel.Win32Exception(error));
    }

    private static SafeFileHandle CreateDeleteOnCloseFile(
        SafeFileHandle directoryHandle,
        string probeName,
        string displayPath)
    {
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(probeName);
        try
        {
            var name = new UnicodeString
            {
                Length = checked((ushort)(probeName.Length * sizeof(char))),
                MaximumLength = checked((ushort)((probeName.Length + 1) * sizeof(char))),
                Buffer = nameBuffer,
            };
            IntPtr namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            try
            {
                Marshal.StructureToPtr(name, namePointer, fDeleteOld: false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = directoryHandle.DangerousGetHandle(),
                    ObjectName = namePointer,
                    Attributes = ObjectCaseInsensitive,
                };

                int status = NtCreateFile(
                    out SafeFileHandle probeHandle,
                    FileWriteData | FileReadAttributes | FileWriteAttributes | Delete | Synchronize,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    FileAttributeHidden,
                    FileShareAll,
                    FileCreate,
                    FileNonDirectoryFile |
                    FileSynchronousIoNonAlert |
                    FileDeleteOnClose,
                    IntPtr.Zero,
                    0);
                if (status >= 0 && !probeHandle.IsInvalid)
                    return probeHandle;

                int error = unchecked((int)RtlNtStatusToDosError(status));
                probeHandle?.Dispose();
                throw new IOException(
                    $"Unable to create a bound write probe under '{displayPath}'.",
                    new System.ComponentModel.Win32Exception(error));
            }
            finally
            {
                Marshal.FreeHGlobal(namePointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
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

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}

internal sealed class TargetWriteAccessProbeFaultInjection
{
    public Action? AfterInitialPathValidation { get; init; }
    public Action? AfterDirectoryValidated { get; init; }
    public Action<string>? AfterProbeOpened { get; init; }
}
