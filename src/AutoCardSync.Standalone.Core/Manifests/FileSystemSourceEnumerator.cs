using System.Runtime.CompilerServices;
using AutoCardSync.Infrastructure.FileSystem;

namespace AutoCardSync.Application.Manifests;

public sealed class FileSystemSourceEnumerator : ISourceEnumerator
{
    private readonly ReadOnlySourceEnumerator _inner = new();

    public async IAsyncEnumerable<SourceFileSnapshot> EnumerateAsync(
        string sourceRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (SourceFileInfo file in _inner.EnumerateFiles(sourceRoot, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(file.FileId) || string.IsNullOrWhiteSpace(file.FileIdType))
                throw new IOException(nameof(file.FileId));

            yield return new SourceFileSnapshot(
                file.FullPath, file.RelativePath, file.FileSize,
                file.LastModifiedUtc, file.FileId, file.FileIdType);
        }
    }
}
