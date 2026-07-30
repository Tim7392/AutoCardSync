using AutoCardSync.Agent.Service.Devices;

namespace AutoCardSync.Standalone.Core.Tests.Devices;

public sealed class ExternalSourceVolumeClassifierTests
{
    [Theory]
    [InlineData(DriveType.Removable, null, null, null)]
    [InlineData(DriveType.Fixed, "SCSI", "External hard disk media", @"SCSI\DISK&VEN_PGYTECH")]
    [InlineData(DriveType.Fixed, "USB", "Fixed hard disk media", @"USBSTOR\DISK")]
    [InlineData(DriveType.Fixed, "SCSI", "Fixed hard disk media", @"SCSI\DISK&VEN_USB&PROD_READER")]
    [InlineData(DriveType.Fixed, "SCSI", "Removable Media", @"SCSI\DISK&VEN_SD_READER")]
    [InlineData(DriveType.Fixed, "SD", "Fixed hard disk media", @"SD\DISK&VEN_CAMERA")]
    [InlineData(DriveType.Fixed, "SCSI", "Fixed hard disk media", @"SD\DISK&VEN_CAMERA")]
    public void External_removable_and_usb_backed_storage_is_accepted(
        DriveType driveType,
        string? interfaceType,
        string? mediaType,
        string? pnpDeviceId)
    {
        var classifier = new ExternalSourceVolumeClassifier(new FakeMetadataResolver(
            new PhysicalDriveMetadata(driveType, interfaceType, mediaType, pnpDeviceId, "test")));

        Assert.True(classifier.IsExternalStorage(Volume()));
    }

    [Theory]
    [InlineData(DriveType.Fixed, "SATA", "Fixed hard disk media", @"SCSI\DISK&VEN_INTERNAL")]
    [InlineData(DriveType.Network, null, null, null)]
    [InlineData(DriveType.CDRom, "SATA", "CD-ROM", @"IDE\CDROM")]
    public void Internal_network_and_optical_volumes_are_rejected(
        DriveType driveType,
        string? interfaceType,
        string? mediaType,
        string? pnpDeviceId)
    {
        var classifier = new ExternalSourceVolumeClassifier(new FakeMetadataResolver(
            new PhysicalDriveMetadata(driveType, interfaceType, mediaType, pnpDeviceId, "test")));

        Assert.False(classifier.IsExternalStorage(Volume()));
    }

    private static VolumeEventArgs Volume() =>
        new(@"E:\", "volume-guid", "exFAT", 1_024, DateTimeOffset.UtcNow);

    private sealed class FakeMetadataResolver(PhysicalDriveMetadata metadata)
        : IPhysicalDriveMetadataResolver
    {
        public PhysicalDriveMetadata Resolve(string driveRoot) => metadata;
    }
}
