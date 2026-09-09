using System.Reflection;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests.Configuration;

public sealed class StandaloneConfigurationMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-ConfigurationMigration", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Legacy_configuration_normalizes_to_schema_two_without_losing_selection_or_targets()
    {
        StandaloneConfiguration legacy = LegacyConfiguration();

        Assert.True(legacy.Validate().IsValid);
        StandaloneConfiguration normalized = legacy.NormalizeForCurrentSchema();

        Assert.Equal(2, normalized.SchemaVersion);
        StandaloneCameraTemplate template = Assert.Single(normalized.CameraTemplates);
        Assert.Equal(StandaloneConfiguration.LegacyCameraTemplateId, template.TemplateId);
        Assert.Equal(["XDROOT\\Clip", "PRIVATE"], template.ApprovedSourceDirectories);
        Assert.Equal([".mov", ".xml"], template.ApprovedExtensions);
        Assert.Equal(template.TemplateId, normalized.DefaultCameraTemplateId);
        Assert.Equal(legacy.LocalTargetPath, normalized.LocalTargetPath);
        Assert.Equal(legacy.NasMappedTargetPath, normalized.NasMappedTargetPath);
        Assert.Equal(legacy.TargetMode, normalized.TargetMode);
    }

    [Fact]
    public void Redundant_nested_source_roots_normalize_without_changing_selected_coverage()
    {
        Guid templateId = Guid.NewGuid();
        StandaloneConfiguration configuration = LegacyConfiguration() with
        {
            SchemaVersion = 2,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "混合相机",
                    ApprovedSourceDirectories = ["DCIM\\117LIUYI", "DCIM", "PRIVATE", "PRIVATE\\AVCHD"],
                    ApprovedExtensions = [".jpg", ".mp4"],
                },
            ],
        };

        Assert.True(configuration.RequiresSchemaMigration());
        StandaloneConfiguration normalized = configuration.NormalizeForCurrentSchema();

        Assert.Equal(["DCIM", "PRIVATE"], Assert.Single(normalized.CameraTemplates).ApprovedSourceDirectories);
        Assert.True(normalized.Validate().IsValid);
    }

    [Fact]
    public async Task Configuration_get_migrates_legacy_json_in_place()
    {
        string path = Path.Combine(_root, "configuration.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration(), CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);
        StandaloneConfiguration? persisted = await store.LoadAsync(CancellationToken.None);

        Assert.True(dto.Configured);
        Assert.NotNull(persisted);
        Assert.Equal(2, persisted!.SchemaVersion);
        Assert.Equal("XDROOT\\Clip", Assert.Single(dto.CameraTemplates).ApprovedSourceDirectories[0]);
        Assert.Equal(dto.DefaultCameraTemplateId, Assert.Single(dto.CameraTemplates).TemplateId);
        Assert.Equal([".mov", ".xml"], dto.ApprovedExtensions);
        Assert.Equal(@"C:\AutoCardSync\Local", dto.LocalTarget);
        Assert.Equal(@"Z:\AutoCardSync", dto.NasMappedTarget);
    }

    [Fact]
    public async Task Persisted_autostart_true_reconciles_a_missing_effective_registration()
    {
        string path = Path.Combine(_root, "autostart-true.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration() with { AutoStartOnLogin = true }, CancellationToken.None);
        var autoStart = new TestLoginAutoStartService(enabled: false);
        var service = new StandaloneConfigurationService(store, autoStart);

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.True(dto.AutoStartOnLogin);
        Assert.True(autoStart.Enabled);
        Assert.Equal(1, autoStart.SetCount);
    }

    [Fact]
    public async Task Persisted_autostart_false_removes_a_stale_effective_registration()
    {
        string path = Path.Combine(_root, "autostart-false.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration() with { AutoStartOnLogin = false }, CancellationToken.None);
        var autoStart = new TestLoginAutoStartService(enabled: true);
        var service = new StandaloneConfigurationService(store, autoStart);

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.False(dto.AutoStartOnLogin);
        Assert.False(autoStart.Enabled);
        Assert.Equal(1, autoStart.SetCount);
    }

    [Fact]
    public async Task Auto_start_registry_failure_does_not_block_configuration_read()
    {
        string path = Path.Combine(_root, "autostart-unavailable.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration() with { AutoStartOnLogin = true }, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new FailingLoginAutoStartService());

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.True(dto.Configured);
        Assert.True(dto.AutoStartOnLogin);
        Assert.Contains("不会阻止插卡导入", dto.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Auto_start_registry_failure_does_not_roll_back_a_saved_configuration()
    {
        string path = Path.Combine(_root, "autostart-save-unavailable.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration() with { AutoStartOnLogin = true }, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new FailingLoginAutoStartService());
        StandaloneConfigurationDto current = await service.GetAsync(CancellationToken.None);

        StandaloneConfigurationDto saved = await service.SaveAsync(
            current with { AutoStartOnLogin = false }, CancellationToken.None);
        StandaloneConfiguration persisted = Assert.IsType<StandaloneConfiguration>(
            await store.LoadAsync(CancellationToken.None));

        Assert.False(persisted.AutoStartOnLogin);
        Assert.False(saved.AutoStartOnLogin);
        Assert.Contains("不会阻止插卡导入", saved.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_run_defaults_work_without_an_inserted_card_or_manual_extension_list()
    {
        string path = Path.Combine(_root, "first-run-defaults.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        var service = new StandaloneConfigurationService(store, new FailingLoginAutoStartService());

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.False(dto.Configured);
        Assert.Equal(["."], dto.ApprovedSourceDirectories);
        Assert.Equal("local-only", dto.TargetMode);
        Assert.Contains(".wav", dto.ApprovedExtensions);
        Assert.Contains(".cr3", dto.ApprovedExtensions);
        Assert.Contains("不会阻止插卡导入", dto.RecoveryNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_configuration_is_preserved_and_returns_to_first_run_instead_of_blocking_startup()
    {
        string path = Path.Combine(_root, "configuration.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.False(dto.Configured);
        Assert.Contains("已安全保留", dto.RecoveryNotice, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(_root, "configuration.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Structurally_invalid_configuration_with_null_collections_is_preserved_and_recoverable()
    {
        string path = Path.Combine(_root, "configuration-null-structure.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        await store.SaveAsync(LegacyConfiguration() with
        {
            ApprovedSourceDirectories = null!,
            CameraTemplates = null!,
            CardProfiles = null!,
        }, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());

        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);

        Assert.False(dto.Configured);
        Assert.Contains("已安全保留", dto.RecoveryNotice, StringComparison.Ordinal);
        Assert.Single(Directory.EnumerateFiles(_root, "configuration-null-structure.json.corrupt-*.bak"));
    }

    [Fact]
    public async Task Save_rejects_ui_payload_with_null_required_collections_as_invalid_data()
    {
        string path = Path.Combine(_root, "configuration-null-payload.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());
        var dto = new StandaloneConfigurationDto
        {
            ApprovedSourceDirectories = null!,
            CameraTemplates = null!,
            CardProfiles = null!,
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveAsync(dto, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Saving_after_runtime_configuration_corruption_preserves_the_bad_file_and_recovers()
    {
        string path = Path.Combine(_root, "configuration-save.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());
        StandaloneConfiguration normalized = LegacyConfiguration().NormalizeForCurrentSchema();
        StandaloneConfigurationDto incoming = new()
        {
            Configured = true,
            ApprovedSourceDirectories = normalized.ApprovedSourceDirectories,
            ApprovedExtensions = normalized.ApprovedExtensions,
            TargetMode = "local-and-nas",
            LocalTarget = normalized.LocalTargetPath,
            NasMappedTarget = normalized.NasMappedTargetPath,
            TargetNamingRule = "import-date",
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = normalized.DefaultCameraTemplateId.ToString("D"),
            CameraTemplates = normalized.CameraTemplates.Select(template => new StandaloneCameraTemplateDto
            {
                TemplateId = template.TemplateId.ToString("D"),
                Name = template.Name,
                ApprovedSourceDirectories = template.ApprovedSourceDirectories,
                ApprovedExtensions = template.ApprovedExtensions,
            }).ToArray(),
        };

        StandaloneConfigurationDto saved = await service.SaveAsync(incoming, CancellationToken.None);

        Assert.True(saved.Configured);
        Assert.NotNull(await store.LoadAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(_root, "configuration-save.json.corrupt-*.bak"));
    }

    [Fact]
    public void Multiple_templates_and_card_profiles_validate_only_with_unique_valid_references()
    {
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        Guid card = Guid.NewGuid();
        StandaloneConfiguration valid = LegacyConfiguration() with
        {
            SchemaVersion = 2,
            DefaultCameraTemplateId = firstTemplate,
            CameraTemplates =
            [
                Template(firstTemplate, "索尼", "XDROOT\\Clip", ".mxf"),
                Template(secondTemplate, "佳能", "CONTENTS\\CLIPS001", ".mp4"),
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = card,
                    DisplayName = "A 机位 1 号卡",
                    CameraTemplateId = secondTemplate,
                },
            ],
        };

        Assert.True(valid.Validate().IsValid);
        Assert.Contains((valid with
        {
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = card,
                    DisplayName = "A 机位 1 号卡",
                    CameraTemplateId = Guid.NewGuid(),
                },
            ],
        }).Validate().Errors, error => error.Code == "card_profile_invalid");
        Assert.Contains((valid with
        {
            CameraTemplates =
            [
                Template(firstTemplate, "索尼", "XDROOT\\Clip", ".mxf"),
                Template(firstTemplate, "佳能", "CONTENTS\\CLIPS001", ".mp4"),
            ],
        }).Validate().Errors, error => error.Code == "camera_template_id_invalid");
    }

    [Fact]
    public void Stale_ui_profile_list_cannot_drop_or_rebind_persisted_card_profiles()
    {
        Guid cardId = Guid.NewGuid();
        Guid templateId = Guid.NewGuid();
        var persisted = new StandaloneCardProfile
        {
            CardInstanceId = cardId,
            DisplayName = "已登记素材卡",
            CameraTemplateId = templateId,
        };
        MethodInfo merge = typeof(StandaloneConfigurationService).GetMethod(
            "MergeCardProfiles", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("Missing card profile merge method.");

        IReadOnlyList<StandaloneCardProfile> retained = Assert.IsAssignableFrom<IReadOnlyList<StandaloneCardProfile>>(
            merge.Invoke(null, [new[] { persisted }, Array.Empty<StandaloneCardProfile>()]));
        Assert.Equal(persisted, Assert.Single(retained));

        var rebound = persisted with { CameraTemplateId = Guid.NewGuid() };
        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(() =>
            merge.Invoke(null, [new[] { persisted }, new[] { rebound }]));
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task Card_profile_is_persisted_once_and_cannot_be_silently_rebound()
    {
        string path = Path.Combine(_root, "configuration.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        StandaloneConfiguration configuration = LegacyConfiguration() with
        {
            SchemaVersion = 2,
            DefaultCameraTemplateId = firstTemplate,
            CameraTemplates =
            [
                Template(firstTemplate, "索尼", "XDROOT\\Clip", ".mxf"),
                Template(secondTemplate, "佳能", "CONTENTS\\CLIPS001", ".mp4"),
            ],
        };
        await store.SaveAsync(configuration, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());
        Guid cardId = Guid.NewGuid();

        await service.EnsureCardProfileAsync(cardId, secondTemplate, CancellationToken.None);
        await service.EnsureCardProfileAsync(cardId, secondTemplate, CancellationToken.None);
        StandaloneConfiguration? persisted = await store.LoadAsync(CancellationToken.None);

        StandaloneCardProfile profile = Assert.Single(persisted!.CardProfiles);
        Assert.Equal(cardId, profile.CardInstanceId);
        Assert.Equal(secondTemplate, profile.CameraTemplateId);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.EnsureCardProfileAsync(cardId, firstTemplate, CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_card_profile_rebind_updates_only_the_confirmed_card()
    {
        string path = Path.Combine(_root, "configuration.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        Guid firstTemplate = Guid.NewGuid();
        Guid secondTemplate = Guid.NewGuid();
        Guid confirmedCard = Guid.NewGuid();
        Guid otherCard = Guid.NewGuid();
        StandaloneConfiguration configuration = LegacyConfiguration() with
        {
            SchemaVersion = 2,
            DefaultCameraTemplateId = firstTemplate,
            CameraTemplates =
            [
                Template(firstTemplate, "索尼", @"XDROOT\Clip", ".mxf"),
                Template(secondTemplate, "佳能", @"CONTENTS\CLIPS001", ".mp4"),
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = confirmedCard,
                    DisplayName = "A 机位",
                    CameraTemplateId = firstTemplate,
                },
                new StandaloneCardProfile
                {
                    CardInstanceId = otherCard,
                    DisplayName = "B 机位",
                    CameraTemplateId = firstTemplate,
                },
            ],
        };
        await store.SaveAsync(configuration, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());

        await service.RebindCardProfileAsync(confirmedCard, secondTemplate, CancellationToken.None);
        StandaloneConfiguration persisted = Assert.IsType<StandaloneConfiguration>(
            await store.LoadAsync(CancellationToken.None));

        Assert.Equal(secondTemplate, persisted.CardProfiles.Single(
            profile => profile.CardInstanceId == confirmedCard).CameraTemplateId);
        Assert.Equal(firstTemplate, persisted.CardProfiles.Single(
            profile => profile.CardInstanceId == otherCard).CameraTemplateId);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RebindCardProfileAsync(confirmedCard, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Referenced_camera_template_selection_policy_cannot_be_mutated_by_normal_settings_save()
    {
        string path = Path.Combine(_root, "configuration-policy-guard.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        Guid templateId = Guid.NewGuid();
        Guid cardId = Guid.NewGuid();
        StandaloneConfiguration configured = new()
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = ["DCIM"],
            ApprovedExtensions = [".mov"],
            LocalTargetPath = Path.Combine(_root, "target"),
            NasMappedTargetPath = string.Empty,
            TargetMode = StandaloneTargetMode.LocalOnly,
            TargetNamingRule = TargetNamingRule.PreserveRelativePath,
            AutoStartOnLogin = false,
            DefaultCameraTemplateId = templateId,
            CameraTemplates =
            [
                new StandaloneCameraTemplate
                {
                    TemplateId = templateId,
                    Name = "Camera",
                    ApprovedSourceDirectories = ["DCIM"],
                    ApprovedExtensions = [".mov"],
                },
            ],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = cardId,
                    DisplayName = "A card",
                    CameraTemplateId = templateId,
                },
            ],
        };
        await store.SaveAsync(configured, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());
        StandaloneConfigurationDto dto = await service.GetAsync(CancellationToken.None);
        StandaloneCameraTemplateDto template = Assert.Single(dto.CameraTemplates);
        StandaloneConfigurationDto changed = dto with
        {
            ApprovedSourceDirectories = ["PRIVATE"],
            CameraTemplates = [template with { ApprovedSourceDirectories = ["PRIVATE"] }],
        };

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.SaveAsync(changed, CancellationToken.None));

        Assert.Contains("已绑定素材卡", exception.Message, StringComparison.Ordinal);
        StandaloneConfiguration persisted = Assert.IsType<StandaloneConfiguration>(
            await store.LoadAsync(CancellationToken.None));
        Assert.Equal(["DCIM"], persisted.DefaultCameraTemplate.ApprovedSourceDirectories);
    }

    [Fact]
    public async Task Card_scoped_template_save_ignores_unrelated_global_fields_and_preserves_other_cards()
    {
        string path = Path.Combine(_root, "configuration-card-scope.json");
        var store = new AtomicJsonFileStore<StandaloneConfiguration>(path);
        Guid defaultTemplateId = Guid.NewGuid();
        Guid selectedTemplateId = Guid.NewGuid();
        Guid otherCardId = Guid.NewGuid();
        StandaloneConfiguration stored = LegacyConfiguration() with
        {
            SchemaVersion = 2,
            DefaultCameraTemplateId = defaultTemplateId,
            CameraTemplates = [Template(defaultTemplateId, "默认范围", "DCIM", ".mov")],
            CardProfiles =
            [
                new StandaloneCardProfile
                {
                    CardInstanceId = otherCardId,
                    DisplayName = "其他素材卡",
                    CameraTemplateId = defaultTemplateId,
                },
            ],
            AutoStartOnLogin = true,
            LocalTargetPath = @"C:\Same",
            NasMappedTargetPath = @"C:\Same",
        };
        await store.SaveAsync(stored, CancellationToken.None);
        var service = new StandaloneConfigurationService(store, new TestLoginAutoStartService());
        StandaloneConfigurationDto cardScope = new()
        {
            TargetMode = "local-and-nas",
            LocalTarget = @"C:\Same",
            NasMappedTarget = @"C:\Same",
            DefaultCameraTemplateId = selectedTemplateId.ToString("D"),
            CameraTemplates =
            [
                new StandaloneCameraTemplateDto
                {
                    TemplateId = selectedTemplateId.ToString("D"),
                    Name = "当前卡范围",
                    ApprovedSourceDirectories = ["."],
                    ApprovedExtensions = [".mp4"],
                },
                new StandaloneCameraTemplateDto
                {
                    TemplateId = Guid.NewGuid().ToString("D"),
                    Name = "当前卡范围",
                    ApprovedSourceDirectories = ["PRIVATE"],
                    ApprovedExtensions = [".jpg"],
                },
            ],
        };

        await service.SaveCardInitializationAsync(
            cardScope, selectedTemplateId, CancellationToken.None);
        StandaloneConfiguration persisted = Assert.IsType<StandaloneConfiguration>(
            await store.LoadAsync(CancellationToken.None));

        Assert.Equal(stored.LocalTargetPath, persisted.LocalTargetPath);
        Assert.Equal(stored.NasMappedTargetPath, persisted.NasMappedTargetPath);
        Assert.Equal(stored.TargetMode, persisted.TargetMode);
        Assert.Equal(defaultTemplateId, persisted.DefaultCameraTemplateId);
        Assert.True(persisted.AutoStartOnLogin);
        Assert.Equal(stored.CardProfiles, persisted.CardProfiles);
        StandaloneCameraTemplate selected = Assert.Single(
            persisted.CameraTemplates, template => template.TemplateId == selectedTemplateId);
        Assert.Equal(["."], selected.ApprovedSourceDirectories);
        Assert.Equal([".mp4"], selected.NormalizedExtensions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static StandaloneConfiguration LegacyConfiguration() => new()
    {
        ApprovedSourceDirectories = ["XDROOT\\Clip", "PRIVATE"],
        ApprovedExtensions = ["MOV", ".xml"],
        LocalTargetPath = @"C:\AutoCardSync\Local",
        NasMappedTargetPath = @"Z:\AutoCardSync",
        TargetMode = StandaloneTargetMode.LocalAndNas,
        TargetNamingRule = TargetNamingRule.PreserveRelativePath,
        AutoStartOnLogin = false,
    };

    private static StandaloneCameraTemplate Template(Guid id, string name, string directory, string extension) => new()
    {
        TemplateId = id,
        Name = name,
        ApprovedSourceDirectories = [directory],
        ApprovedExtensions = [extension],
    };
}
