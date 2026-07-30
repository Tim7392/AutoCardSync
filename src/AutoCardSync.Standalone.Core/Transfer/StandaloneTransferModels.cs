using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Safety;

namespace AutoCardSync.Standalone.Core.Transfer;

public sealed record StandaloneTargetProgress
{
    public required string Role { get; init; }
    public required string Status { get; init; }
    public required double CopyPercent { get; init; }
    public required double VerificationPercent { get; init; }
}

public sealed record StandaloneTransferProgress
{
    public required string Phase { get; init; }
    public string? CurrentFile { get; init; }
    public long BytesProcessed { get; init; }
    public long TotalBytes { get; init; }
    public double OverallPercent { get; init; }
    public double CurrentBytesPerSecond { get; init; }
    public double AverageBytesPerSecond { get; init; }
    public double? EstimatedSecondsRemaining { get; init; }
    public required StandaloneTargetProgress LocalTarget { get; init; }
    public required StandaloneTargetProgress NasTarget { get; init; }
}

public sealed record StandaloneCompletionFileFact
{
    public required Guid FileId { get; init; }
    public required string RelativePath { get; init; }
    public required long Length { get; init; }
    public required string SourceSha256 { get; init; }
    public required string LocalFinalObjectId { get; init; }
    public required string LocalFinalSha256 { get; init; }
    public required string NasFinalObjectId { get; init; }
    public required string NasFinalSha256 { get; init; }
}

public sealed record StandaloneCompletionReceipt
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid TaskId { get; init; }
    public required Guid CardInstanceId { get; init; }
    public StandaloneTargetMode TargetMode { get; init; } = StandaloneTargetMode.LocalAndNas;
    public required string ManifestHash { get; init; }
    public required string SourceIdentity { get; init; }
    public required string LocalTargetIdentity { get; init; }
    public required string NasTargetIdentity { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required IReadOnlyList<StandaloneCompletionFileFact> Files { get; init; }
    public bool SafeToRemoveCard { get; init; }
}

public sealed record StandaloneTransferResult
{
    public required Guid TaskId { get; init; }
    public required Guid CardInstanceId { get; init; }
    public StandaloneTargetMode TargetMode { get; init; } = StandaloneTargetMode.LocalAndNas;
    public required string ManifestHash { get; init; }
    public required string JournalPath { get; init; }
    public required string CompletionReceiptPath { get; init; }
    public required StandaloneSafetyResult Safety { get; init; }
}
