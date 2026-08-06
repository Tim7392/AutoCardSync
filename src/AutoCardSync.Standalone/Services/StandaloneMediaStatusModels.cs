namespace AutoCardSync.Standalone.Services;

public sealed record StandaloneMediaStatusDto
{
    public long Revision { get; init; }
    public string ActiveMountSessionId { get; init; } = string.Empty;
    public IReadOnlyList<StandaloneMediaItemDto> Media { get; init; } = [];
}

public sealed record StandaloneMediaItemDto
{
    public string MountSessionId { get; init; } = string.Empty;
    public string VolumeKey { get; init; } = string.Empty;
    public string DriveLetter { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long CapacityBytes { get; init; }
    public string DetectedAtUtc { get; init; } = string.Empty;
    public string PresenceState { get; init; } = "detecting";
    public string EligibilityState { get; init; } = "checking";
    public string IdentityState { get; init; } = "unknown";
    public string CardInstanceId { get; init; } = string.Empty;
    public string CardDisplayName { get; init; } = string.Empty;
    public string CameraTemplateId { get; init; } = string.Empty;
    public IReadOnlyList<string> ExpectedDirectories { get; init; } = [];
    public IReadOnlyList<string> ObservedCandidateDirectories { get; init; } = [];
    public string WorkState { get; init; } = "awaiting_action";
    public int? QueuePosition { get; init; }
    public string SafetyConclusion { get; init; } = "no_backup_conclusion";
    public string ReasonCode { get; init; } = string.Empty;
    public string PrimaryAction { get; init; } = string.Empty;
    public IReadOnlyList<string> AvailableActions { get; init; } = [];
    public int SelectedFileCount { get; init; }
    public long SelectedBytes { get; init; }
    public int DeltaFileCount { get; init; }
    public long DeltaBytes { get; init; }
    public string LastCompletedAtUtc { get; init; } = string.Empty;
    public double OverallPercent { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed record StandaloneKnownCardDto
{
    public string CardInstanceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string CameraTemplateId { get; init; } = string.Empty;
    public string CameraTemplateName { get; init; } = string.Empty;
    public IReadOnlyList<string> ApprovedSourceDirectories { get; init; } = [];
    public IReadOnlyList<string> ApprovedExtensions { get; init; } = [];
    public bool HasProfile { get; init; }
    public bool HasIdentityBinding { get; init; }
    public bool HasBaseline { get; init; }
    public bool InitializationPending { get; init; }
    public string HealthState { get; init; } = "healthy";
    public string FirstSeenUtc { get; init; } = string.Empty;
    public string LastSeenUtc { get; init; } = string.Empty;
    public string LastCompletedTaskId { get; init; } = string.Empty;
}

public sealed record StandaloneImportHistoryItemDto
{
    public string TaskId { get; init; } = string.Empty;
    public string CardInstanceId { get; init; } = string.Empty;
    public string CardDisplayName { get; init; } = "素材卡";
    public string CompletedAtUtc { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public string TargetMode { get; init; } = string.Empty;
    public bool SafeToRemoveCard { get; init; }
}
