using System.IO;
using System.Text.Json;
using AutoCardSync.Standalone.Core.Cards;
using AutoCardSync.Standalone.Core.Configuration;
using Microsoft.Win32;

namespace AutoCardSync.Standalone.Services;

public sealed record StandaloneCameraTemplateDto
{
    public string TemplateId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> ApprovedSourceDirectories { get; init; } = [];
    public IReadOnlyList<string> ApprovedExtensions { get; init; } = [];
}

public sealed record StandaloneCardProfileDto
{
    public string CardInstanceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string CameraTemplateId { get; init; } = string.Empty;
}

public sealed record StandaloneConfigurationDto
{
    public bool Configured { get; init; }
    public IReadOnlyList<string> ApprovedSourceDirectories { get; init; } = [];
    public IReadOnlyList<string> ApprovedExtensions { get; init; } = [];
    public string TargetMode { get; init; } = "nas-only";
    public string LocalTarget { get; init; } = string.Empty;
    public string NasMappedTarget { get; init; } = string.Empty;
    public string TargetNamingRule { get; init; } = "import-date";
    public bool AutoStartOnLogin { get; init; }
    public string DefaultCameraTemplateId { get; init; } = string.Empty;
    public IReadOnlyList<StandaloneCameraTemplateDto> CameraTemplates { get; init; } = [];
    public IReadOnlyList<StandaloneCardProfileDto> CardProfiles { get; init; } = [];
    public IReadOnlyList<StandaloneKnownCardDto> KnownCards { get; init; } = [];
    public string RecoveryNotice { get; init; } = string.Empty;
}

public sealed class StandaloneConfigurationService
{
    private readonly AtomicJsonFileStore<StandaloneConfiguration> _store;
    private readonly LoginAutoStartService _autoStart;
    private readonly AutoCardSync.Standalone.Core.StandaloneDataPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _recoveryNotice;

    public StandaloneConfigurationService(
        AtomicJsonFileStore<StandaloneConfiguration> store,
        LoginAutoStartService autoStart,
        AutoCardSync.Standalone.Core.StandaloneDataPaths paths)
    {
        _store = store;
        _autoStart = autoStart;
        _paths = paths;
    }

    public async Task<StandaloneConfigurationDto> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneConfiguration configuration;
            try
            {
                StandaloneConfiguration? stored = await _store.LoadAsync(cancellationToken);
                if (stored is null)
                    return await WithKnownCardsAsync(CreateUnconfiguredDto(), null, cancellationToken);

                configuration = ValidateAndNormalize(stored);
                if (stored.RequiresSchemaMigration())
                    await _store.SaveAsync(configuration, cancellationToken);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                _ = exception;
                string backupPath = CorruptStateFileRecovery.Preserve(_store.FilePath);
                _recoveryNotice =
                    $"原设置文件无法读取，已安全保留为“{Path.GetFileName(backupPath)}”。请重新完成设置；素材卡基线、任务记录和完成回执均未删除。";
                return await WithKnownCardsAsync(CreateUnconfiguredDto(), null, cancellationToken);
            }

            // Persisted configuration is authoritative across MSI repair and upgrade.
            _autoStart.SetEnabled(configuration.AutoStartOnLogin);
            StandaloneConfigurationDto result = ToDto(configuration) with
            {
                AutoStartOnLogin = _autoStart.IsEnabled(),
                RecoveryNotice = _recoveryNotice ?? string.Empty,
            };
            return await WithKnownCardsAsync(result, configuration, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneConfigurationDto> SaveAsync(
        StandaloneConfigurationDto value,
        CancellationToken cancellationToken)
    {
        StandaloneConfiguration incoming = FromDto(value).NormalizeForCurrentSchema();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneConfiguration? stored;
            try
            {
                stored = await _store.LoadAsync(cancellationToken);
                if (stored is not null)
                    stored = ValidateAndNormalize(stored);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                _ = exception;
                string backupPath = CorruptStateFileRecovery.Preserve(_store.FilePath);
                _recoveryNotice =
                    $"原设置文件无法读取，已安全保留为“{Path.GetFileName(backupPath)}”。";
                stored = null;
            }

            StandaloneConfiguration configuration = incoming;
            if (stored is not null)
            {
                EnsureReferencedTemplatePoliciesUnchanged(stored, incoming);
                configuration = incoming with
                {
                    CardProfiles = MergeCardProfiles(stored.CardProfiles, incoming.CardProfiles),
                };
            }
            configuration = ValidateAndNormalize(configuration);
            await _store.SaveAsync(configuration, cancellationToken);
            _autoStart.SetEnabled(configuration.AutoStartOnLogin);
            _recoveryNotice = null;
            StandaloneConfigurationDto result = ToDto(configuration) with
            {
                AutoStartOnLogin = _autoStart.IsEnabled(),
                RecoveryNotice = string.Empty,
            };
            return await WithKnownCardsAsync(result, configuration, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private StandaloneConfigurationDto CreateUnconfiguredDto() => new()
    {
        Configured = false,
        ApprovedSourceDirectories = [],
        ApprovedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".mp4", ".mov", ".mxf", ".xml", ".xmp"],
        TargetMode = "nas-only",
        TargetNamingRule = "import-date",
        AutoStartOnLogin = _autoStart.IsEnabled(),
        RecoveryNotice = _recoveryNotice ?? string.Empty,
    };

    public async Task<StandaloneConfiguration> EnsureCardProfileAsync(
        Guid cardInstanceId,
        Guid cameraTemplateId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        if (cameraTemplateId == Guid.Empty)
            throw new ArgumentException("Camera template identity is required.", nameof(cameraTemplateId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneConfiguration? stored = await _store.LoadAsync(cancellationToken);
            if (stored is null)
                throw new InvalidDataException("首次设置未完成，不能保存素材卡档案。");
            StandaloneConfiguration configuration = ValidateAndNormalize(stored);
            if (configuration.CameraTemplates.All(template => template.TemplateId != cameraTemplateId))
                throw new InvalidDataException("素材卡引用的相机模板不存在。");

            StandaloneCardProfile? existing = configuration.CardProfiles.SingleOrDefault(
                profile => profile.CardInstanceId == cardInstanceId);
            if (existing is not null)
            {
                if (existing.CameraTemplateId != cameraTemplateId)
                    throw new InvalidDataException("素材卡档案与当前相机模板不一致，已失败关闭。");
                if (stored.RequiresSchemaMigration())
                    await _store.SaveAsync(configuration, cancellationToken);
                return configuration;
            }

            StandaloneConfiguration updated = configuration with
            {
                CardProfiles = configuration.CardProfiles.Append(new StandaloneCardProfile
                {
                    CardInstanceId = cardInstanceId,
                    DisplayName = $"素材卡 {cardInstanceId:N}"[..12],
                    CameraTemplateId = cameraTemplateId,
                }).ToArray(),
            };
            updated = ValidateAndNormalize(updated);
            await _store.SaveAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneConfiguration> RebindCardProfileAsync(
        Guid cardInstanceId,
        Guid cameraTemplateId,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        if (cameraTemplateId == Guid.Empty)
            throw new ArgumentException("Camera template identity is required.", nameof(cameraTemplateId));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneConfiguration? stored = await _store.LoadAsync(cancellationToken);
            if (stored is null)
                throw new InvalidDataException("首次设置未完成，不能重新绑定素材卡档案。");
            StandaloneConfiguration configuration = ValidateAndNormalize(stored);
            if (configuration.CameraTemplates.All(template => template.TemplateId != cameraTemplateId))
                throw new InvalidDataException("重新绑定所选的相机模板不存在。");

            StandaloneCardProfile? existing = configuration.CardProfiles.SingleOrDefault(
                profile => profile.CardInstanceId == cardInstanceId);
            StandaloneCardProfile replacement = existing is null
                ? new StandaloneCardProfile
                {
                    CardInstanceId = cardInstanceId,
                    DisplayName = $"素材卡 {cardInstanceId:N}"[..12],
                    CameraTemplateId = cameraTemplateId,
                }
                : existing with { CameraTemplateId = cameraTemplateId };
            StandaloneConfiguration updated = configuration with
            {
                CardProfiles = configuration.CardProfiles
                    .Where(profile => profile.CardInstanceId != cardInstanceId)
                    .Append(replacement)
                    .ToArray(),
            };
            updated = ValidateAndNormalize(updated);
            await _store.SaveAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneConfigurationDto> RestoreSnapshotAsync(
        StandaloneConfigurationDto value,
        CancellationToken cancellationToken)
    {
        StandaloneConfiguration configuration = ValidateAndNormalize(FromDto(value));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // This is only used to roll back an explicit card-scope transaction
            // that did not commit. Unlike normal settings saves, the snapshot
            // must replace temporary templates/profiles instead of merging them.
            await _store.SaveAsync(configuration, cancellationToken);
            _autoStart.SetEnabled(configuration.AutoStartOnLogin);
            StandaloneConfigurationDto result = ToDto(configuration) with
            {
                AutoStartOnLogin = _autoStart.IsEnabled(),
                RecoveryNotice = string.Empty,
            };
            return await WithKnownCardsAsync(result, configuration, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneConfigurationDto> RenameCardProfileAsync(
        Guid cardInstanceId,
        string displayName,
        CancellationToken cancellationToken)
    {
        if (cardInstanceId == Guid.Empty)
            throw new ArgumentException("Card instance identity is required.", nameof(cardInstanceId));
        string normalizedName = displayName?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > 64)
            throw new InvalidDataException("素材卡名称必须为 1 到 64 个字符。");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneConfiguration? stored = await _store.LoadAsync(cancellationToken);
            if (stored is null)
                throw new InvalidDataException("首次设置未完成，不能重命名素材卡。");
            StandaloneConfiguration configuration = ValidateAndNormalize(stored);
            if (configuration.CardProfiles.All(profile => profile.CardInstanceId != cardInstanceId))
                throw new InvalidDataException("找不到这张已初始化素材卡。");
            if (configuration.CardProfiles.Any(profile =>
                    profile.CardInstanceId != cardInstanceId &&
                    string.Equals(profile.DisplayName.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("素材卡名称不能重复。");
            }

            StandaloneConfiguration updated = configuration with
            {
                CardProfiles = configuration.CardProfiles.Select(profile =>
                    profile.CardInstanceId == cardInstanceId
                        ? profile with { DisplayName = normalizedName }
                        : profile).ToArray(),
            };
            updated = ValidateAndNormalize(updated);
            await _store.SaveAsync(updated, cancellationToken);
            StandaloneConfigurationDto result = ToDto(updated) with
            {
                AutoStartOnLogin = _autoStart.IsEnabled(),
                RecoveryNotice = string.Empty,
            };
            return await WithKnownCardsAsync(result, updated, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void EnsureReferencedTemplatePoliciesUnchanged(
        StandaloneConfiguration stored,
        StandaloneConfiguration incoming)
    {
        HashSet<Guid> referencedTemplateIds = stored.CardProfiles
            .Select(profile => profile.CameraTemplateId)
            .ToHashSet();
        foreach (Guid templateId in referencedTemplateIds)
        {
            StandaloneCameraTemplate previous = stored.CameraTemplates.Single(
                template => template.TemplateId == templateId);
            StandaloneCameraTemplate? proposed = incoming.CameraTemplates.SingleOrDefault(
                template => template.TemplateId == templateId);
            if (proposed is null)
                throw new InvalidDataException("已绑定素材卡的相机模板不能通过普通设置删除。请先使用明确的素材卡恢复操作。");

            string previousPolicy = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                previous.ApprovedSourceDirectories, previous.NormalizedExtensions);
            string proposedPolicy = StandaloneInventoryBaselineStore.ComputeSelectionPolicyHash(
                proposed.ApprovedSourceDirectories, proposed.NormalizedExtensions);
            if (!string.Equals(previousPolicy, proposedPolicy, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "已绑定素材卡的目录或扩展名策略不能通过普通设置静默修改。请新建模板并通过明确的素材卡恢复操作重新绑定。");
            }
        }
    }

    private static IReadOnlyList<StandaloneCardProfile> MergeCardProfiles(
        IReadOnlyList<StandaloneCardProfile> persisted,
        IReadOnlyList<StandaloneCardProfile> incoming)
    {
        var merged = persisted.ToDictionary(profile => profile.CardInstanceId);
        foreach (StandaloneCardProfile profile in incoming)
        {
            if (merged.TryGetValue(profile.CardInstanceId, out StandaloneCardProfile? existing))
            {
                if (existing.CameraTemplateId != profile.CameraTemplateId)
                    throw new InvalidDataException("素材卡档案不能通过普通设置静默改绑相机模板。");
                continue;
            }
            merged.Add(profile.CardInstanceId, profile);
        }
        return merged.Values.OrderBy(profile => profile.CardInstanceId).ToArray();
    }

    private static StandaloneConfiguration ValidateAndNormalize(StandaloneConfiguration value)
    {
        ConfigurationValidationResult validation = value.Validate();
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("；", validation.Errors.Select(error => error.Message)));
        return value.NormalizeForCurrentSchema();
    }

    private static StandaloneConfiguration FromDto(StandaloneConfigurationDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ApprovedSourceDirectories is null || value.ApprovedExtensions is null ||
            value.CameraTemplates is null || value.CardProfiles is null ||
            value.TargetMode is null || value.LocalTarget is null || value.NasMappedTarget is null ||
            value.TargetNamingRule is null || value.DefaultCameraTemplateId is null)
        {
            throw new InvalidDataException("设置内容缺少必需字段。");
        }

        StandaloneCameraTemplate[] templates = value.CameraTemplates.Count > 0
            ? value.CameraTemplates.Select(ToTemplate).ToArray()
            :
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = StandaloneConfiguration.LegacyCameraTemplateId,
                    Name = "默认相机",
                    ApprovedSourceDirectories = Clean(value.ApprovedSourceDirectories),
                    ApprovedExtensions = Clean(value.ApprovedExtensions),
                },
            ];
        Guid defaultId = ParseOptionalGuid(value.DefaultCameraTemplateId) ?? templates[0].TemplateId;
        StandaloneCameraTemplate defaultTemplate = templates.SingleOrDefault(template => template.TemplateId == defaultId)
            ?? throw new InvalidDataException("默认相机模板无效。");
        return new StandaloneConfiguration
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = defaultTemplate.ApprovedSourceDirectories,
            ApprovedExtensions = defaultTemplate.ApprovedExtensions,
            TargetMode = FromUiTargetMode(value.TargetMode),
            LocalTargetPath = value.LocalTarget.Trim(),
            NasMappedTargetPath = value.NasMappedTarget.Trim(),
            TargetNamingRule = FromUiRule(value.TargetNamingRule),
            AutoStartOnLogin = value.AutoStartOnLogin,
            DefaultCameraTemplateId = defaultId,
            CameraTemplates = templates,
            CardProfiles = value.CardProfiles.Select(ToProfile).ToArray(),
        };
    }

    private static StandaloneConfigurationDto ToDto(StandaloneConfiguration value)
    {
        StandaloneConfiguration normalized = value.NormalizeForCurrentSchema();
        return new StandaloneConfigurationDto
        {
            Configured = normalized.Validate().IsValid,
            ApprovedSourceDirectories = normalized.DefaultCameraTemplate.ApprovedSourceDirectories,
            ApprovedExtensions = normalized.DefaultCameraTemplate.NormalizedExtensions,
            TargetMode = ToUiTargetMode(normalized.TargetMode),
            LocalTarget = normalized.LocalTargetPath,
            NasMappedTarget = normalized.NasMappedTargetPath,
            TargetNamingRule = ToUiRule(normalized.TargetNamingRule),
            AutoStartOnLogin = normalized.AutoStartOnLogin,
            DefaultCameraTemplateId = normalized.EffectiveDefaultCameraTemplateId.ToString("D"),
            CameraTemplates = normalized.EffectiveCameraTemplates.Select(template => new StandaloneCameraTemplateDto
            {
                TemplateId = template.TemplateId.ToString("D"),
                Name = template.Name,
                ApprovedSourceDirectories = template.ApprovedSourceDirectories,
                ApprovedExtensions = template.NormalizedExtensions,
            }).ToArray(),
            CardProfiles = normalized.CardProfiles.Select(profile => new StandaloneCardProfileDto
            {
                CardInstanceId = profile.CardInstanceId.ToString("D"),
                DisplayName = profile.DisplayName,
                CameraTemplateId = profile.CameraTemplateId.ToString("D"),
            }).ToArray(),
        };
    }

    private async Task<StandaloneConfigurationDto> WithKnownCardsAsync(
        StandaloneConfigurationDto configurationDto,
        StandaloneConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var baselineStore = new StandaloneInventoryBaselineStore(_paths.CardInventoryBaselineFile);
        var identityResolver = new StandaloneCardIdentityResolver(_paths.CardIdentityFile);
        IReadOnlyList<StandaloneInventoryBaseline> baselines = [];
        IReadOnlyList<StandaloneCardIdentityBinding> bindings = [];
        var recoveryNotices = new List<string>();
        try
        {
            baselines = await baselineStore.FindAllBaselinesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            recoveryNotices.Add(
                "素材卡基线记录当前无法读取，原文件已保留；卡管理仍显示可读取的档案，自动处理将保持失败关闭。");
        }
        try
        {
            bindings = await identityResolver.ListBindingsAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            recoveryNotices.Add(
                "素材卡身份记录当前无法读取，原文件已保留；卡管理仍显示可读取的档案与基线，自动处理将保持失败关闭。");
        }

        return configurationDto with
        {
            KnownCards = CreateKnownCards(configuration, baselines, bindings),
            RecoveryNotice = CombineNotices(configurationDto.RecoveryNotice, recoveryNotices),
        };
    }

    private static string CombineNotices(string existing, IReadOnlyList<string> additions)
    {
        IEnumerable<string> notices = string.IsNullOrWhiteSpace(existing)
            ? additions
            : new[] { existing }.Concat(additions);
        return string.Join(" ", notices.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
    }

    private static IReadOnlyList<StandaloneKnownCardDto> CreateKnownCards(
        StandaloneConfiguration? configuration,
        IReadOnlyList<StandaloneInventoryBaseline> baselines,
        IReadOnlyList<StandaloneCardIdentityBinding> bindings)
    {
        IReadOnlyList<StandaloneCardProfile> profiles = configuration?.CardProfiles ?? [];
        IReadOnlyList<StandaloneCameraTemplate> templates = configuration?.EffectiveCameraTemplates ?? [];
        Dictionary<Guid, StandaloneCardProfile> profilesByCardId = profiles.ToDictionary(
            profile => profile.CardInstanceId);
        Dictionary<Guid, StandaloneInventoryBaseline> baselinesByCardId = baselines.ToDictionary(
            baseline => baseline.CardInstanceId);
        Dictionary<Guid, StandaloneCardIdentityBinding> bindingsByCardId = bindings.ToDictionary(
            binding => binding.CardInstanceId);
        Dictionary<Guid, StandaloneCameraTemplate> templatesById = templates.ToDictionary(
            template => template.TemplateId);

        return profilesByCardId.Keys
            .Concat(baselinesByCardId.Keys)
            .Concat(bindingsByCardId.Keys)
            .Distinct()
            .OrderBy(cardInstanceId => cardInstanceId)
            .Select(cardInstanceId =>
            {
                profilesByCardId.TryGetValue(cardInstanceId, out StandaloneCardProfile? profile);
                baselinesByCardId.TryGetValue(cardInstanceId, out StandaloneInventoryBaseline? baseline);
                bindingsByCardId.TryGetValue(cardInstanceId, out StandaloneCardIdentityBinding? binding);

                Guid? cameraTemplateId = profile?.CameraTemplateId ?? baseline?.CameraTemplateId;
                StandaloneCameraTemplate? template = cameraTemplateId is Guid templateId
                    ? templatesById.GetValueOrDefault(templateId)
                    : null;
                bool initializationPending = baseline?.InitializationPending == true;
                bool hasBaseline = baseline is { InitializationPending: false };
                bool hasProfile = profile is not null;
                bool hasIdentityBinding = binding is not null;

                return new StandaloneKnownCardDto
                {
                    CardInstanceId = cardInstanceId.ToString("D"),
                    DisplayName = profile?.DisplayName ?? $"素材卡 {cardInstanceId:N}"[..12],
                    CameraTemplateId = cameraTemplateId?.ToString("D") ?? string.Empty,
                    CameraTemplateName = template?.Name ?? string.Empty,
                    ApprovedSourceDirectories = template?.ApprovedSourceDirectories ?? [],
                    ApprovedExtensions = template?.NormalizedExtensions ?? [],
                    HasProfile = hasProfile,
                    HasIdentityBinding = hasIdentityBinding,
                    HasBaseline = hasBaseline,
                    InitializationPending = initializationPending,
                    HealthState = GetKnownCardHealthState(
                        hasProfile,
                        hasIdentityBinding,
                        hasBaseline,
                        initializationPending),
                    FirstSeenUtc = FormatUtc(binding?.FirstSeenUtc),
                    LastSeenUtc = FormatUtc(binding?.LastSeenUtc),
                    LastCompletedTaskId = hasBaseline && baseline?.LastCompletedTaskId is Guid taskId
                        ? taskId.ToString("D")
                        : string.Empty,
                };
            })
            .ToArray();
    }

    private static string GetKnownCardHealthState(
        bool hasProfile,
        bool hasIdentityBinding,
        bool hasBaseline,
        bool initializationPending)
    {
        if (initializationPending)
            return "initialization_pending";
        if (hasBaseline && !hasProfile)
            return "legacy_profile_missing";
        if (hasIdentityBinding && !hasProfile && !hasBaseline)
            return "identity_only";
        if (!hasBaseline)
            return "baseline_missing";
        if (!hasIdentityBinding)
            return "identity_binding_missing";
        return "healthy";
    }

    private static string FormatUtc(DateTimeOffset? timestamp) => timestamp is DateTimeOffset value
        ? value.ToUniversalTime().ToString("O")
        : string.Empty;

    private static StandaloneCameraTemplate ToTemplate(StandaloneCameraTemplateDto value)
    {
        if (value is null || value.TemplateId is null || value.Name is null ||
            value.ApprovedSourceDirectories is null || value.ApprovedExtensions is null)
        {
            throw new InvalidDataException("相机模板缺少必需字段。");
        }
        return new()
        {
            TemplateId = ParseRequiredGuid(value.TemplateId, "相机模板标识无效。"),
            Name = value.Name.Trim(),
            ApprovedSourceDirectories = Clean(value.ApprovedSourceDirectories),
            ApprovedExtensions = Clean(value.ApprovedExtensions),
        };
    }

    private static StandaloneCardProfile ToProfile(StandaloneCardProfileDto value)
    {
        if (value is null || value.CardInstanceId is null || value.DisplayName is null ||
            value.CameraTemplateId is null)
        {
            throw new InvalidDataException("素材卡档案缺少必需字段。");
        }
        return new()
        {
            CardInstanceId = ParseRequiredGuid(value.CardInstanceId, "素材卡档案标识无效。"),
            DisplayName = value.DisplayName.Trim(),
            CameraTemplateId = ParseRequiredGuid(value.CameraTemplateId, "素材卡相机模板无效。"),
        };
    }

    private static string[] Clean(IEnumerable<string> values) => values
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item.Trim())
        .ToArray();

    private static Guid ParseRequiredGuid(string value, string message) =>
        Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidDataException(message);

    private static Guid? ParseOptionalGuid(string value) =>
        Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty ? parsed : null;

    private static StandaloneTargetMode FromUiTargetMode(string value) =>
        value switch
        {
            "nas-only" => StandaloneTargetMode.NasOnly,
            "local-only" => StandaloneTargetMode.LocalOnly,
            "local-and-nas" => StandaloneTargetMode.LocalAndNas,
            _ => throw new InvalidDataException("保存方式无效。"),
        };

    private static string ToUiTargetMode(StandaloneTargetMode value) =>
        value switch
        {
            StandaloneTargetMode.NasOnly => "nas-only",
            StandaloneTargetMode.LocalOnly => "local-only",
            StandaloneTargetMode.LocalAndNas => "local-and-nas",
            _ => "nas-only",
        };

    private static TargetNamingRule FromUiRule(string value) =>
        value switch
        {
            "capture-date" => TargetNamingRule.CaptureDate,
            "import-date" => TargetNamingRule.ImportDate,
            "preserve" => TargetNamingRule.PreserveRelativePath,
            _ => throw new InvalidDataException("目标命名规则无效。"),
        };

    private static string ToUiRule(TargetNamingRule value) =>
        value switch
        {
            TargetNamingRule.CaptureDate => "capture-date",
            TargetNamingRule.ImportDate => "import-date",
            TargetNamingRule.PreserveRelativePath => "preserve",
            _ => "import-date",
        };
}

public class LoginAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoCardSync";

    public virtual bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value &&
            string.Equals(value, GetCommandLine(), StringComparison.OrdinalIgnoreCase);
    }

    public virtual void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true) ??
            throw new InvalidOperationException("无法打开登录自动启动设置。");
        if (enabled)
            key.SetValue(ValueName, GetCommandLine(), RegistryValueKind.String);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetCommandLine()
    {
        string path = Environment.ProcessPath ??
            throw new InvalidOperationException("无法确定 AutoCardSync 程序路径。");
        return $"\"{path}\"";
    }
}
