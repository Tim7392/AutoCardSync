namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneSetupUiTests
{
    [Fact]
    public void First_run_uses_native_source_picker_and_extension_choices_instead_of_freeform_lists()
    {
        string root = FindRepositoryRoot();
        string html = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "index.html"));
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));

        Assert.Contains("id=\"homeSub\"", html, StringComparison.Ordinal);
        Assert.Contains("先完成一次设置，再自动同步。", html, StringComparison.Ordinal);
        Assert.Contains("id=\"waitingHeadline\">先完成首次配置", html, StringComparison.Ordinal);
        Assert.Contains("跟随首次引导完成素材范围和保存位置", html, StringComparison.Ordinal);
        Assert.Contains("id=\"manageCardsBtn\" type=\"button\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("可以选择当前卡里的某个文件夹，也可以选择整张卡的根目录", html, StringComparison.Ordinal);
        Assert.Contains("选择卡根目录时会保存为“.”", html, StringComparison.Ordinal);
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
        string configurationRender = Slice(script, "function renderConfiguration(configuration", "function formatBytes");
        Assert.Contains("byId('homeTitle').textContent = configured ? '插卡后，自动开始。' : '先完成一次设置，再自动同步。';", configurationRender, StringComparison.Ordinal);
        Assert.Contains("byId('homeSub').textContent = configured", configurationRender, StringComparison.Ordinal);

        string mainWindow = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "MainWindow.xaml.cs"));
        Assert.Contains("SourceDirectorySelectionResolver.Resolve", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ExternalSourceVolumeClassifier", mainWindow, StringComparison.Ordinal);
        Assert.Contains("case \"card.reassociate\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("case \"card.reinitialize\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("InitializeMountedCardAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ReinitializeMountedCardAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RequiredBoolean(payload, \"confirmed\")", mainWindow, StringComparison.Ordinal);
        int configurationSaveCase = mainWindow.IndexOf("case \"configuration.save\"", StringComparison.Ordinal);
        int nextCase = mainWindow.IndexOf("case \"card.initialize\"", configurationSaveCase, StringComparison.Ordinal);
        string configurationSave = mainWindow[configurationSaveCase..nextCase];
        int transition = configurationSave.IndexOf("BeginConfigurationChange()", StringComparison.Ordinal);
        int save = configurationSave.IndexOf("_configurationService.SaveAsync(", StringComparison.Ordinal);
        int activation = configurationSave.IndexOf("NotifyConfigurationChangedAsync(", StringComparison.Ordinal);
        int successResponse = configurationSave.IndexOf("SendResponse(requestId, true, data: saved", StringComparison.Ordinal);
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
    public void Startup_shows_the_window_before_waiting_for_background_runtime_recovery()
    {
        string root = FindRepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "App.xaml.cs"));

        int resolveWindow = app.IndexOf("GetRequiredService<MainWindow>()", StringComparison.Ordinal);
        int showWindow = app.IndexOf("window.Show()", StringComparison.Ordinal);
        int startHost = app.IndexOf("await _host.StartAsync()", StringComparison.Ordinal);

        Assert.True(resolveWindow >= 0 && showWindow > resolveWindow && startHost > showWindow);
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
    [Fact]
    public void Card_scoped_setup_does_not_submit_or_save_global_settings()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));
        string mainWindow = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "MainWindow.xaml.cs"));

        string cardCollector = Slice(
            script,
            "function collectCardScopedConfiguration()",
            "function cardTextList(value)");
        Assert.Contains("cameraTemplates: [selectedTemplate]", cardCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("targetMode", cardCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("localTarget", cardCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("nasMappedTarget", cardCollector, StringComparison.Ordinal);
        Assert.DoesNotContain("autoStartOnLogin", cardCollector, StringComparison.Ordinal);
        Assert.Contains("cardScopedOperation", script, StringComparison.Ordinal);
        Assert.Contains("collectCardScopedConfiguration()", script, StringComparison.Ordinal);

        string profileHandler = Slice(
            mainWindow,
            "case \"card.profile.configure\"",
            "case \"card.profile.reinitialize\"");
        Assert.Contains("SaveCardInitializationAsync", profileHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAsync(dto", profileHandler, StringComparison.Ordinal);
        Assert.Contains("relativePath = selection.RelativePath", mainWindow, StringComparison.Ordinal);
        Assert.Contains("volumeKey = mountedCard?.VolumeKey", mainWindow, StringComparison.Ordinal);
    }

    private static string Slice(string value, string start, string end)
    {
        int startIndex = value.IndexOf(start, StringComparison.Ordinal);
        int endIndex = value.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return value[startIndex..endIndex];
    }

    [Fact]
    public void Card_initialization_never_uses_the_legacy_register_without_import_state()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "src", "AutoCardSync.Standalone", "WebUI", "scripts", "app.js"));

        string completionPredicate = Slice(script, "function mediaIsComplete(item)", "function mediaNeedsAttention(item)");
        Assert.DoesNotContain("item.workState", completionPredicate, StringComparison.Ordinal);
        Assert.Contains("approved_material_verified", completionPredicate, StringComparison.Ordinal);
        Assert.Contains("safe_to_remove", completionPredicate, StringComparison.Ordinal);

        string detail = Slice(script, "function mediaDetailText(item)", "function mediaStateClass(item");
        Assert.DoesNotContain("CARD_SOFTWARE_INITIALIZED", script, StringComparison.Ordinal);
        Assert.DoesNotContain("只登记为基线", script, StringComparison.Ordinal);
        Assert.Contains("初始化并导入当前素材", script, StringComparison.Ordinal);
        Assert.Contains("EMPTY_CARD_INITIALIZED", detail, StringComparison.Ordinal);
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
            if (File.Exists(Path.Combine(current.FullName, "AutoCardSync.Standalone.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
