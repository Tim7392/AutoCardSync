using System.Security.Cryptography;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Application.Copying;

public static class CopyPathConvention
{
    /// <summary>
    /// Creates stable single-level destination names while preserving unique source file names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildDestinationRelativePathMap(
        IReadOnlyList<string> sourceRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(sourceRelativePaths);
        if (sourceRelativePaths.Any(string.IsNullOrWhiteSpace) ||
            sourceRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceRelativePaths.Count)
        {
            throw new InvalidDataException("Source relative paths must be non-empty and unique.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IGrouping<string, string>[] groups = sourceRelativePaths
            .GroupBy(GetFlatFileName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (IGrouping<string, string> group in groups.Where(group => group.Count() == 1))
        {
            string source = group.Single();
            if (!used.Add(group.Key))
                throw new InvalidDataException("Destination file names are not unique.");
            result.Add(source, group.Key);
        }

        foreach (IGrouping<string, string> group in groups.Where(group => group.Count() > 1))
        {
            foreach (string source in group.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                    source.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                        .ToUpperInvariant())));
                string? destination = null;
                for (int length = 8; length <= hash.Length; length += 4)
                {
                    string candidate = AddStableSuffix(group.Key, hash[..Math.Min(length, hash.Length)]);
                    if (used.Add(candidate))
                    {
                        destination = candidate;
                        break;
                    }
                }
                if (destination is null)
                    throw new InvalidDataException("Unable to create a unique flat destination file name.");
                result.Add(source, destination);
            }
        }

        return result;
    }

    /// <summary>Resolves the frozen target-relative path while preserving legacy journal layouts.</summary>
    public static string ResolveDestinationRelativePath(
        int schemaVersion,
        string sourceRelativePath,
        string destinationRelativePath) =>
        schemaVersion >= 3
            ? ValidateFlatFileName(destinationRelativePath)
            : sourceRelativePath;

    /// <summary>Resolves one journal file's target-relative path according to its schema.</summary>
    public static string GetDestinationRelativePath(
        StandaloneTaskJournal journal,
        StandaloneFileJournal file)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(file);
        return ResolveDestinationRelativePath(
            journal.SchemaVersion,
            file.RelativePath,
            file.DestinationRelativePath);
    }

    public static string GetFinalPath(string targetRoot, string relativePath)
        => SafePathResolver.ResolveSafePath(targetRoot, relativePath);

    public static string GetFinalPath(
        string targetRoot,
        StandaloneTaskJournal journal,
        StandaloneFileJournal file) =>
        GetFinalPath(targetRoot, GetDestinationRelativePath(journal, file));

    public static string GetTempPath(
        string targetRoot,
        string relativePath,
        Guid taskId,
        Guid fileId,
        Guid targetId)
    {
        byte[] ownershipHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{taskId:N}:{fileId:N}:{targetId:N}"));
        string ownershipToken = Convert.ToHexStringLower(ownershipHash.AsSpan(0, 16));
        return SafePathResolver.ResolveSafePath(
            targetRoot, $"{relativePath}.partial.{ownershipToken}");
    }

    public static string GetTempPath(
        string targetRoot,
        StandaloneTaskJournal journal,
        StandaloneFileJournal file,
        Guid taskId,
        Guid targetId) =>
        GetTempPath(targetRoot, GetDestinationRelativePath(journal, file), taskId, file.FileId, targetId);

    /// <summary>Validates that a schema-v3 destination contains exactly one file-name segment.</summary>
    public static string ValidateFlatFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value is "." or ".." ||
            value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A flat destination path must contain exactly one file name.");
        }
        return value;
    }

    private static string GetFlatFileName(string sourceRelativePath) =>
        ValidateFlatFileName(Path.GetFileName(sourceRelativePath));

    private static string AddStableSuffix(string fileName, string suffix)
    {
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem}__{suffix}{extension}";
    }
}
