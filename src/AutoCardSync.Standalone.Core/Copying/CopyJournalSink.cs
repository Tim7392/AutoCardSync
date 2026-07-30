using AutoCardSync.Infrastructure.Copying;

namespace AutoCardSync.Application.Copying;

public sealed record CopyJournalFileStarted(
    Guid TaskId,
    Guid FileId,
    Guid TargetId,
    string RelativePath,
    string SourceFileId,
    string TemporaryPath,
    string FinalPath,
    long FileSize,
    string? ExpectedSha256,
    string? TemporaryObjectId = null);

public sealed record CopyJournalCheckpointCompleted(
    Guid TaskId,
    Guid FileId,
    Guid TargetId,
    string RelativePath,
    string TemporaryObjectId,
    BlockCheckpoint Checkpoint);

public sealed record CopyJournalFileVerified(
    Guid TaskId,
    Guid FileId,
    Guid TargetId,
    string RelativePath,
    string FinalPath,
    string FinalObjectId,
    long FileSize,
    string Sha256,
    bool ReusedExisting);

public sealed record CopyJournalFileFailed(
    Guid TaskId,
    Guid FileId,
    Guid TargetId,
    string RelativePath,
    string Error);

public interface ICopyJournalSink
{
    ValueTask OnFileStartedAsync(CopyJournalFileStarted value, CancellationToken cancellationToken);
    ValueTask OnCheckpointCompletedAsync(CopyJournalCheckpointCompleted value, CancellationToken cancellationToken);
    ValueTask OnFileVerifiedAsync(CopyJournalFileVerified value, CancellationToken cancellationToken);
    ValueTask OnFileFailedAsync(CopyJournalFileFailed value, CancellationToken cancellationToken);
}
