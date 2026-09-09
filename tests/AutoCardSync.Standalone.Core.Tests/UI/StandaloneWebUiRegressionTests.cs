namespace AutoCardSync.Standalone.Core.Tests.UI;

public sealed class StandaloneWebUiRegressionTests
{
    [Fact]
    public void Metrics_use_persistent_motion_slots_instead_of_rebuilding_every_value()
    {
        string script = ReadWebUi("scripts", "app.js");
        string renderMetric = Between(script, "  function renderMetric", "  function queueMetricRender");
        string animateDigit = Between(script, "  function animateDigit", "  function renderMetric");

        Assert.DoesNotContain("replaceChildren", renderMetric, StringComparison.Ordinal);
        Assert.Contains("element.dataset.motionInitialized !== 'true'", renderMetric, StringComparison.Ordinal);
        Assert.Contains("while (element.firstChild) element.firstChild.remove()", renderMetric, StringComparison.Ordinal);
        Assert.Contains("part.replaceWith(replacement)", renderMetric, StringComparison.Ordinal);
        Assert.Contains("animateDigit(part, character)", renderMetric, StringComparison.Ordinal);
        Assert.Contains("motion-digit-slot", script, StringComparison.Ordinal);
        Assert.Contains("motion-static--unit", script, StringComparison.Ordinal);
        Assert.Contains("outgoing", animateDigit, StringComparison.Ordinal);
        Assert.Contains("incoming", animateDigit, StringComparison.Ordinal);
    }

    [Fact]
    public void Motion_parameters_and_metric_throttle_match_the_approved_visual_contract()
    {
        string script = ReadWebUi("scripts", "app.js");

        Assert.Contains("const METRIC_RENDER_INTERVAL_MS = 610", script, StringComparison.Ordinal);
        Assert.Contains("durationMs: 330", script, StringComparison.Ordinal);
        Assert.Contains("travelPercent: 24", script, StringComparison.Ordinal);
        Assert.Contains("midpointPercent: 6", script, StringComparison.Ordinal);
        Assert.Contains("maxBlurPx: 3.2", script, StringComparison.Ordinal);
        Assert.Contains("midpointBlurPx: 1", script, StringComparison.Ordinal);
        Assert.Contains("enterMidpoint: 0.56", script, StringComparison.Ordinal);
        Assert.Contains("exitMidpoint: 0.50", script, StringComparison.Ordinal);
        Assert.Contains("cubic-bezier(.22,1,.36,1)", script, StringComparison.Ordinal);
        Assert.Contains("METRIC_RENDER_INTERVAL_MS - elapsed", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Progress_ring_has_one_css_renderer_that_starts_at_zero()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");
        string styles = ReadWebUi("styles", "app.css");

        Assert.DoesNotContain("progress-svg", html, StringComparison.Ordinal);
        Assert.DoesNotContain("progressCircle", html, StringComparison.Ordinal);
        Assert.Contains("--progress-rendered:0;", styles, StringComparison.Ordinal);
        Assert.Contains("style.setProperty('--progress-rendered', String(percent))", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-dashoffset", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CIRCUMFERENCE", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Task_inspector_and_responsive_layout_do_not_enable_horizontal_scrolling()
    {
        string styles = ReadWebUi("styles", "product.css");

        Assert.Contains(".target-inspector{padding:24px 21px;display:flex;flex-direction:column;gap:14px;overflow-x:hidden;overflow-y:auto}", styles, StringComparison.Ordinal);
        Assert.Contains(".task-layout{overflow-x:hidden;overflow-y:auto}", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".target-inspector{padding:24px 21px;display:flex;flex-direction:column;gap:14px;overflow:auto}", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".task-layout{overflow:auto}", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_ready_uses_its_own_safe_screen_without_target_completion_claims()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");
        string renderBaseline = Between(script, "  function renderBaseline", "  function renderComplete");

        Assert.Contains("id=\"baseline\"", html, StringComparison.Ordinal);
        Assert.Contains("素材卡初始化完成", html, StringComparison.Ordinal);
        Assert.Contains("已记录当前素材清单，今后仅同步新增素材", html, StringComparison.Ordinal);
        Assert.Contains("payload.phase !== 'baseline-ready'", renderBaseline, StringComparison.Ordinal);
        Assert.Contains("payload.baselinePersisted !== true", renderBaseline, StringComparison.Ordinal);
        Assert.Contains("payload.safeToRemoveCard !== false", renderBaseline, StringComparison.Ordinal);
        Assert.Contains("payload.safeToClear !== false", renderBaseline, StringComparison.Ordinal);
        Assert.Contains("payload.verificationScope !== 'none'", renderBaseline, StringComparison.Ordinal);
        Assert.Contains("payload.targets.length !== 0", renderBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("renderTarget(", renderBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("completeLocal", renderBaseline, StringComparison.Ordinal);
        Assert.DoesNotContain("completeNas", renderBaseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_uses_source_read_speed_and_preserves_single_target_cards()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");
        string renderTarget = Between(script, "  function renderTarget", "  function renderCopying");
        string renderCopying = Between(script, "  function renderCopying", "  function renderFailure");

        Assert.Contains("素材卡读取速度", html, StringComparison.Ordinal);
        Assert.Contains("payload.sourceBytesPerSecond", renderCopying, StringComparison.Ordinal);
        Assert.Contains("payload.sourceAverageBytesPerSecond", renderCopying, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.currentBytesPerSecond", renderCopying, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.averageBytesPerSecond", renderCopying, StringComparison.Ordinal);
        Assert.Contains("target.state === 'copying'", renderTarget, StringComparison.Ordinal);
        Assert.Contains("formatSpeed(target.bytesPerSecond)", renderTarget, StringComparison.Ordinal);
        Assert.Contains("写入速度 ${formatSpeed(target.bytesPerSecond)}", renderTarget, StringComparison.Ordinal);
        Assert.Contains("已校验 ${formatBytes(target.verificationBytes)}", renderTarget, StringComparison.Ordinal);
        Assert.Contains("校验速度 ${formatSpeed(target.verificationBytesPerSecond)}", renderTarget, StringComparison.Ordinal);
        Assert.Contains("formatEta(target.verificationEtaSeconds)", renderTarget, StringComparison.Ordinal);
        Assert.Contains("safeText(target.currentFile, '')", renderTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("currentBytesPerSecond", renderTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("averageBytesPerSecond", renderTarget, StringComparison.Ordinal);
        Assert.Contains("payload.targets.length !== requiredCount", renderCopying, StringComparison.Ordinal);
        Assert.Contains("byId('localTargetCard').hidden = !requiresLocal", renderCopying, StringComparison.Ordinal);
        Assert.Contains("byId('nasTargetCard').hidden = !requiresNas", renderCopying, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.targets.length === 2", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Target_mode_is_validated_before_copying_or_completion_rendering()
    {
        string script = ReadWebUi("scripts", "app.js");
        string normalize = Between(script, "  function normalizeTargetMode", "  function showScreen");
        string renderCopying = Between(script, "  function renderCopying", "  function renderFailure");
        string renderComplete = Between(script, "  function renderComplete", "  function renderStatus");

        Assert.Contains("['nas-only', 'local-only', 'local-and-nas'].includes(value)", normalize, StringComparison.Ordinal);
        Assert.Contains("throw new Error('保存方式无效')", normalize, StringComparison.Ordinal);
        Assert.Contains("normalizeTargetMode(payload.targetMode)", renderCopying, StringComparison.Ordinal);
        Assert.Contains("normalizeTargetMode(payload.targetMode)", renderComplete, StringComparison.Ordinal);
    }
    [Fact]
    public void Setup_supports_multiple_camera_templates_without_dropping_card_profiles()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");
        string collect = Between(script, "  function collectConfiguration", "  function submitSetup");

        Assert.Contains("id=\"cameraTemplateSelect\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"addCameraTemplateBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"setDefaultCameraTemplateBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"removeCameraTemplateBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("setCameraTemplates(configuration)", script, StringComparison.Ordinal);
        Assert.Contains("defaultCameraTemplateId", collect, StringComparison.Ordinal);
        Assert.Contains("cameraTemplates: normalizedTemplates", collect, StringComparison.Ordinal);
        Assert.Contains("cardProfiles", collect, StringComparison.Ordinal);
        Assert.Contains("已有素材卡使用此模板，不能删除", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_collapses_redundant_nested_source_directories_before_save()
    {
        string script = ReadWebUi("scripts", "app.js");
        string normalize = Between(script, "  function normalizeSourceDirectories", "  function renderSourceDirectories");
        string collect = Between(script, "  function collectConfiguration", "  function submitSetup");

        Assert.Contains("key.startsWith(`${parent}\\\\`)", normalize, StringComparison.Ordinal);
        Assert.Contains("existing.startsWith(`${key}\\\\`)", normalize, StringComparison.Ordinal);
        Assert.Contains("normalizeSourceDirectories(template.approvedSourceDirectories)", collect, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_screen_exposes_sheet_confirmed_fresh_restart_only_for_recoverable_old_tasks()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");

        Assert.Contains("id=\"restartFreshBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("restartFreshBtn\" type=\"button\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("'task.restartFresh'", script, StringComparison.Ordinal);
        Assert.Contains("failure.canRestartFresh", script, StringComparison.Ordinal);
        Assert.Contains("restartFreshBtn').hidden = canRestartFresh !== true", script, StringComparison.Ordinal);
        Assert.Contains("requestCardDecision({", script, StringComparison.Ordinal);
        Assert.Contains("command: 'task.restartFresh'", script, StringComparison.Ordinal);
        Assert.Contains("payload: { ...target, confirmed: true }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_screen_exposes_confirmed_card_reinitialization_only_when_runtime_allows_it()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");

        Assert.Contains("id=\"reinitializeCardBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("reinitializeCardBtn\" type=\"button\" hidden", html, StringComparison.Ordinal);
        Assert.Contains("'card.reinitialize'", script, StringComparison.Ordinal);
        Assert.Contains("failure.canReinitializeCard", script, StringComparison.Ordinal);
        Assert.Contains("reinitializeCardBtn').hidden = canReinitializeCard !== true", script, StringComparison.Ordinal);
        Assert.Contains("旧任务、回执和历史基线会保留", script, StringComparison.Ordinal);
        Assert.Contains("源卡不会被写入", script, StringComparison.Ordinal);
        Assert.Contains("command: 'card.reinitialize'", script, StringComparison.Ordinal);
        Assert.Contains("payload: { ...target, confirmed: true }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_screen_distinguishes_same_card_reader_change_from_new_card_reinitialization()
    {
        string html = ReadWebUi("index.html");
        string script = ReadWebUi("scripts", "app.js");

        Assert.Contains("id=\"reassociateCardBtn\"", html, StringComparison.Ordinal);
        Assert.Contains("这是同一张卡，继续使用", html, StringComparison.Ordinal);
        Assert.Contains("failure.canReassociateCard", script, StringComparison.Ordinal);
        Assert.Contains("reassociateCardBtn').hidden = canReassociateCard !== true", script, StringComparison.Ordinal);
        Assert.Contains("command: 'card.reassociate'", script, StringComparison.Ordinal);
        Assert.Contains("payload: { ...target, confirmed: true }", script, StringComparison.Ordinal);
        Assert.Contains("不会把当前内容直接算作已导入", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sensitive_bridge_timeout_unlocks_submit_and_refreshes_authoritative_state()
    {
        string script = ReadWebUi("scripts", "app.js");
        string expire = Between(script, "  function expireRequest", "  function postCommand");
        string response = Between(script, "  function handleResponse", "  function receiveNativeMessage");

        Assert.Contains("const REQUEST_TIMEOUT_MS = 30000", script, StringComparison.Ordinal);
        Assert.Contains("window.setTimeout(() => expireRequest(requestKey), REQUEST_TIMEOUT_MS)", script, StringComparison.Ordinal);
        Assert.Contains("pendingRequests.delete(requestId)", script, StringComparison.Ordinal);
        Assert.Contains("window.clearTimeout(pending.timeoutId)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pending.timedOut = true", script, StringComparison.Ordinal);
        Assert.Contains("setupSubmitBtn').disabled = false", script, StringComparison.Ordinal);
        Assert.Contains("postCommand('configuration.get')", script, StringComparison.Ordinal);
        Assert.Contains("postCommand('status.refresh')", script, StringComparison.Ordinal);
        Assert.Contains("无需重启应用", script, StringComparison.Ordinal);
        Assert.True(
            expire.IndexOf("pendingRequests.delete(requestId)", StringComparison.Ordinal) <
            expire.IndexOf("postCommand('configuration.get')", StringComparison.Ordinal));
        Assert.Contains("if (!pending) return;", response, StringComparison.Ordinal);
    }
    [Fact]
    public void Corrupt_configuration_recovery_notice_is_visible_in_first_run_setup()
    {
        string script = ReadWebUi("scripts", "app.js");
        string renderConfiguration = Between(script, "  function renderConfiguration", "  function formatBytes");

        Assert.Contains("configuration.recoveryNotice", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("byId('setupError').textContent", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("原设置已保留，请重新完成设置", renderConfiguration, StringComparison.Ordinal);
    }

    [Fact]
    public void A_late_valid_configuration_releases_only_the_automatic_first_run_screen()
    {
        string script = ReadWebUi("scripts", "app.js");
        string renderConfiguration = Between(script, "  function renderConfiguration", "  function formatBytes");

        Assert.Contains("const wasAutomaticFirstRun = setupFirstRun", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("else if (wasAutomaticFirstRun)", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("setupFirstRun = false", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("byId('app').classList.remove('first-run')", renderConfiguration, StringComparison.Ordinal);
        Assert.Contains("if (currentScreen === 'setup') showScreen('home')", renderConfiguration, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_attribute_cannot_be_overridden_by_component_display_rules()
    {
        string styles = ReadWebUi("styles", "app.css");

        Assert.Contains("[hidden]{display:none!important}", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Simplified_digit_animation_has_been_removed()
    {
        string styles = ReadWebUi("styles", "product.css");
        string script = ReadWebUi("scripts", "app.js");

        Assert.DoesNotContain("digitSettle", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("digit-slot.changed", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.add('changed')", script, StringComparison.Ordinal);
    }

    private static string ReadWebUi(params string[] segments)
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine([root, "src", "AutoCardSync.Standalone", "WebUI", .. segments]));
    }

    private static string Between(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing marker: {start}");
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing marker: {end}");
        return source[startIndex..endIndex];
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
