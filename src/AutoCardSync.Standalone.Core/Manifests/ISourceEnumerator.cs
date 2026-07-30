namespace AutoCardSync.Application.Manifests;

public sealed record SourceFileSnapshot(
    string FullPath,
    string RelativePath,
    long FileSize,
    DateTimeOffset LastModifiedUtc,
    string FileId,
    string FileIdType);

public interface ISourceEnumerator
{
    IAsyncEnumerable<SourceFileSnapshot> EnumerateAsync(
        string sourceRoot,
        CancellationToken cancellationToken);
}
