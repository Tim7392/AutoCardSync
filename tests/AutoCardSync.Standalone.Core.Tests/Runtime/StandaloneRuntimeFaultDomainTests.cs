using System.Reflection;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests.Runtime;

public sealed class StandaloneRuntimeFaultDomainTests
{
    [Fact]
    public void Source_and_local_target_on_same_physical_disk_fail_closed()
    {
        FaultDomainInfo source = Local("source-volume", "physicaldrive2", "source");
        FaultDomainInfo local = Local("local-volume", "physicaldrive2", "local");
        FaultDomainInfo nas = Nas("nas-server", "nas");

        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(() =>
            Invoke(source, local, nas));

        IOException error = Assert.IsType<IOException>(invocation.InnerException);
        Assert.Contains("physical disk", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Three_distinct_storage_domains_are_accepted()
    {
        FaultDomainInfo source = Local("source-volume", "physicaldrive2", "source");
        FaultDomainInfo local = Local("local-volume", "physicaldrive0", "local");
        FaultDomainInfo nas = Nas("nas-server", "nas");

        Invoke(source, local, nas);
    }

    private static void Invoke(
        FaultDomainInfo source,
        FaultDomainInfo local,
        FaultDomainInfo nas)
    {
        MethodInfo method = typeof(StandaloneRuntimeService).GetMethod(
            "EnsureIndependentStorageSet",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing storage-set independence guard.");
        method.Invoke(null, [new FaultDomainResolver(), source, local, nas]);
    }

    private static FaultDomainInfo Local(string volume, string disk, string identity) => new(
        FaultDomainId: $"local:{disk}",
        StorageType: "Local",
        PhysicalDiskId: disk,
        VolumeGuid: volume,
        NasSystemId: null,
        Disclaimer: "test",
        StorageIdentity: identity,
        VolumeSerialNumber: disk == "physicaldrive2" ? 1UL : 3UL,
        PhysicalEndpoint: volume);

    private static FaultDomainInfo Nas(string server, string identity) => new(
        FaultDomainId: $"nas:{server}",
        StorageType: "NAS",
        PhysicalDiskId: null,
        VolumeGuid: null,
        NasSystemId: server,
        Disclaimer: "test",
        StorageIdentity: identity,
        VolumeSerialNumber: 2,
        PhysicalEndpoint: $@"\\{server}\share");
}
