using System.Reflection;
using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Standalone;
using AutoCardSync.Standalone.Core;

namespace AutoCardSync.Standalone.Core.Tests.Architecture;

public sealed class StandaloneDependencyBoundaryTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "AutoCardSync.Agent.Service",
        "AutoCardSync.Dashboard",
        "AutoCardSync.CaSigner",
        "AutoCardSync.Contracts",
        "AutoCardSync.Infrastructure",
        "Microsoft.AspNetCore.SignalR",
        "Microsoft.Data.Sqlite",
        "Microsoft.EntityFrameworkCore",
        "System.Security.Cryptography.Pkcs",
    ];

    [Fact]
    public void Production_assemblies_do_not_reference_v2_runtime_components()
    {
        Assembly[] productionAssemblies =
        [
            typeof(StandaloneDataPaths).Assembly,
            typeof(StandaloneWebMessageProtocol).Assembly,
        ];

        foreach (Assembly assembly in productionAssemblies)
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => ForbiddenAssemblyPrefixes.Any(prefix =>
                    reference.Name?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true));
        }
    }

    [Fact]
    public void Extracted_volume_notification_code_is_built_into_standalone_core()
    {
        Assert.Same(typeof(StandaloneDataPaths).Assembly, typeof(VolumeNotificationListener).Assembly);
    }
}
