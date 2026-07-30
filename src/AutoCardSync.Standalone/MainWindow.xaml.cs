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
                        StandaloneConfigurationDto dto = payload.Deserialize<StandaloneConfigurationDto>(WebJson) ??
                            throw new InvalidDataException("设置内容无效。");
                        using IDisposable configurationChange = _runtimeService.BeginConfigurationChange();
                        StandaloneConfigurationDto saved = await _configurationService.SaveAsync(
                            dto, CancellationToken.None);
                        try
                        {
                            await _runtimeService.NotifyConfigurationChangedAsync(saved, CancellationToken.None);
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                "设置已保存，但素材卡重新检测失败。请保持素材卡原位并重试刷新。",
                                exception);
                        }
                        SendResponse(requestId, true, data: saved, message: "设置已保存并已开始检测素材卡。");
                        PostMessage("standalone.configuration", saved);
                        await PushStatusAsync();
                        break;
                    }
                case "picker.selectFolder":
                    SendResponse(requestId, true, data: SelectFolder(RequiredString(payload, "purpose")));
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
                case "task.retry":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.RetryAsync(CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        break;
                    }
                case "task.restartFresh":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.RestartFreshAsync(
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        break;
                    }
                case "card.reassociate":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReassociateCurrentCardAsync(
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
                        break;
                    }
                case "card.reinitialize":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ReinitializeCurrentCardAsync(
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushConfigurationAsync();
                        await PushStatusAsync();
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
                        StandaloneConfigurationDto saved;
                        using (_runtimeService.BeginConfigurationChange())
                        {
                            saved = await _configurationService.SaveAsync(dto, CancellationToken.None);
                        }
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
                            break;
                        }
                        SendResponse(
                            requestId,
                            true,
                            data: new { configuration = finalConfiguration, status = result.Status },
                            message: result.Message);
                        PostMessage("standalone.configuration", finalConfiguration);
                        await PushStatusAsync();
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
                        break;
                    }
                case "card.confirmSourceCleanup":
                    {
                        StandaloneRuntimeOperationResult result =
                            await _runtimeService.ConfirmSourceCleanupAsync(
                                RequiredBoolean(payload, "confirmed"),
                                CancellationToken.None);
                        SendResponse(requestId, true, data: result.Status, message: result.Message);
                        await PushStatusAsync();
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
                    break;
                case "window.minimize":
                    WindowState = WindowState.Minimized;
                    break;
                case "window.maximize":
                    WindowState = WindowState == WindowState.Maximized
                        ? WindowState.Normal
                        : WindowState.Maximized;
                    break;
                default:
                    SendResponse(requestId, false, error: "不支持的界面请求。");
                    break;
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
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
        PostMessage("standalone.mediaStatus", _runtimeService.GetMediaStatusSnapshot());
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
        StandaloneMediaItemDto? mounted = active ?? status.Media.FirstOrDefault(media =>
            string.Equals(media.PresenceState, "mounted", StringComparison.Ordinal) ||
            string.Equals(media.PresenceState, "detecting", StringComparison.Ordinal));
        _trayIcon.Text = mounted is null
            ? "AutoCardSync - 等待插入素材卡"
            : TrimTrayText($"AutoCardSync - {MediaLabel(mounted)}：{TrayStateLabel(mounted)}");
    }

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

    private static object SelectFolder(string purpose) => purpose switch
    {
        "approvedSourceDirectories" => SelectSourceFolder(),
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

    private static object SelectSourceFolder()
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择素材卡内需要自动同步的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return new { path = (string?)null, displayPath = (string?)null };

        string selectedPath = Path.GetFullPath(dialog.SelectedPath);
        string volumeRoot = Path.GetPathRoot(selectedPath) ??
            throw new InvalidDataException("无法确定所选文件夹所在的盘符。");
        var drive = new DriveInfo(volumeRoot);
        if (!drive.IsReady)
            throw new InvalidDataException("所选素材卡当前不可访问。");

        bool isExternal;
        try
        {
            isExternal = new ExternalSourceVolumeClassifier().IsExternalStorage(new VolumeEventArgs(
                volumeRoot,
                volumeRoot,
                drive.DriveFormat,
                drive.TotalSize,
                DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            throw new InvalidDataException("无法确认所选文件夹属于外接素材卡。", exception);
        }

        if (!isExternal)
            throw new InvalidDataException("批准源目录必须选择外接素材卡内的文件夹。");

        SourceDirectorySelection selection = SourceDirectorySelectionResolver.Resolve(selectedPath, volumeRoot);
        return new { path = selection.RelativePath, displayPath = selection.FullPath };
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
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "AutoCardSync - 等待插卡",
            Icon = LoadApplicationIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();
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

    private void ExitApplication()
    {
        _explicitExit = true;
        _runtimeService.StatusChanged -= OnRuntimeStatusChanged;
        _runtimeService.MediaStatusChanged -= OnMediaStatusChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
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
