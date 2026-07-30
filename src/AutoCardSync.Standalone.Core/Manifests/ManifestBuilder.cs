using System.Security.Cryptography;
using AutoCardSync.Application.Ingestion;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.FileSystem;

namespace AutoCardSync.Application.Manifests;

public sealed class ManifestBuilder(ISourceEnumerator sourceEnumerator)
{
    public async Task<TaskManifest> BuildAsync(
        Guid taskId, string sourceRoot, string filterRuleVersion,
        SourceHashPolicy hashPolicy, SourceSelectionPolicy selectionPolicy,
        CancellationToken cancellationToken)
    {
        ValidateSourceRoot(sourceRoot);
        selectionPolicy.Validate();
        var manifest = new TaskManifest(taskId) { FilterRuleVersion = filterRuleVersion };

        foreach (string relativeRoot in selectionPolicy.GetNormalizedRoots())
        {
            string scanRoot = relativeRoot.Length == 0
                ? Path.GetFullPath(sourceRoot)
                : SafePathResolver.ResolveSafePath(sourceRoot, relativeRoot);
            if (!Directory.Exists(scanRoot))
                throw new DirectoryNotFoundException(scanRoot);
            RejectReparsePoints(scanRoot);

            await foreach (SourceFileSnapshot file in sourceEnumerator.EnumerateAsync(scanRoot, cancellationToken))
            {
                string relativePath = relativeRoot.Length == 0
                    ? file.RelativePath
                    : Path.Combine(relativeRoot, file.RelativePath);
                SourceFileSnapshot scoped = file with { RelativePath = relativePath };
                bool included = selectionPolicy.IncludesExtension(relativePath);
                manifest.AddEntry(await CreateEntryAsync(
                    taskId, sourceRoot, scoped, hashPolicy, included, cancellationToken));
            }
        }

        IReadOnlyList<string> errors = manifest.Validate();
        if (errors.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        manifest.Freeze();
        return manifest;
    }

    public static TaskManifest CreateDeltaInventoryManifest(
        TaskManifest inventoryManifest,
        IReadOnlyCollection<string> addedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(inventoryManifest);
        ArgumentNullException.ThrowIfNull(addedRelativePaths);
        if (inventoryManifest.FrozenAt is null || string.IsNullOrWhiteSpace(inventoryManifest.ManifestHash))
            throw new InvalidDataException("The metadata inventory must be frozen before delta selection.");

        var selected = new HashSet<string>(addedRelativePaths, StringComparer.OrdinalIgnoreCase);
        var delta = new TaskManifest(inventoryManifest.TaskId)
        {
            FilterRuleVersion = inventoryManifest.FilterRuleVersion,
        };
        foreach (ManifestEntry entry in inventoryManifest.Entries
                     .Where(entry => !entry.Excluded && selected.Contains(entry.RelativePath)))
        {
            delta.AddEntry(entry);
        }
        if (delta.TotalFiles != selected.Count)
            throw new InvalidDataException("The added-file selection does not match the metadata inventory.");
        delta.Freeze();
        return delta;
    }

    public static TaskManifest CreateContentManifest(
        TaskManifest inventoryManifest,
        IReadOnlyDictionary<Guid, string> sourceHashes)
    {
        ArgumentNullException.ThrowIfNull(inventoryManifest);
        ArgumentNullException.ThrowIfNull(sourceHashes);
        if (inventoryManifest.FrozenAt is null || string.IsNullOrWhiteSpace(inventoryManifest.ManifestHash))
            throw new InvalidDataException("The inventory manifest must be frozen before content hashing.");

        var contentManifest = new TaskManifest(inventoryManifest.TaskId)
        {
            FilterRuleVersion = inventoryManifest.FilterRuleVersion,
        };
        foreach (ManifestEntry entry in inventoryManifest.Entries)
        {
            string? sourceHash = entry.Excluded
                ? null
                : sourceHashes.TryGetValue(entry.Id, out string? value) && !string.IsNullOrWhiteSpace(value)
                    ? value
                    : throw new InvalidDataException(
                        $"Source SHA-256 is missing for inventory entry '{entry.RelativePath}'.");
            contentManifest.AddEntry(entry with { SourceHash = sourceHash });
        }

        IReadOnlyList<string> errors = contentManifest.Validate();
        if (errors.Count != 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        contentManifest.Freeze();
        return contentManifest;
    }

    private static void ValidateSourceRoot(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Path.IsPathFullyQualified(sourceRoot))
            throw new ArgumentException(nameof(sourceRoot));
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);
    }

    private static void RejectReparsePoints(string sourceRoot)
    {
        if ((File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0)
            throw new PathViolationException(ViolationType.ReparsePoint, sourceRoot, nameof(sourceRoot));
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new PathViolationException(ViolationType.ReparsePoint, path, nameof(path));
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
            }
        }
    }

    private static async Task<ManifestEntry> CreateEntryAsync(
        Guid taskId, string sourceRoot, SourceFileSnapshot file, SourceHashPolicy hashPolicy, bool included,
        CancellationToken cancellationToken)
    {
        string safePath = SafePathResolver.ResolveSafePath(sourceRoot, file.RelativePath);
        if (!string.Equals(Path.GetFullPath(file.FullPath), safePath, StringComparison.OrdinalIgnoreCase))
            throw new PathViolationException(ViolationType.Traversal, file.FullPath, nameof(file.FullPath));
        if (file.FileSize < 0 || string.IsNullOrWhiteSpace(file.FileId) || string.IsNullOrWhiteSpace(file.FileIdType))
            throw new InvalidDataException(nameof(file));

        string? hash = included && hashPolicy == SourceHashPolicy.Sha256DuringScan
            ? await HashAsync(safePath, cancellationToken)
            : null;
        return new ManifestEntry
        {
            Id = CreateDeterministicEntryId(taskId, file),
            RelativePath = file.RelativePath,
            FileSize = file.FileSize,
            LastModifiedUtc = file.LastModifiedUtc.ToUniversalTime(),
            SourceFileId = file.FileId,
            SourceFileIdType = file.FileIdType,
            SourceHash = hash,
            Excluded = !included,
            ExclusionRule = included ? null : ManifestExclusionRules.ExtensionNotIncluded,
        };
    }

    private static Guid CreateDeterministicEntryId(Guid taskId, SourceFileSnapshot file)
    {
        string input = $"{taskId:N}\n{file.RelativePath.ToUpperInvariant()}\n{file.FileIdType}\n{file.FileId}";
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        long size = before.Length;
        DateTime modified = before.LastWriteTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var after = new FileInfo(path);
        if (after.Length != size || after.LastWriteTimeUtc != modified)
            throw new IOException(nameof(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
