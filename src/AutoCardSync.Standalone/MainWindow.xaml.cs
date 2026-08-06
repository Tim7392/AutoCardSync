using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using AutoCardSync.Agent.Service.Devices;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using WinForms = System.Windows.Forms;

namespace AutoCardSync.Standalone;

public partial class MainWindow : Window
{
    private const int SchemaVersion = StandaloneWebMessageProtocol.SchemaVersion;
    private const string VirtualHostName = "app.autocardsync.local";
    private const string LocalOrigin = "https://app.autocardsync.local/";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ILogger<MainWindow> _logger;
    private readonly StandaloneConfigurationService _configurationService;
    private readonly StandaloneRuntimeService _runtimeService;
    private readonly StandaloneDataPaths _paths;
    private WinForms.NotifyIcon? _trayIcon;
    private readonly Dictionary<string, string> _mediaNotificationStates = new(StringComparer.Ordinal);
    private TrayVisualState? _trayVisualState;
    private bool _explicitExit;
    private bool _webViewReady;

    public MainWindow(
        ILogger<MainWindow> logger,
        StandaloneConfigurationService configurationService,
        StandaloneRuntimeService runtimeService,
        StandaloneDataPaths paths)
    {
        _logger = logger;
        _configurationService = configurationService;
        _runtimeService = runtimeService;
        _paths = paths;
        _runtimeService.StatusChanged += OnRuntimeStatusChanged;
        _runtimeService.MediaStatusChanged += OnMediaStatusChanged;
        InitializeComponent();
        Closing += OnClosing;
        Loaded += OnLoaded;
        CreateTrayIcon();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string webRoot = Path.Combine(AppContext.BaseDirectory, "WebUI");
            if (!Directory.Exists(webRoot))
                throw new DirectoryNotFoundException("安装内容中缺少本地界面资源。");

            string userDataFolder = _paths.WebViewDataDirectory;
            Directory.CreateDirectory(userDataFolder);
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions("--disable-features=msWebOOUI,msPdfOOUI"));
            await WebView.EnsureCoreWebView2Async(environment);

            CoreWebView2 core = WebView.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            ConfigureWebViewSecurity(core);
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.WebMessageReceived += OnWebMessageReceived;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, args) => ShowFallback(
                $"本地界面进程异常：{args.ProcessFailedKind}。当前任务不会被标记为安全完成。");
            core.Navigate(LocalOrigin + "index.html");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to initialize the standalone WebView2 UI.");
            bool runtimeMissing = exception is WebView2RuntimeNotFoundException;
            ShowFallback(
                runtimeMissing
                    ? "缺少 Microsoft Edge WebView2 Runtime。请重新运行安装程序。"
                    : "本地界面初始化失败。可先备份并重建本地界面缓存；当前任务不会被标记为安全完成。",
                canRepairProfile: !runtimeMissing);
        }
    }

    private static void ConfigureWebViewSecurity(CoreWebView2 core)
    {
        CoreWebView2Settings settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Debugger.IsAttached;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!IsAllowedLocalUri(args.Uri))
        {
            args.Cancel = true;
            _logger.LogWarning("Blocked WebView2 navigation to {Uri}.", args.Uri);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess || WebView.CoreWebView2 is null || !IsAllowedLocalUri(WebView.CoreWebView2.Source))
        {
            ShowFallback("本地界面加载失败。当前任务不会被标记为安全完成。");
            return;
        }

        FallbackPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;
        _webViewReady = true;
        await PushConfigurationAsync();
        await PushStatusAsync();
        await PushMediaStatusAsync();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string requestId = string.Empty;
        try
        {
            if (!Uri.TryCreate(args.Source, UriKind.Absolute, out Uri? source) ||
                !string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("消息来源无效。");
            }

            using JsonDocument document = JsonDocument.Parse(args.WebMessageAsJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.GetInt32() != SchemaVersion)
            {
                throw new InvalidDataException("消息版本无效。");
            }

            requestId = RequiredString(root, "requestId");
            string type = RequiredString(root, "type");
            JsonElement payload = root.TryGetProperty("payload", out JsonElement payloadElement)
                ? payloadElement
                : default;

            switch (type)
            {
                case "ui.ready":
                    SendResponse(requestId, true, message: "界面已就绪。");
                    await PushConfigurationAsync();
                    await PushStatusAsync();
                    await PushMediaStatusAsync();
                    break;
                case "configuration.get":
                    SendResponse(requestId, true,
                        data: await _configurationService.GetAsync(CancellationToken.None));
                    break;
                case "configuration.save":
                    {
                        JsonElement configurationPayload = payload;
                        string? preferredMountSessionId = null;
                        if (payload.ValueKind == JsonValueKind.Object &&
                            payload.TryGetProperty("configuration", out JsonElement nestedConfiguration))
                        {
                            configurationPayload = nestedConfiguration;
                            preferredMountSessionId = RequiredString(payload, "mountSessionId");
                        }
                        StandaloneConfigurationDto dto = configurationPayload.Deserialize<StandaloneConfigurationDto>(WebJson) ??
                            throw new InvalidDataException("设置内容无效。");
                        StandaloneConfigurationDto saved;
                        using (_runtimeService.BeginConfigurationChange())
                        {
                            saved = await _configurationService.SaveAsync(
                                dto, CancellationToken.None);
                        }
                        string message = "设置已保存并已开始检测素材卡。";
                        try
                        {
                            await _runtimeService.NotifyConfigurationChangedAsync(
                                saved,
                                CancellationToken.None,
                                startMountedVolume: preferredMountSessionId is null);
                            if (preferredMountSessionId is not null)
                            {
                                StandaloneRuntimeOperationResult result =
                                    _runtimeService.StartPreferredMountedVolumeAfterConfiguration(
                                        preferredMountSessionId,
                                        CancellationToken.None);
                                message = result.Message;
                            }
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                "设置已保存，但素材卡重新检测失败。请保持素材卡原位并重试刷新。",
                                exception);
                        }
                        SendResponse(requestId, true, data: saved, message: message);
                        PostMessage("standalone.configuration", saved);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "picker.selectFolder":
                    SendResponse(requestId, true, data: SelectFolder(
                        RequiredString(payload, "purpose"),
                        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("mountSessionId", out JsonElement mountSession)
                            ? mountSession.GetString()
                            : null));
                    break;
                case "status.refresh":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.RefreshAsync(CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "media.defer":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.DeferCurrentBlockedVolumeAsync(
                                RequiredString(payload, "mountSessionId"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "history.get":
                    {
                        IReadOnlyList<StandaloneImportHistoryItemDto> history =
                            await _runtimeService.GetImportHistoryAsync(CancellationToken.None);
                        SendResponse(requestId, true, data: history, message: $"已读取 {history.Count} 条本机同步记录。");
                        break;
                    }
                case "media.prioritize":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.PrioritizeMountedVolumeAsync(
                                RequiredString(payload, "mountSessionId"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "storage.test":
                    SendResponse(requestId, true, data: TestStorageTarget(payload), message: "保存位置可访问且可写入。");
                    break;
                case "task.retry":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.RetryMountedVolumeAsync(
                                RequiredString(payload, "mountSessionId"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "task.restartFresh":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.RestartFreshMountedVolumeAsync(
                                RequiredString(payload, "mountSessionId"),
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "card.reassociate":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReassociateMountedCardAsync(
                                RequiredString(payload, "mountSessionId"),
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "card.reinitialize":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReinitializeMountedCardAsync(
                                RequiredString(payload, "mountSessionId"),
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushConfigurationAsync();
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "card.profile.rename":
                    {
                        StandaloneConfigurationDto configuration =
                            await _configurationService.RenameCardProfileAsync(
                                RequiredGuid(payload, "cardInstanceId"),
                                RequiredString(payload, "displayName", 64),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: configuration, message: "素材卡名称已更新。");
                        PostMessage("standalone.configuration", configuration);
                        break;
                    }
                case "card.initialize":
                    {
                        if (payload.ValueKind != JsonValueKind.Object ||
                            !payload.TryGetProperty("configuration", out JsonElement configurationPayload))
                        {
                            throw new InvalidDataException("素材卡初始化内容无效。");
                        }
                        StandaloneConfigurationDto dto =
                            configurationPayload.Deserialize<StandaloneConfigurationDto>(WebJson) ??
                            throw new InvalidDataException("素材卡初始化内容无效。");
                        string mountSessionId = RequiredString(payload, "mountSessionId");
                        Guid cameraTemplateId = RequiredGuid(payload, "cameraTemplateId");
                        bool confirmed = RequiredBoolean(payload, "confirmed");
                        if (!confirmed)
                            throw new InvalidOperationException("软件初始化素材卡需要明确确认。");

                        StandaloneConfigurationDto originalConfiguration =
                            await _configurationService.GetAsync(CancellationToken.None);
                        try
                        {
                            StandaloneConfigurationDto saved =
                                await _configurationService.SaveCardInitializationAsync(
                                    dto, cameraTemplateId, CancellationToken.None);
                            await _runtimeService.NotifyConfigurationChangedAsync(
                                saved, CancellationToken.None, startMountedVolume: false);
                            StandaloneRuntimeOperationResult result =
                                await _runtimeService.InitializeMountedCardAsync(
                                    mountSessionId, cameraTemplateId, confirmed, CancellationToken.None);
                            StandaloneConfigurationDto finalConfiguration =
                                await _configurationService.GetAsync(CancellationToken.None);
                            if (!result.ManagedCardInitializationCommitted)
                            {
                                using (_runtimeService.BeginConfigurationChange())
                                {
                                    finalConfiguration = await _configurationService.RestoreSnapshotAsync(
                                        originalConfiguration, CancellationToken.None);
                                }
                                await _runtimeService.NotifyConfigurationChangedAsync(
                                    finalConfiguration, CancellationToken.None, startMountedVolume: false);
                                SendResponse(
                                    requestId,
                                    false,
                                    error: result.Message +
                                        " 软件初始化事务未提交；新建的素材范围和卡片记录没有保留，原有全局设置未改变。");
                                PostMessage("standalone.configuration", finalConfiguration);
                                await PushStatusAsync();
                                await PushMediaStatusAsync();
                                break;
                            }
                            SendResponse(requestId, true,
                                data: new { configuration = finalConfiguration, status = result.Status },
                                message: result.Message);
                            PostMessage("standalone.configuration", finalConfiguration);
                            await PushStatusAsync();
                            await PushMediaStatusAsync();
                        }
                        catch
                        {
                            StandaloneConfigurationDto restored;
                            using (_runtimeService.BeginConfigurationChange())
                            {
                                restored = await _configurationService.RestoreSnapshotAsync(
                                    originalConfiguration, CancellationToken.None);
                            }
                            await _runtimeService.NotifyConfigurationChangedAsync(
                                restored, CancellationToken.None, startMountedVolume: false);
                            PostMessage("standalone.configuration", restored);
                            throw;
                        }
                        break;
                    }
                case "card.profile.configure":
                    {
                        if (payload.ValueKind != JsonValueKind.Object ||
                            !payload.TryGetProperty("configuration", out JsonElement configurationPayload))
                        {
                            throw new InvalidDataException("素材卡范围设置内容无效。");
                        }
                        StandaloneConfigurationDto dto =
                            configurationPayload.Deserialize<StandaloneConfigurationDto>(WebJson) ??
                            throw new InvalidDataException("素材卡范围设置内容无效。");
                        Guid cardInstanceId = RequiredGuid(payload, "cardInstanceId");
                        Guid cameraTemplateId = RequiredGuid(payload, "cameraTemplateId");
                        bool confirmed = RequiredBoolean(payload, "confirmed");
                        string expectedVolumeKey =
                            _runtimeService.CaptureManagedCardReinitializationVolumeKey(cardInstanceId);
                        StandaloneConfigurationDto originalConfiguration =
                            await _configurationService.GetAsync(CancellationToken.None);
                        StandaloneConfigurationDto saved =
                            await _configurationService.SaveCardInitializationAsync(
                                dto, cameraTemplateId, CancellationToken.None);
                        await _runtimeService.NotifyConfigurationChangedAsync(
                            saved,
                            CancellationToken.None,
                            startMountedVolume: false);
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReinitializeManagedCardAsync(
                                cardInstanceId,
                                cameraTemplateId,
                                expectedVolumeKey,
                                confirmed,
                                CancellationToken.None);
                        StandaloneConfigurationDto finalConfiguration =
                            await _configurationService.GetAsync(CancellationToken.None);
                        JsonElement resultStatus = JsonSerializer.SerializeToElement(result.Status, WebJson);
                        bool failed = resultStatus.ValueKind == JsonValueKind.Object &&
                            resultStatus.TryGetProperty("view", out JsonElement view) &&
                            string.Equals(view.GetString(), "failure", StringComparison.Ordinal);
                        string operationMessage = ManagedCardOperationMessage(result, resultStatus);
                        if (!result.ManagedCardInitializationCommitted)
                        {
                            using (_runtimeService.BeginConfigurationChange())
                            {
                                finalConfiguration = await _configurationService.RestoreSnapshotAsync(
                                    originalConfiguration,
                                    CancellationToken.None);
                            }
                            await _runtimeService.NotifyConfigurationChangedAsync(
                                finalConfiguration,
                                CancellationToken.None,
                                startMountedVolume: false);
                            SendResponse(
                                requestId,
                                false,
                                error: operationMessage +
                                    " 素材卡初始化事务尚未提交，本次新建的素材范围和其他设置未保留；未完成事务证据仍保留供恢复。");
                            PostMessage("standalone.configuration", finalConfiguration);
                            await PushStatusAsync();
                            await PushMediaStatusAsync();
                            break;
                        }
                        if (failed)
                        {
                            SendResponse(
                                requestId,
                                false,
                                error: operationMessage + " 新素材范围绑定已经保存，但当前复制或校验尚未安全完成；请按失败页继续恢复。");
                            PostMessage("standalone.configuration", finalConfiguration);
                            await PushStatusAsync();
                            await PushMediaStatusAsync();
                            break;
                        }
                        SendResponse(
                            requestId,
                            true,
                            data: new { configuration = finalConfiguration, status = result.Status },
                            message: result.Message);
                        PostMessage("standalone.configuration", finalConfiguration);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "card.profile.reinitialize":
                    {
                        Guid cardInstanceId = RequiredGuid(payload, "cardInstanceId");
                        Guid cameraTemplateId = RequiredGuid(payload, "cameraTemplateId");
                        string expectedVolumeKey =
                            _runtimeService.CaptureManagedCardReinitializationVolumeKey(cardInstanceId);
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReinitializeManagedCardAsync(
                                cardInstanceId,
                                cameraTemplateId,
                                expectedVolumeKey,
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushConfigurationAsync();
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "card.confirmSourceCleanup":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ConfirmMountedSourceCleanupAsync(
                                RequiredString(payload, "mountSessionId"),
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        await PushMediaStatusAsync();
                        break;
                    }
                case "task.cancel":
                    Guid? expectedOperationId = TryGetGuid(payload, "operationId");
                    SendResponse(requestId, true, data: await _runtimeService.CancelAsync(expectedOperationId), message: "已处理停止请求。");
                    break;
                case "window.drag":
                    TryDragWindow();
                    SendResponse(requestId, true, message: "窗口拖动已开始。");
                    break;
                case "window.close":
                    Hide();
                    SendResponse(requestId, true, message: "已关闭到托盘，后台检测会继续运行。");
                    break;
                case "window.minimize":
                    WindowState = WindowState.Minimized;
                    SendResponse(requestId, true, message: "窗口已最小化。");
                    break;
                case "window.maximize":
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    SendResponse(requestId, true, message: "窗口显示状态已更新。");
                    break;
                case "window.exit":
                    if (HasActiveTransfer() && !IsConfirmed(payload, "confirmed"))
                    {
                        SendResponse(
                            requestId,
                            false,
                            error: "素材卡仍在处理。请确认停止本次处理后再退出；未确认前，AutoCardSync 不会中断任务。");
                        break;
                    }
                    SendResponse(requestId, true, message: "正在安全退出 AutoCardSync。");
                    _ = Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(ExitApplication));
                    break;
                default:
                    SendResponse(requestId, false, error: "不支持的界面请求。");
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Standalone UI bridge request failed closed.");
            if (!string.IsNullOrWhiteSpace(requestId))
                SendResponse(requestId, false, error: exception.Message);
        }
    }

    private async Task PushConfigurationAsync()
    {
        StandaloneConfigurationDto configuration = await _configurationService.GetAsync(CancellationToken.None);
        PostMessage("standalone.configuration", configuration);
    }

    private Task PushStatusAsync()
    {
        PostMessage("standalone.status", _runtimeService.GetStatusSnapshot());
        return Task.CompletedTask;
    }

    private Task PushMediaStatusAsync()
    {
        StandaloneMediaStatusDto status = _runtimeService.GetMediaStatusSnapshot();
        PostMessage("standalone.mediaStatus", status);
        UpdateTrayIcon(status);
        return Task.CompletedTask;
    }

    private void OnRuntimeStatusChanged(object? sender, object status) =>
        Dispatcher.Invoke(() => PostMessage("standalone.status", status));

    private void OnMediaStatusChanged(object? sender, StandaloneMediaStatusDto status)
    {
        if (_explicitExit || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_explicitExit)
                return;
            PublishMediaStatus(status);
        }));
    }

    private void PublishMediaStatus(StandaloneMediaStatusDto status)
    {
        PostMessage("standalone.mediaStatus", status);
        UpdateTrayIcon(status);

        foreach (StandaloneMediaItemDto media in status.Media)
        {
            if (string.Equals(media.PresenceState, "removed", StringComparison.Ordinal))
            {
                _mediaNotificationStates.Remove(MediaNotificationKey(media));
                continue;
            }

            if (!TryGetMediaNotification(media, out string state, out string title, out string message,
                    out WinForms.ToolTipIcon icon))
            {
                continue;
            }

            string key = MediaNotificationKey(media);
            if (_mediaNotificationStates.TryGetValue(key, out string? previous) &&
                string.Equals(previous, state, StringComparison.Ordinal))
            {
                continue;
            }

            _mediaNotificationStates[key] = state;
            ShowTrayNotification(title, message, icon);
        }
    }

    private void UpdateTrayIcon(StandaloneMediaStatusDto status)
    {
        if (_trayIcon is null)
            return;

        StandaloneMediaItemDto? active = status.Media.FirstOrDefault(media =>
            string.Equals(media.MountSessionId, status.ActiveMountSessionId, StringComparison.Ordinal));
        StandaloneMediaItemDto? mounted = active ?? status.Media.FirstOrDefault(IsProcessingMedia) ??
            status.Media.FirstOrDefault(NeedsAttention) ??
            status.Media.FirstOrDefault(media =>
                string.Equals(media.PresenceState, "mounted", StringComparison.Ordinal) ||
                string.Equals(media.PresenceState, "detecting", StringComparison.Ordinal));
        TrayVisualState visualState = ResolveTrayVisualState(mounted);
        ApplyTrayVisualState(visualState);
        _trayIcon.Text = mounted is null
            ? "AutoCardSync - 等待插入素材卡"
            : TrimTrayText($"AutoCardSync - {MediaLabel(mounted)}：{TrayStateLabel(mounted)}");
    }

    private void ApplyTrayVisualState(TrayVisualState visualState)
    {
        if (_trayIcon is null || _trayVisualState == visualState)
            return;

        try
        {
            System.Drawing.Icon next = TrayStatusIconFactory.Create(visualState);
            System.Drawing.Icon? previous = _trayIcon.Icon;
            _trayIcon.Icon = next;
            _trayVisualState = visualState;
            previous?.Dispose();
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Unable to update the tray icon for state {TrayVisualState}.", visualState);
        }
    }

    private static TrayVisualState ResolveTrayVisualState(StandaloneMediaItemDto? media)
    {
        if (media is null || string.Equals(media.PresenceState, "detecting", StringComparison.Ordinal))
            return TrayVisualState.Waiting;
        if (string.Equals(media.SafetyConclusion, "approved_material_verified", StringComparison.Ordinal))
            return TrayVisualState.Completed;
        if (IsProcessingMedia(media))
            return TrayVisualState.Processing;
        return NeedsAttention(media) ? TrayVisualState.Attention : TrayVisualState.Waiting;
    }

    private static bool IsProcessingMedia(StandaloneMediaItemDto media) =>
        media.WorkState is "scanning" or "copying" or "verifying" ||
        string.Equals(media.ReasonCode, "CARD_TASK_ACTIVE", StringComparison.Ordinal);

    private static bool NeedsAttention(StandaloneMediaItemDto media) =>
        media.WorkState is "blocked" or "awaiting_action" ||
        media.IdentityState is "needs_confirmation" or "identity_conflict" or "recovery_required";

    private static bool TryGetMediaNotification(
        StandaloneMediaItemDto media,
        out string state,
        out string title,
        out string message,
        out WinForms.ToolTipIcon icon)
    {
        string card = MediaLabel(media);
        if (string.Equals(media.SafetyConclusion, "approved_material_verified", StringComparison.Ordinal))
        {
            state = "completed";
            title = "素材卡已完成";
            message = $"{card} 纳入素材范围的文件已完成复制和完整校验，可以拔卡。";
            icon = WinForms.ToolTipIcon.Info;
            return true;
        }

        if (string.Equals(media.PresenceState, "detecting", StringComparison.Ordinal) ||
            string.Equals(media.ReasonCode, "DETECTED_EXTERNAL_MEDIA", StringComparison.Ordinal) ||
            string.Equals(media.ReasonCode, "MEDIA_DETECTED", StringComparison.Ordinal))
        {
            state = "detected";
            title = "已检测到素材卡";
            message = $"已检测到 {card}，正在识别素材卡身份和素材范围。";
            icon = WinForms.ToolTipIcon.Info;
            return true;
        }

        if (string.Equals(media.WorkState, "blocked", StringComparison.Ordinal))
        {
            state = "blocked:" + media.ReasonCode;
            title = "素材卡未安全完成";
            message = $"{card} 未安全完成。{MediaDetail(media, "请查看恢复操作后再继续使用这张卡。")}";
            icon = WinForms.ToolTipIcon.Error;
            return true;
        }

        if (string.Equals(media.WorkState, "awaiting_action", StringComparison.Ordinal) ||
            media.IdentityState is "needs_confirmation" or "identity_conflict" or "recovery_required")
        {
            state = "action:" + media.ReasonCode + ":" + media.IdentityState;
            title = "素材卡需要你的决定";
            message = $"{card} 需要你的决定。{MediaDetail(media, "当前尚未开始复制，也没有安全完成结论。")}";
            icon = WinForms.ToolTipIcon.Warning;
            return true;
        }

        state = string.Empty;
        title = string.Empty;
        message = string.Empty;
        icon = WinForms.ToolTipIcon.None;
        return false;
    }

    private void ShowTrayNotification(string title, string message, WinForms.ToolTipIcon icon)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(5000, title, TrimTrayText(message), icon);
        }
        catch (ObjectDisposedException)
        {
            // The explicit-exit path disposes the tray icon while a queued status update may still arrive.
        }
    }

    private static string MediaNotificationKey(StandaloneMediaItemDto media) =>
        !string.IsNullOrWhiteSpace(media.MountSessionId)
            ? media.MountSessionId
            : media.VolumeKey;

    private static string MediaLabel(StandaloneMediaItemDto media)
    {
        string name = string.IsNullOrWhiteSpace(media.CardDisplayName) ? "素材卡" : media.CardDisplayName.Trim();
        string drive = string.IsNullOrWhiteSpace(media.DriveLetter) ? "未知盘符" : media.DriveLetter.Trim();
        return $"{name}（{drive}）";
    }

    private static string TrayStateLabel(StandaloneMediaItemDto media) =>
        string.Equals(media.SafetyConclusion, "approved_material_verified", StringComparison.Ordinal)
            ? "已完成完整校验"
            : media.WorkState switch
            {
                "queued" => "等待处理",
                "scanning" => "正在识别",
                "copying" => "正在复制",
                "verifying" => "正在校验",
                "awaiting_action" => "需要决定",
                "blocked" => "未安全完成",
                "deferred" => "已暂缓",
                _ => "已检测到",
            };

    private static string MediaDetail(StandaloneMediaItemDto media, string fallback) =>
        string.IsNullOrWhiteSpace(media.Detail) ? fallback : TrimTrayText(media.Detail);

    private static string TrimTrayText(string value) =>
        value.Length <= 63 ? value : value[..60] + "…";

    private object TestStorageTarget(JsonElement payload)
    {
        string purpose = RequiredString(payload, "purpose", 32);
        if (purpose is not ("localTarget" or "nasMappedTarget"))
            throw new InvalidDataException("只能测试本地保存目标或已映射的 NAS 保存目标。");

        string selectedPath = RequiredStorageTargetPath(payload);
        if (!Directory.Exists(selectedPath))
        {
            throw new InvalidDataException(
                $"{StoragePurposeLabel(purpose)}不存在或暂时不可访问。请连接保存位置后重试。");
        }

        RejectReparsePointPath(selectedPath);
        RejectMountedCardOverlap(selectedPath);

        string root = Path.GetPathRoot(selectedPath) ??
            throw new InvalidDataException("无法确定保存位置所在的盘符。");
        if (!IsDriveRoot(root))
        {
            throw new InvalidDataException(
                purpose == "nasMappedTarget"
                    ? "NAS 保存位置必须使用已映射的盘符路径。请先在 Windows 中映射后再测试。"
                    : "本地保存位置必须使用可访问的本地盘符路径。请重新选择一个本地文件夹。");
        }

        DriveInfo drive;
        try
        {
            drive = new DriveInfo(root);
            if (!drive.IsReady)
                throw new IOException("保存位置所在的盘符尚未就绪。");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"无法读取{StoragePurposeLabel(purpose)}的可用空间。请检查盘符和连接状态后重试。",
                exception);
        }

        long availableBytes;
        try
        {
            availableBytes = drive.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"无法读取{StoragePurposeLabel(purpose)}的可用空间。请检查盘符和连接状态后重试。",
                exception);
        }
        if (availableBytes <= 0)
            throw new InvalidDataException($"{StoragePurposeLabel(purpose)}没有可用空间，无法用作保存位置。");

        VerifyTargetWriteAccess(selectedPath, purpose);
        return new
        {
            displayPath = selectedPath,
            availableBytes,
            writable = true,
        };
    }

    private static string RequiredStorageTargetPath(JsonElement payload)
    {
        string path = RequiredString(payload, "path", 32767).Trim();
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("保存位置必须是绝对路径。请重新选择一个文件夹。");

        try
        {
            return NormalizeDirectoryPath(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("保存路径无效。请重新选择一个可访问的文件夹。", exception);
        }
    }

    private void RejectMountedCardOverlap(string selectedPath)
    {
        foreach (StandaloneMediaItemDto media in _runtimeService.GetMediaStatusSnapshot().Media)
        {
            if (string.Equals(media.PresenceState, "removed", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(media.DriveLetter))
            {
                continue;
            }

            string cardRoot;
            try
            {
                cardRoot = NormalizeDirectoryPath(Path.GetFullPath(media.DriveLetter));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogDebug(exception, "Skipping an invalid mounted-media path while validating a storage target.");
                continue;
            }

            if (PathsOverlap(selectedPath, cardRoot))
            {
                throw new InvalidDataException(
                    $"不能将当前已挂载的素材卡 {MediaLabel(media)} 或其子目录设为保存位置。请选择另一块本地磁盘或已映射的 NAS 盘符。");
            }
        }
    }

    private void VerifyTargetWriteAccess(string targetDirectory, string purpose)
    {
        string probePath = Path.Combine(targetDirectory, $".autocardsync-write-probe-{Guid.NewGuid():N}.tmp");
        bool probeCreated = false;
        Exception? writeFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 256,
                options: FileOptions.WriteThrough);
            probeCreated = true;
            byte[] probe = Guid.NewGuid().ToByteArray();
            stream.Write(probe, 0, probe.Length);
            stream.Flush(flushToDisk: true);
            File.SetAttributes(probePath, File.GetAttributes(probePath) | FileAttributes.Hidden);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            writeFailure = exception;
        }
        finally
        {
            try
            {
                if (probeCreated)
                    File.Delete(probePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                cleanupFailure = exception;
            }
        }

        if (cleanupFailure is not null)
        {
            _logger.LogError(cleanupFailure, "Storage write probe cleanup failed for {Purpose}.", purpose);
            string detail = writeFailure is null
                ? "写入验证后的临时检查文件无法清理。"
                : "写入验证失败，且临时检查文件无法清理。";
            throw new IOException(
                $"{StoragePurposeLabel(purpose)}{detail}请检查权限或安全软件后重试。",
                cleanupFailure);
        }

        if (writeFailure is not null)
        {
            throw new IOException(
                $"{StoragePurposeLabel(purpose)}不可写入。请检查权限、磁盘空间或网络连接后重试。",
                writeFailure);
        }
    }

    private static void RejectReparsePointPath(string path)
    {
        string root = Path.GetPathRoot(path) ?? throw new InvalidDataException("无法确定保存位置的根目录。");
        string current = root;
        foreach (string segment in path[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "保存位置不能经过快捷方式、符号链接或挂载点。请直接选择真实的本地或映射 NAS 文件夹。");
            }
        }
    }

    private static bool PathsOverlap(string first, string second) =>
        IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    private static bool IsSameOrDescendant(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(EnsureTrailingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDirectoryPath(string path)
    {
        string normalized = Path.GetFullPath(path);
        string root = Path.GetPathRoot(normalized) ?? normalized;
        return string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.TrimEndingDirectorySeparator(normalized);
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static bool IsDriveRoot(string root) =>
        root.Length == 3 && char.IsLetter(root[0]) && root[1] == ':' && root[2] == Path.DirectorySeparatorChar;

    private static string StoragePurposeLabel(string purpose) => purpose == "nasMappedTarget"
        ? "NAS 保存位置"
        : "本地保存位置";

    private bool HasActiveTransfer()
    {
        StandaloneMediaStatusDto mediaStatus = _runtimeService.GetMediaStatusSnapshot();
        if (mediaStatus.Media.Any(IsProcessingMedia))
            return true;

        JsonElement status = JsonSerializer.SerializeToElement(_runtimeService.GetStatusSnapshot(), WebJson);
        return status.ValueKind == JsonValueKind.Object &&
            status.TryGetProperty("view", out JsonElement view) &&
            string.Equals(view.GetString(), "copying", StringComparison.Ordinal);
    }

    private object SelectFolder(string purpose, string? mountSessionId) => purpose switch
    {
        "approvedSourceDirectories" => SelectSourceFolder(mountSessionId),
        "localTarget" => SelectTargetFolder("选择本地保存文件夹"),
        "nasMappedTarget" => SelectTargetFolder("选择已连接的第二保存目标"),
        _ => throw new InvalidDataException("文件夹选择请求无效。"),
    };

    private static object SelectTargetFolder(string description)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        return new
        {
            path = dialog.ShowDialog() == WinForms.DialogResult.OK ? dialog.SelectedPath : null,
        };
    }

    private object SelectSourceFolder(string? mountSessionId)
    {
        StandaloneMediaItemDto? mountedCard = null;
        if (!string.IsNullOrWhiteSpace(mountSessionId))
        {
            mountedCard = _runtimeService.GetMediaStatusSnapshot().Media.SingleOrDefault(media =>
                string.Equals(media.MountSessionId, mountSessionId, StringComparison.Ordinal) &&
                string.Equals(media.PresenceState, "mounted", StringComparison.Ordinal));
            if (mountedCard is null)
                throw new InvalidDataException("这张素材卡已移除或状态已变化，请重新插入后再选择素材范围。");
        }

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = mountedCard is null
                ? "选择素材卡内需要自动同步的文件夹"
                : $"为“{(string.IsNullOrWhiteSpace(mountedCard.CardDisplayName) ? mountedCard.DriveLetter : mountedCard.CardDisplayName)}”选择素材范围",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return new { path = (string?)null, displayPath = (string?)null, mountSessionId };

        string selectedPath = Path.GetFullPath(dialog.SelectedPath);
        string volumeRoot = mountedCard?.DriveLetter ?? Path.GetPathRoot(selectedPath) ??
            throw new InvalidDataException("无法确定所选文件夹所在的盘符。");
        volumeRoot = Path.GetFullPath(volumeRoot);
        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady)
            throw new InvalidDataException("所选素材卡当前不可访问，请保持插入后重试。");
        if (mountedCard is null)
        {
            bool isExternal;
            try
            {
                isExternal = new ExternalSourceVolumeClassifier().IsExternalStorage(new VolumeEventArgs(
                    volumeRoot, volumeRoot, drive.DriveFormat, drive.TotalSize, DateTimeOffset.UtcNow));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("无法确认所选文件夹属于外接素材卡，请从已插入素材卡进入配置。", exception);
            }
            if (!isExternal)
                throw new InvalidDataException("首次设置的源目录必须来自素材卡；卡级配置会按当前挂载卡直接校验。");
        }
        if (mountedCard is not null && !string.Equals(
                Path.GetPathRoot(selectedPath), Path.GetPathRoot(volumeRoot),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选文件夹不属于当前正在配置的素材卡，请从当前卡窗口中重新选择。");

        // The mounted session is the authoritative card boundary. Hardware type
        // classification is intentionally not a hard gate here because SD readers
        // are frequently reported as Fixed by Windows.
        SourceDirectorySelection selection = SourceDirectorySelectionResolver.Resolve(selectedPath, volumeRoot);
        return new
        {
            path = selection.RelativePath,
            relativePath = selection.RelativePath,
            displayPath = selection.RelativePath == "." ? "素材卡根目录（整张卡）" : selection.FullPath,
            mountSessionId,
            volumeKey = mountedCard?.VolumeKey,
        };
    }

    private static string ManagedCardOperationMessage(
        StandaloneRuntimeOperationResult result,
        JsonElement resultStatus)
    {
        if (resultStatus.ValueKind == JsonValueKind.Object &&
            resultStatus.TryGetProperty("failure", out JsonElement failure) &&
            failure.ValueKind == JsonValueKind.Object &&
            failure.TryGetProperty("what", out JsonElement what) &&
            what.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(what.GetString()))
        {
            return $"{result.Message} {what.GetString()}";
        }

        return result.Message;
    }

    private void SendResponse(
        string requestId,
        bool success,
        object? data = null,
        string? message = null,
        string? error = null) =>
        PostMessage("ui.response", new { success, data, message, error }, requestId);

    private void PostMessage(string type, object? payload = null, string? requestId = null)
    {
        if (_webViewReady && WebView.CoreWebView2 is not null)
            WebView.CoreWebView2.PostWebMessageAsJson(
                StandaloneWebMessageProtocol.Serialize(type, payload, requestId));
    }

    private static Guid? TryGetGuid(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return Guid.TryParse(value.GetString(), out Guid parsed) && parsed != Guid.Empty ? parsed : null;
    }

    private static Guid RequiredGuid(JsonElement root, string property) =>
        TryGetGuid(root, property) ??
        throw new InvalidDataException("界面请求中的素材卡或模板标识无效。");

    private static bool RequiredBoolean(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("界面确认信息无效。");
        }
        return value.GetBoolean();
    }

    private static bool IsConfirmed(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private static string RequiredString(JsonElement root, string property, int maxLength = 128)
    {
        if (!root.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("界面请求结构无效。");
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new InvalidDataException("界面请求标识无效。");
        return text;
    }

    private static bool IsAllowedLocalUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, VirtualHostName, StringComparison.OrdinalIgnoreCase);

    private void CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 AutoCardSync", null, (_, _) => ShowWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => RequestExitFromTray());

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "AutoCardSync - 等待插卡",
            Icon = LoadApplicationIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
        UpdateTrayIcon(_runtimeService.GetMediaStatusSnapshot());
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法解析 AutoCardSync 应用程序路径。");
        return System.Drawing.Icon.ExtractAssociatedIcon(executablePath)
            ?? throw new InvalidOperationException("AutoCardSync 应用程序图标资源缺失。");
    }

    internal void ActivateFromSecondaryLaunch() => ShowWindow();

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_explicitExit)
            return;
        e.Cancel = true;
        Hide();
    }

    private void RequestExitFromTray()
    {
        if (HasActiveTransfer())
        {
            WinForms.DialogResult confirmation = WinForms.MessageBox.Show(
                "素材卡仍在复制或校验。退出会停止本次处理，但恢复证据会被保留。\n\n确定要停止并退出吗？",
                "AutoCardSync",
                WinForms.MessageBoxButtons.YesNo,
                WinForms.MessageBoxIcon.Warning,
                WinForms.MessageBoxDefaultButton.Button2);
            if (confirmation != WinForms.DialogResult.Yes)
                return;
        }

        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_explicitExit)
            return;
        _explicitExit = true;
        _runtimeService.StatusChanged -= OnRuntimeStatusChanged;
        _runtimeService.MediaStatusChanged -= OnMediaStatusChanged;
        System.Drawing.Icon? trayImage = _trayIcon?.Icon;
        _trayIcon?.Dispose();
        _trayIcon = null;
        trayImage?.Dispose();
        _trayVisualState = null;
        WebView.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async void OnFallbackRepairClick(object sender, RoutedEventArgs e)
    {
        FallbackRepairButton.IsEnabled = false;
        FallbackMessage.Text = "正在备份本地界面缓存并准备重启…";
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.Root));
            string profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.WebViewDataDirectory));
            if (!string.Equals(Path.GetDirectoryName(profile), root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(profile), "WebView2", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("本地界面缓存路径不在预期的数据目录中，已拒绝重置。");
            }

            _webViewReady = false;
            WebView.Dispose();
            await Task.Delay(250);
            if (Directory.Exists(profile))
                CorruptStateFileRecovery.PreserveDirectory(profile);
            FallbackMessage.Text = "旧界面缓存已保留为备份，AutoCardSync 正在重启并创建全新界面缓存。";
            if (System.Windows.Application.Current is App app)
                app.RequestRestart();
            else
                throw new InvalidOperationException("无法请求 AutoCardSync 重启。");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(exception, "Unable to preserve and rebuild the WebView2 profile.");
            FallbackMessage.Text = "本地界面缓存未能重建：" + exception.Message + "。配置、素材卡基线和任务记录均未删除。";
            FallbackRepairButton.IsEnabled = true;
        }
    }

    private void OnFallbackCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
            Hide();
    }

    private void TryDragWindow()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        _ = ReleaseCapture();
        _ = SendMessage(handle, 0x00A1, new IntPtr(0x0002), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    private void ShowFallback(string message, bool canRepairProfile = true)
    {
        Dispatcher.Invoke(() =>
        {
            _webViewReady = false;
            FallbackTitle.Text = "AutoCardSync 界面无法启动";
            WebView.Visibility = Visibility.Hidden;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackMessage.Text = message;
            FallbackRepairButton.Visibility = canRepairProfile ? Visibility.Visible : Visibility.Collapsed;
            FallbackRepairButton.IsEnabled = true;
        });
    }
}
