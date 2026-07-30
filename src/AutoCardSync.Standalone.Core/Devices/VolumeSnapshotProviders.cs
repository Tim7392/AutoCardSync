using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Agent.Service.Devices;

public interface IVolumeSnapshotProvider
{
    IReadOnlyList<VolumeEventArgs> EnumerateMountedVolumes();
}

[SupportedOSPlatform("windows")]
public sealed class NativeVolumeSnapshotProvider(
    IVolumeSnapshotProvider? fallback = null) : IVolumeSnapshotProvider
{
    private readonly IVolumeSnapshotProvider _fallback = fallback ?? new WmiVolumeSnapshotProvider();

    public IReadOnlyList<VolumeEventArgs> EnumerateMountedVolumes()
    {
        try
        {
            return EnumerateNative();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or
                                          UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return _fallback.EnumerateMountedVolumes();
        }
    }

    private static IReadOnlyList<VolumeEventArgs> EnumerateNative()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var volumes = new List<VolumeEventArgs>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;
                string root = EnsureTrailingSeparator(drive.RootDirectory.FullName);
                var volumeName = new StringBuilder(128);
                if (!GetVolumeNameForVolumeMountPoint(root, volumeName, volumeName.Capacity))
                    continue;
                MountIdentitySnapshot mount = CaptureMountIdentity(root);
                volumes.Add(new VolumeEventArgs(
                    root,
                    volumeName.ToString(),
                    drive.DriveFormat,
                    drive.TotalSize,
                    DateTimeOffset.UtcNow,
                    mount.Identity,
                    mount.ContinuityProven));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive can disappear between enumeration and inspection. The next
                // notification or the ten-second reconciliation pass will retry it.
            }
        }
        return volumes;
    }

    private static string EnsureTrailingSeparator(string value) =>
        value.EndsWith(Path.DirectorySeparatorChar)
            ? value
            : string.Concat(value, Path.DirectorySeparatorChar);

    internal static MountIdentitySnapshot CaptureMountIdentity(string root)
    {
        FileIdentity? fileIdentity = null;
        try
        {
            fileIdentity = FileIdentity.GetFileIdentity(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           PlatformNotSupportedException)
        {
            // Media-change evidence can still provide a mount epoch when the root FileId is unavailable.
        }

        uint? mediaChangeCount = TryGetMediaChangeCount(root, out uint observedCount)
            ? observedCount
            : null;
        string? identity = ComposeMountIdentity(fileIdentity, mediaChangeCount);
        return new MountIdentitySnapshot(
            identity,
            mediaChangeCount.HasValue && !string.IsNullOrWhiteSpace(identity));
    }

    internal static string? ComposeMountIdentity(
        FileIdentity? fileIdentity,
        uint? mediaChangeCount)
    {
        bool hasFileIdentity = fileIdentity is FileIdentity value &&
            (value.FileIndexHigh != 0 || value.FileIndexLow != 0);
        if (!hasFileIdentity && !mediaChangeCount.HasValue)
            return null;

        string objectIdentity = hasFileIdentity
            ? $"{fileIdentity!.Value.VolumeSerialNumber:X16}:" +
              $"{fileIdentity.Value.FileIndexHigh:X16}:{fileIdentity.Value.FileIndexLow:X16}"
            : "NOFILEID";
        return mediaChangeCount.HasValue
            ? $"{objectIdentity}:M{mediaChangeCount.Value:X8}"
            : objectIdentity;
    }

    internal static bool TryGetMediaChangeCount(string root, out uint mediaChangeCount)
    {
        mediaChangeCount = 0;
        string? driveRoot = Path.GetPathRoot(Path.GetFullPath(root));
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':')
            return false;
        string volumePath = string.Concat(@"\\.\", driveRoot.AsSpan(0, 2));
        using SafeFileHandle handle = CreateFileW(
            volumePath, 0, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;
        return DeviceIoControl(
            handle, 0x002D0800, IntPtr.Zero, 0,
            out mediaChangeCount, sizeof(uint), out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out uint lpOutBuffer,
        int nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);
}

[SupportedOSPlatform("windows")]
public sealed class WmiVolumeSnapshotProvider : IVolumeSnapshotProvider
{
    public IReadOnlyList<VolumeEventArgs> EnumerateMountedVolumes()
    {
        var volumes = new List<VolumeEventArgs>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, DriveLetter, FileSystem, Capacity FROM Win32_Volume WHERE DriveLetter IS NOT NULL");
        foreach (ManagementObject volume in searcher.Get())
            using (volume)
            {
                string? driveLetter = volume["DriveLetter"] as string;
                if (string.IsNullOrWhiteSpace(driveLetter))
                    continue;
                string root = EnsureTrailingSeparator(driveLetter);
                MountIdentitySnapshot mount = NativeVolumeSnapshotProvider.CaptureMountIdentity(root);
                volumes.Add(new VolumeEventArgs(
                    root,
                    volume["DeviceID"] as string ?? string.Empty,
                    volume["FileSystem"] as string ?? string.Empty,
                    volume["Capacity"] is null ? 0 : Convert.ToInt64(volume["Capacity"]),
                    DateTimeOffset.UtcNow,
                    mount.Identity,
                    mount.ContinuityProven));
            }
        return volumes;
    }

    private static string EnsureTrailingSeparator(string value) =>
        value.EndsWith(Path.DirectorySeparatorChar)
            ? value
            : string.Concat(value, Path.DirectorySeparatorChar);
}

internal readonly record struct MountIdentitySnapshot(
    string? Identity,
    bool ContinuityProven);

public sealed record VolumeEventArgs(
    string DriveLetter,
    string VolumeGuid,
    string FileSystem,
    long Capacity,
    DateTimeOffset Timestamp,
    string? MountIdentity = null,
    bool MountContinuityProven = false);
