using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoCardSync.Infrastructure.FileSystem;

/// <summary>
/// Resolves the final target of junctions, symbolic links, and mount points
/// using the Windows GetFinalPathNameByHandleW API. On non-Windows platforms,
/// this class fails closed by throwing PlatformNotSupportedException.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class JunctionResolver
{
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Resolves the final target of a path by opening a handle and calling
    /// GetFinalPathNameByHandleW. If the path is not a junction/symlink,
    /// returns the original path unchanged. If resolution fails, throws
    /// PathViolationException with ViolationType.JunctionEscape.
    /// </summary>
    /// <param name="path">The path to resolve (must be an absolute, full path).</param>
    /// <returns>The final resolved target path.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown on non-Windows platforms.</exception>
    /// <exception cref="PathViolationException">Thrown when resolution fails.</exception>
    public static string GetFinalTarget(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Junction resolution via GetFinalPathNameByHandleW is only supported on Windows.");

        string fullPath = Path.GetFullPath(path);

        IntPtr handle = OpenHandle(fullPath);
        if (handle == INVALID_HANDLE_VALUE)
        {
            // Path does not exist or cannot be opened — nothing to resolve.
            // Non-existent paths pass through safely; there is no junction to exploit.
            return fullPath;
        }

        try
        {
            return ResolveViaHandle(handle, fullPath);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr OpenHandle(string fullPath)
    {
        // Open with minimal access rights. READ_ATTRIBUTES (0x80) is sufficient
        // for GetFinalPathNameByHandleW to work.
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a directory handle.
        // FILE_FLAG_OPEN_REPARSE_POINT opens the reparse point itself, not its target,
        // so we get a handle to the link/junction that we can then resolve.
        return CreateFileW(
            fullPath,
            0x80, // FILE_READ_ATTRIBUTES
            0x7,  // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            IntPtr.Zero,
            3,    // OPEN_EXISTING
            FILE_ATTRIBUTE_NORMAL | FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);
    }

    private static string ResolveViaHandle(IntPtr handle, string originalPath)
    {
        // GetFinalPathNameByHandleW with FILE_NAME_NORMALIZED (0x0) and VOLUME_NAME_DOS (0x0)
        // Returns the normalized path using the DOS device namespace (e.g., \\?\C:\foo).
        // First call with cchFilePath=0 to get the required buffer size (including null terminator).
        uint requiredSize = GetFinalPathNameByHandleW(handle, Array.Empty<char>(), 0, 0x0);

        if (requiredSize == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new PathViolationException(
                ViolationType.JunctionEscape,
                originalPath,
                $"GetFinalPathNameByHandleW failed to determine buffer size (Win32 error {error}): '{originalPath}'");
        }

        char[] buffer = new char[requiredSize];
        uint resultLength = GetFinalPathNameByHandleW(handle, buffer, requiredSize, 0x0);

        if (resultLength == 0)
        {
            int error = Marshal.GetLastWin32Error();
            throw new PathViolationException(
                ViolationType.JunctionEscape,
                originalPath,
                $"GetFinalPathNameByHandleW failed to resolve path (Win32 error {error}): '{originalPath}'");
        }

        // Result includes the \\?\ prefix. Strip it for normal path operations.
        // For UNC paths, GetFinalPathNameByHandleW returns \\?\UNC\server\share\...
        // which must be converted back to \\server\share\...
        string resolved = new string(buffer, 0, (int)resultLength);
        const string extendedPrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (resolved.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            resolved = @"\\" + resolved.Substring(uncPrefix.Length);
        else if (resolved.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
            resolved = resolved.Substring(extendedPrefix.Length);

        return resolved;
    }
}
