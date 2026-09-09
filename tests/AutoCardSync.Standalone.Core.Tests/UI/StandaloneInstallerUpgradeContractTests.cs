using System.Xml.Linq;

namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneInstallerUpgradeContractTests
{
    [Fact]
    public void Present_machine_prerequisites_are_not_recached_during_a_per_user_upgrade()
    {
        XDocument bundle = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "AutoCardSync.Bootstrapper", "Bundle.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";

        XElement dotnet = RequiredPackage(bundle, wix, "DotNetDesktopRuntime10x64");
        Assert.Equal("NOT (DotNetDesktopRuntime10Version >= v10.0.10)",
            DecodeCondition(dotnet.Attribute("InstallCondition")?.Value));
        Assert.Equal("remove", dotnet.Attribute("Cache")?.Value);

        XElement webView = RequiredPackage(bundle, wix, "WebView2RuntimeX64");
        Assert.Equal("NOT (WebView2RuntimeMachineVersion OR WebView2RuntimeUserVersion)",
            DecodeCondition(webView.Attribute("InstallCondition")?.Value));
        Assert.Equal("remove", webView.Attribute("Cache")?.Value);
    }

    [Fact]
    public void Standalone_msi_keeps_major_upgrade_and_normal_uninstall_contracts()
    {
        XDocument product = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "AutoCardSync.Standalone.Installer", "Product.wxs"));
        XNamespace wix = "http://wixtoolset.org/schemas/v4/wxs";
        XElement package = Assert.Single(product.Descendants(wix + "Package"));

        Assert.Equal("perUser", package.Attribute("Scope")?.Value);
        XElement majorUpgrade = Assert.Single(package.Elements(wix + "MajorUpgrade"));
        Assert.Equal("yes", majorUpgrade.Attribute("AllowSameVersionUpgrades")?.Value);

        XDocument installerProject = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src", "AutoCardSync.Standalone.Installer", "AutoCardSync.Standalone.Installer.csproj"));
        string suppressedIces = Assert.Single(installerProject.Descendants("SuppressIces")).Value;
        Assert.Contains("ICE61", suppressedIces.Split(';'), StringComparer.Ordinal);

        Assert.DoesNotContain(package.Descendants(wix + "Component"), component =>
            string.Equals(component.Attribute("Permanent")?.Value, "yes", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(package.Descendants(wix + "RegistryValue"), value =>
            string.Equals(value.Attribute("Key")?.Value, @"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(package.Descendants(wix + "ComponentGroup"), group =>
            string.Equals(group.Attribute("Id")?.Value, "StandaloneAutoStart", StringComparison.Ordinal));

        XElement reconcile = Assert.Single(package.Elements(wix + "CustomAction"), action =>
            action.Attribute("Id")?.Value == "ReconcileAutoStart");
        Assert.Equal("fil_7403977D5127658C", reconcile.Attribute("FileRef")?.Value);
        Assert.Equal("--reconcile-autostart", reconcile.Attribute("ExeCommand")?.Value);
        Assert.Equal("deferred", reconcile.Attribute("Execute")?.Value);
        Assert.Equal("yes", reconcile.Attribute("Impersonate")?.Value);

        XElement remove = Assert.Single(package.Elements(wix + "CustomAction"), action =>
            action.Attribute("Id")?.Value == "RemoveAutoStart");
        Assert.Equal(reconcile.Attribute("FileRef")?.Value, remove.Attribute("FileRef")?.Value);
        Assert.Equal("--remove-autostart", remove.Attribute("ExeCommand")?.Value);

        XElement sequence = Assert.Single(package.Elements(wix + "InstallExecuteSequence"));
        XElement reconcileSequence = Assert.Single(sequence.Elements(wix + "Custom"), action =>
            action.Attribute("Action")?.Value == "ReconcileAutoStart");
        Assert.Equal("InstallFiles", reconcileSequence.Attribute("After")?.Value);
        Assert.Equal("NOT (REMOVE ~= \"ALL\")", reconcileSequence.Attribute("Condition")?.Value);
        XElement removeSequence = Assert.Single(sequence.Elements(wix + "Custom"), action =>
            action.Attribute("Action")?.Value == "RemoveAutoStart");
        Assert.Equal("RemoveFiles", removeSequence.Attribute("Before")?.Value);
        Assert.Equal("REMOVE ~= \"ALL\" AND NOT UPGRADINGPRODUCTCODE",
            removeSequence.Attribute("Condition")?.Value);

        string appSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "AutoCardSync.Standalone", "App.xaml.cs"));
        Assert.Contains("--reconcile-autostart", appSource, StringComparison.Ordinal);
        Assert.Contains("--remove-autostart", appSource, StringComparison.Ordinal);
    }

    private static XElement RequiredPackage(XDocument document, XNamespace wix, string id) =>
        Assert.Single(document.Descendants(wix + "ExePackage"),
            package => package.Attribute("Id")?.Value == id);

    private static string? DecodeCondition(string? value) => value?.Replace("&gt;", ">");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AutoCardSync.Standalone.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
