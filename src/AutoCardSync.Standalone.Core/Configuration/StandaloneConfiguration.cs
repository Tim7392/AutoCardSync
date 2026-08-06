using System.Text.Json.Serialization;

namespace AutoCardSync.Standalone.Core.Configuration;

public enum TargetNamingRule
{
    PreserveRelativePath,
    ImportDate,
    CaptureDate,
    CardNameAndImportTime,
}

public enum StandaloneTargetMode
{
    NasOnly,
    LocalOnly,
    LocalAndNas,
}

public static class StandaloneTargetModeExtensions
{
    public static bool RequiresLocal(this StandaloneTargetMode mode) =>
        mode is StandaloneTargetMode.LocalOnly or StandaloneTargetMode.LocalAndNas;

    public static bool RequiresNas(this StandaloneTargetMode mode) =>
        mode is StandaloneTargetMode.NasOnly or StandaloneTargetMode.LocalAndNas;
}

public sealed record ConfigurationValidationError(string Code, string Message);

public sealed record ConfigurationValidationResult(IReadOnlyList<ConfigurationValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record StandaloneCameraTemplate
{
    public required Guid TemplateId { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> ApprovedSourceDirectories { get; init; }
    public required IReadOnlyList<string> ApprovedExtensions { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> NormalizedExtensions => ApprovedExtensions
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(StandaloneConfiguration.NormalizeExtension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record StandaloneCardProfile
{
    public required Guid CardInstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required Guid CameraTemplateId { get; init; }
}

public sealed record StandaloneConfiguration
{
    public static readonly Guid LegacyCameraTemplateId = new("7d8f2f83-4418-4f65-9e11-9985d85a7ef1");

    public int SchemaVersion { get; init; } = 1;
    public required IReadOnlyList<string> ApprovedSourceDirectories { get; init; }
    public required IReadOnlyList<string> ApprovedExtensions { get; init; }
    public required string LocalTargetPath { get; init; }
    public required string NasMappedTargetPath { get; init; }
    public StandaloneTargetMode TargetMode { get; init; } = StandaloneTargetMode.LocalAndNas;
    public TargetNamingRule TargetNamingRule { get; init; }
    public bool AutoStartOnLogin { get; init; }
    public Guid DefaultCameraTemplateId { get; init; }
    public IReadOnlyList<StandaloneCameraTemplate> CameraTemplates { get; init; } = [];
    public IReadOnlyList<StandaloneCardProfile> CardProfiles { get; init; } = [];

    [JsonIgnore]
    public IReadOnlyList<string> NormalizedExtensions => ApprovedExtensions
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(NormalizeExtension)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    [JsonIgnore]
    public IReadOnlyList<StandaloneCameraTemplate> EffectiveCameraTemplates => CameraTemplates.Count > 0
        ? CameraTemplates
        :
        [
            new StandaloneCameraTemplate
            {
                TemplateId = LegacyCameraTemplateId,
                Name = "默认相机",
                ApprovedSourceDirectories = ApprovedSourceDirectories,
                ApprovedExtensions = ApprovedExtensions,
            },
        ];

    [JsonIgnore]
    public Guid EffectiveDefaultCameraTemplateId => DefaultCameraTemplateId != Guid.Empty
        ? DefaultCameraTemplateId
        : EffectiveCameraTemplates[0].TemplateId;

    [JsonIgnore]
    public StandaloneCameraTemplate DefaultCameraTemplate => EffectiveCameraTemplates.Single(
        value => value.TemplateId == EffectiveDefaultCameraTemplateId);

    public StandaloneConfiguration NormalizeForCurrentSchema()
    {
        IReadOnlyList<StandaloneCameraTemplate> templates = EffectiveCameraTemplates
            .Select(template => template with
            {
                Name = template.Name.Trim(),
                ApprovedSourceDirectories = NormalizeSourceDirectories(template.ApprovedSourceDirectories),
                ApprovedExtensions = template.NormalizedExtensions,
            })
            .ToArray();
        Guid defaultId = EffectiveDefaultCameraTemplateId;
        StandaloneCameraTemplate selected = templates.Single(value => value.TemplateId == defaultId);
        return this with
        {
            SchemaVersion = 2,
            ApprovedSourceDirectories = selected.ApprovedSourceDirectories,
            ApprovedExtensions = selected.NormalizedExtensions,
            TargetNamingRule = TargetNamingRule.CardNameAndImportTime,
            DefaultCameraTemplateId = defaultId,
            CameraTemplates = templates,
            CardProfiles = CardProfiles.ToArray(),
        };
    }

    public bool RequiresSchemaMigration()
    {
        if (SchemaVersion != 2 || CameraTemplates.Count == 0 || DefaultCameraTemplateId == Guid.Empty ||
            TargetNamingRule != TargetNamingRule.CardNameAndImportTime)
            return true;

        return CameraTemplates.Any(template =>
            !template.ApprovedSourceDirectories.SequenceEqual(
                NormalizeSourceDirectories(template.ApprovedSourceDirectories),
                StringComparer.OrdinalIgnoreCase) ||
            !template.ApprovedExtensions.SequenceEqual(
                template.NormalizedExtensions,
                StringComparer.OrdinalIgnoreCase) ||
            !string.Equals(template.Name, template.Name.Trim(), StringComparison.Ordinal));
    }

    public ConfigurationValidationResult Validate()
    {
        var errors = new List<ConfigurationValidationError>();

        if (ApprovedSourceDirectories is null || ApprovedExtensions is null ||
            LocalTargetPath is null || NasMappedTargetPath is null ||
            CameraTemplates is null || CardProfiles is null)
        {
            errors.Add(new("configuration_structure_invalid", "The configuration contains a missing required collection or path."));
            return new(errors.AsReadOnly());
        }

        if (SchemaVersion is < 1 or > 2)
            errors.Add(new("schema_version_invalid", "The configuration schema version is not supported."));
        if (!Enum.IsDefined(TargetMode))
            errors.Add(new("target_mode_invalid", "The selected target mode is not supported."));
        if (!Enum.IsDefined(TargetNamingRule))
            errors.Add(new("target_naming_rule_invalid", "The selected target naming rule is not supported."));

        StandaloneCameraTemplate[] templates = EffectiveCameraTemplates.ToArray();
        if (templates.Any(template => template is null))
        {
            errors.Add(new("camera_template_invalid", "Camera templates cannot contain null entries."));
            return new(errors.AsReadOnly());
        }
        if (templates.Any(template => template.TemplateId == Guid.Empty) ||
            templates.Select(template => template.TemplateId).Distinct().Count() != templates.Length)
        {
            errors.Add(new("camera_template_id_invalid", "Camera template identities must be non-empty and unique."));
        }
        if (templates.Any(template => string.IsNullOrWhiteSpace(template.Name)) ||
            templates.Select(template => template.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != templates.Length)
        {
            errors.Add(new("camera_template_name_invalid", "Camera template names must be non-empty and unique."));
        }
        foreach (StandaloneCameraTemplate template in templates)
            ValidateSelectionPolicy(template, errors);
        if (templates.All(template => template.TemplateId != EffectiveDefaultCameraTemplateId))
            errors.Add(new("default_camera_template_invalid", "The default camera template does not exist."));

        if (CardProfiles.Any(profile => profile is null) ||
            CardProfiles.Any(profile => profile.CardInstanceId == Guid.Empty ||
                profile.CameraTemplateId == Guid.Empty || string.IsNullOrWhiteSpace(profile.DisplayName)) ||
            CardProfiles.Select(profile => profile.CardInstanceId).Distinct().Count() != CardProfiles.Count ||
            CardProfiles.Any(profile => templates.All(template => template.TemplateId != profile.CameraTemplateId)))
        {
            errors.Add(new("card_profile_invalid", "Card profiles must have unique card identities and reference an existing camera template."));
        }

        string? localTarget = TargetMode.RequiresLocal()
            ? NormalizeAbsoluteTarget(LocalTargetPath, "local_target_invalid", errors)
            : null;
        string? nasTarget = TargetMode.RequiresNas()
            ? NormalizeAbsoluteTarget(NasMappedTargetPath, "nas_target_invalid", errors)
            : null;
        if (TargetMode == StandaloneTargetMode.LocalAndNas &&
            localTarget is not null && nasTarget is not null && PathsOverlap(localTarget, nasTarget))
        {
            errors.Add(new(
                "targets_overlap",
                "The local and mapped NAS targets must not be the same path or contain one another."));
        }

        if (TargetMode.RequiresNas() && nasTarget is not null)
        {
            string? root = Path.GetPathRoot(nasTarget);
            if (string.IsNullOrWhiteSpace(root) || !root.EndsWith(@":\", StringComparison.Ordinal))
            {
                errors.Add(new(
                    "nas_target_not_mapped_drive",
                    "The NAS target must use a mapped drive letter."));
            }
        }

        return new(errors.AsReadOnly());
    }

    private static void ValidateSelectionPolicy(
        StandaloneCameraTemplate template,
        ICollection<ConfigurationValidationError> errors)
    {
        if (template.ApprovedSourceDirectories is null || template.ApprovedExtensions is null)
        {
            errors.Add(new("camera_template_structure_invalid", $"Camera template '{template.Name}' contains a missing selection collection."));
            return;
        }
        if (template.ApprovedSourceDirectories.Count == 0)
            errors.Add(new("source_directory_required", $"Camera template '{template.Name}' requires a source directory."));
        foreach (string sourceDirectory in template.ApprovedSourceDirectories)
        {
            if (!IsSafeRelativeDirectory(sourceDirectory))
            {
                errors.Add(new(
                    "source_directory_invalid",
                    $"Approved source directory must be a relative path without traversal: '{sourceDirectory}'."));
            }
        }
        if (template.NormalizedExtensions.Count == 0 ||
            template.ApprovedExtensions.Any(extension => !IsSafeExtension(extension)))
        {
            errors.Add(new(
                "extension_invalid",
                $"Camera template '{template.Name}' has invalid approved extensions."));
        }
    }

    private static IReadOnlyList<string> NormalizeSourceDirectories(IEnumerable<string> values)
    {
        var normalized = new List<string>();
        foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string candidate = value.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (candidate is "." or "")
                candidate = ".";
            else
                candidate = candidate.Trim(Path.DirectorySeparatorChar);
            if (normalized.Contains(".", StringComparer.OrdinalIgnoreCase))
                continue;
            if (normalized.Any(existing => IsSameOrDescendant(existing, candidate)))
                continue;
            normalized.RemoveAll(existing => IsSameOrDescendant(candidate, existing));
            normalized.Add(candidate);
        }
        return normalized;
    }

    private static bool IsSameOrDescendant(string parent, string candidate) =>
        string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeRelativeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;

        string normalized = value.Replace('/', (char)92).Trim((char)92);
        if (normalized == ".")
            return true;
        if (normalized.Length == 0 || normalized.Contains(':', StringComparison.Ordinal))
            return false;

        string[] segments = normalized.Split((char)92, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment =>
            segment is not "." and not ".." &&
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    private static bool IsSafeExtension(string value)
    {
        string normalized = NormalizeExtension(value);
        return normalized.Length is >= 2 and <= 17 &&
            normalized[0] == '.' &&
            normalized.AsSpan(1).ToArray().All(char.IsLetterOrDigit);
    }

    internal static string NormalizeExtension(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length > 0 && normalized[0] == '.' ? normalized : $".{normalized}";
    }

    private static string? NormalizeAbsoluteTarget(
        string value,
        string errorCode,
        ICollection<ConfigurationValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new(errorCode, "A target path is required."));
            return null;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (!Path.IsPathRooted(normalized))
                throw new ArgumentException("Target path is not absolute.");
            return normalized;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(new(errorCode, $"Target path is invalid: '{value}'."));
            return null;
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        string leftPrefix = left + Path.DirectorySeparatorChar;
        string rightPrefix = right + Path.DirectorySeparatorChar;
        return leftPrefix.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase) ||
               rightPrefix.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
