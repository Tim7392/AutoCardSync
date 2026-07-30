namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneSetupUiTests
{
    [Fact]
    public void First_run_uses_native_source_picker_and_extension_choices_instead_of_freeform_lists()
    {
        string root = FindRepositoryRoot();
        string html = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "index.html"));
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));

        Assert.Contains("data-picker=\"approvedSourceDirectories\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"approvedSourceDirectoryList\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea id=\"approvedSourceDirectories\"", html, StringComparison.Ordinal);
        Assert.Contains("data-approved-extension", html, StringComparison.Ordinal);
        Assert.Contains("id=\"customExtensionInput\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"addCustomExtensionBtn\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<input id=\"approvedExtensions\" type=\"text\"", html, StringComparison.Ordinal);
        Assert.Contains("picker.selectFolder", script, StringComparison.Ordinal);
        Assert.Contains("collectApprovedExtensions", script, StringComparison.Ordinal);
        Assert.Contains("addCustomExtension", script, StringComparison.Ordinal);
        Assert.Contains("value=\".xml\" checked", html, StringComparison.Ordinal);
        Assert.Contains("payload.totalFiles <= 0", script, StringComparison.Ordinal);

        string mainWindow = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "MainWindow.xaml.cs"));
        Assert.Contains("SourceDirectorySelectionResolver.Resolve", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ExternalSourceVolumeClassifier", mainWindow, StringComparison.Ordinal);
        Assert.Contains("case \"card.reassociate\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("case \"card.reinitialize\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ReinitializeCurrentCardAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RequiredBoolean(payload, \"confirmed\")", mainWindow, StringComparison.Ordinal);
        int transition = mainWindow.IndexOf("BeginConfigurationChange()", StringComparison.Ordinal);
        int save = mainWindow.IndexOf("_configurationService.SaveAsync(", StringComparison.Ordinal);
        int activation = mainWindow.IndexOf("NotifyConfigurationChangedAsync(saved", StringComparison.Ordinal);
        int successResponse = mainWindow.IndexOf("SendResponse(requestId, true, data: saved", StringComparison.Ordinal);
        Assert.True(transition >= 0 && save > transition && activation > save && successResponse > activation);

        string runtime = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "Services", "StandaloneRuntimeService.cs"));
        Assert.Contains("result.CurrentVolumes", runtime, StringComparison.Ordinal);
        Assert.Contains("skipIfScanBusy: true", runtime, StringComparison.Ordinal);
        Assert.Contains("Immediate volume notifications are unavailable", runtime, StringComparison.Ordinal);
        Assert.Contains("await _reconciler.StartAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("ContainsApprovedSourceDirectory", runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void Webview_failure_fallback_can_preserve_profile_and_restart_without_deleting_runtime_state()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "MainWindow.xaml"));
        string window = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "MainWindow.xaml.cs"));
        string app = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "App.xaml.cs"));

        Assert.Contains("FallbackRepairButton", xaml, StringComparison.Ordinal);
        Assert.Contains("备份并重建本地界面", xaml, StringComparison.Ordinal);
        Assert.Contains("CorruptStateFileRecovery.PreserveDirectory(profile)", window, StringComparison.Ordinal);
        Assert.Contains("Path.GetFileName(profile), \"WebView2\"", window, StringComparison.Ordinal);
        Assert.Contains("app.RequestRestart()", window, StringComparison.Ordinal);
        Assert.Contains("_singleInstance?.Dispose()", app, StringComparison.Ordinal);
        Assert.True(
            app.IndexOf("_singleInstance?.Dispose()", StringComparison.Ordinal) <
            app.IndexOf("Process.Start", StringComparison.Ordinal));
    }

    [Fact]
    public void First_run_is_a_required_four_step_screen_instead_of_a_modal_configuration_form()
    {
        string root = FindRepositoryRoot();
        string html = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "index.html"));
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));
        string styles = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "styles", "product.css"));

        Assert.Contains("class=\"screen onboarding-screen\" id=\"setup\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"overlay setup-overlay\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"setupSheet\"", html, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(html, "data-setup-step=\""));
        Assert.Equal(4, CountOccurrences(html, "data-setup-progress=\""));
        Assert.Contains("id=\"setupBackBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"setupNextBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"setupSubmitBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("showSetupStep", script, StringComparison.Ordinal);
        Assert.Contains("validateSetupStep", script, StringComparison.Ordinal);
        Assert.Contains("openSetup({ firstRun: true })", script, StringComparison.Ordinal);
        Assert.Contains("currentScreen !== 'setup'", script, StringComparison.Ordinal);
        Assert.Contains(".app.first-run", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_exposes_three_explicit_target_modes_and_conditionally_validates_selected_targets()
    {
        string root = FindRepositoryRoot();
        string html = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "index.html"));
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));

        Assert.Contains("value=\"nas-only\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"local-only\"", html, StringComparison.Ordinal);
        Assert.Contains("value=\"local-and-nas\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"targetMode\"", html, StringComparison.Ordinal);
        Assert.Contains("updateTargetModeUi", script, StringComparison.Ordinal);
        Assert.Contains("targetMode !== 'nas-only'", script, StringComparison.Ordinal);
        Assert.Contains("targetMode !== 'local-only'", script, StringComparison.Ordinal);
        Assert.Contains("approvedSourceDirectories: defaultTemplate.approvedSourceDirectories", script, StringComparison.Ordinal);
        Assert.Contains("cameraTemplates: normalizedTemplates", script, StringComparison.Ordinal);
        Assert.Contains("targetMode,", script, StringComparison.Ordinal);
        Assert.Contains("localTarget,", script, StringComparison.Ordinal);
        Assert.Contains("nasMappedTarget,", script, StringComparison.Ordinal);
        Assert.Contains("data-completion-target=\"local\"", html, StringComparison.Ordinal);
        Assert.Contains("data-completion-target=\"nas\"", html, StringComparison.Ordinal);
        Assert.Contains("document.querySelector('[data-completion-target=\"local\"]')", script, StringComparison.Ordinal);
        Assert.Contains("document.querySelector('[data-completion-target=\"nas\"]')", script, StringComparison.Ordinal);
    }
    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "AutoCardSync.CSharp.slnf")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
