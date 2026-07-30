using System.Management;
using System.Runtime.Versioning;

namespace AutoCardSync.Agent.Service.Devices;

public sealed record PhysicalDriveMetadata(
    DriveType DriveType,
    string? InterfaceType,
    string? MediaType,
    string? PnpDeviceId,
    string? Model);

public interface IPhysicalDriveMetadataResolver
{
    PhysicalDriveMetadata Resolve(string driveRoot);
}

[SupportedOSPlatform("windows")]
public sealed class WmiPhysicalDriveMetadataResolver : IPhysicalDriveMetadataResolver
{
    public PhysicalDriveMetadata Resolve(string driveRoot)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(driveRoot)) ??
            throw new InvalidDataException("The volume does not have a drive root.");
        var drive = new DriveInfo(root);
        if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram)
            return new(drive.DriveType, null, null, null, null);

        string driveId = root[..2].Replace("'", "''", StringComparison.Ordinal);
        using var partitionSearcher = new ManagementObjectSearcher(
            $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveId}'}} " +
            "WHERE AssocClass=Win32_LogicalDiskToPartition");
        using ManagementObjectCollection partitions = partitionSearcher.Get();
        foreach (ManagementObject partition in partitions)
            using (partition)
            {
                string partitionId = (partition["DeviceID"] as string ?? string.Empty)
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("'", "''", StringComparison.Ordinal);
                using var diskSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} " +
                    "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                using ManagementObjectCollection disks = diskSearcher.Get();
                foreach (ManagementObject disk in disks)
                    using (disk)
                    {
                        return new(
                            drive.DriveType,
                            disk["InterfaceType"] as string,
                            disk["MediaType"] as string,
                            disk["PNPDeviceID"] as string,
                            disk["Model"] as string);
                    }
            }

        return new(drive.DriveType, null, null, null, null);
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExternalSourceVolumeClassifier(
    IPhysicalDriveMetadataResolver? metadataResolver = null)
{
    private readonly IPhysicalDriveMetadataResolver _metadataResolver =
        metadataResolver ?? new WmiPhysicalDriveMetadataResolver();

    public bool IsExternalStorage(VolumeEventArgs volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        PhysicalDriveMetadata metadata = _metadataResolver.Resolve(volume.DriveLetter);
        if (metadata.DriveType == DriveType.Removable)
            return true;
        if (metadata.DriveType != DriveType.Fixed)
            return false;

        return Contains(metadata.MediaType, "external") ||
            Contains(metadata.MediaType, "removable") ||
            EqualsAny(metadata.InterfaceType, "USB", "1394", "SD") ||
            StartsWithAny(metadata.PnpDeviceId, "USB\\", "USBSTOR\\", "SD\\") ||
            Contains(metadata.PnpDeviceId, "USB");
    }

    private static bool EqualsAny(string? value, params string[] expected) =>
        !string.IsNullOrWhiteSpace(value) &&
        expected.Any(item => string.Equals(value.Trim(), item, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(string? value, params string[] prefixes) =>
        !string.IsNullOrWhiteSpace(value) &&
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? value, string fragment) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
