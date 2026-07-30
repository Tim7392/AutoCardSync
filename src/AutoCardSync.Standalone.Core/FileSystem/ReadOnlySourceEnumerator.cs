using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace AutoCardSync.Infrastructure.FileSystem;

/// <summary>
/// Enumerator for files on a source volume.
/// No create, write, delete, move, or rename operations are exposed at the API level.
/// Note: read-only enforcement is API-level only; the underlying OS does not open
/// files with read-only sharing flags, so concurrent writers are not prevented.
/// </summary>
public sealed class ReadOnlySourceEnumerator
{
    private static readonly EnumerationOptions DefaultOptions = new()
    {
        // Missing a source file is a data-integrity failure, not a condition to
        // hide. Let access/enumeration errors abort the scan so it cannot be
        // reported as complete with silently omitted media.
        IgnoreInaccessible = false,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchType = MatchType.Simple,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Enumerates all regular files under <paramref name="sourceRoot"/> (excluding reparse points and ADS).
    /// </summary>
    public async IAsyncEnumerable<SourceFileInfo> EnumerateFiles(
        string sourceRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);

        var entries = Directory.EnumerateFiles(sourceRoot, "*", DefaultOptions);

        foreach (var fullPath in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceRoot, fullPath);
            string safeFullPath = SafePathResolver.ResolveSafePath(sourceRoot, relativePath);
            var fileInfo = new FileInfo(safeFullPath);

            string? fileId = null;
            string? fileIdType = null;
            if (OperatingSystem.IsWindows())
            {
                var identity = FileIdentity.GetFileIdentity(safeFullPath);
                fileId = $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";
                fileIdType = "NTFS_FileId";
            }

            var info = new SourceFileInfo(
                FullPath: safeFullPath,
                RelativePath: relativePath,
                FileSize: fileInfo.Length,
                LastModifiedUtc: fileInfo.LastWriteTimeUtc,
                FileId: fileId,
                FileIdType: fileIdType);

            // Yield on the thread pool to avoid blocking the caller on large trees.
            await Task.Yield();

            yield return info;
        }
    }
}

/// <summary>
/// Immutable descriptor for a single file discovered on the source volume.
/// </summary>
public sealed record SourceFileInfo(
    string FullPath,
    string RelativePath,
    long FileSize,
    DateTimeOffset LastModifiedUtc,
    string? FileId,
    string? FileIdType);
