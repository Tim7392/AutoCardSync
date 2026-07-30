using AutoCardSync.Domain.Manifests;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record ManifestJournalBindingValidationResult(
    bool IsValid,
    IReadOnlyList<string> Reasons);

public static class ManifestJournalBindingValidator
{
    public static ManifestJournalBindingValidationResult Validate(
        TaskManifest manifest,
        StandaloneTaskJournal journal)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(journal);

        var reasons = new List<string>();
        if (manifest.FrozenAt is null || string.IsNullOrWhiteSpace(manifest.ManifestHash))
            reasons.Add("manifest_not_frozen");
        if (manifest.TaskId != journal.TaskId)
            reasons.Add("task_id_mismatch");
        if (!string.Equals(
                manifest.ManifestHash,
                journal.ManifestHash,
                StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("manifest_hash_mismatch");
        }

        ManifestEntry[] manifestFiles = manifest.Entries
            .Where(entry => !entry.Excluded)
            .ToArray();
        StandaloneFileJournal[] journalFiles = journal.Files.ToArray();

        ValidateManifestFacts(manifestFiles, reasons);
        ValidateJournalFacts(journalFiles, reasons);

        Dictionary<Guid, ManifestEntry> manifestById = manifestFiles
            .Where(entry => entry.Id != Guid.Empty)
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<Guid, StandaloneFileJournal> journalById = journalFiles
            .Where(file => file.FileId != Guid.Empty)
            .GroupBy(file => file.FileId)
            .ToDictionary(group => group.Key, group => group.First());

        foreach ((Guid fileId, ManifestEntry entry) in manifestById)
        {
            if (!journalById.TryGetValue(fileId, out StandaloneFileJournal? file))
            {
                reasons.Add($"journal_file_missing:{fileId:N}");
                continue;
            }

            CompareFileFacts(entry, file, reasons);
        }

        foreach (Guid fileId in journalById.Keys.Except(manifestById.Keys))
            reasons.Add($"journal_file_extra:{fileId:N}");

        return new(reasons.Count == 0, reasons.AsReadOnly());
    }

    public static void EnsureValid(
        TaskManifest manifest,
        StandaloneTaskJournal journal)
    {
        ManifestJournalBindingValidationResult result = Validate(manifest, journal);
        if (!result.IsValid)
        {
            throw new InvalidDataException(
                $"Manifest and journal are not exactly bound: {string.Join(", ", result.Reasons)}");
        }
    }

    private static void ValidateManifestFacts(
        IReadOnlyList<ManifestEntry> files,
        ICollection<string> reasons)
    {
        foreach (IGrouping<Guid, ManifestEntry> duplicate in files
                     .GroupBy(entry => entry.Id)
                     .Where(group => group.Count() > 1))
        {
            reasons.Add($"manifest_duplicate_file_id:{duplicate.Key:N}");
        }

        var paths = new List<(Guid FileId, string NormalizedPath)>();
        foreach (ManifestEntry entry in files)
        {
            if (entry.Id == Guid.Empty)
                reasons.Add("manifest_invalid_file_id:empty");
            if (entry.FileSize < 0)
                reasons.Add($"manifest_invalid_length:{entry.Id:N}");

            if (TryNormalizeRelativePath(entry.RelativePath, out string normalizedPath))
                paths.Add((entry.Id, normalizedPath));
            else
                reasons.Add($"manifest_invalid_relative_path:{entry.Id:N}");

            if (!TryNormalizeSha256(entry.SourceHash, out _))
                reasons.Add($"manifest_invalid_source_sha256:{entry.Id:N}");
        }

        AddDuplicatePathReasons(paths, "manifest_duplicate_relative_path", reasons);
    }

    private static void ValidateJournalFacts(
        IReadOnlyList<StandaloneFileJournal> files,
        ICollection<string> reasons)
    {
        foreach (IGrouping<Guid, StandaloneFileJournal> duplicate in files
                     .GroupBy(file => file.FileId)
                     .Where(group => group.Count() > 1))
        {
            reasons.Add($"journal_duplicate_file_id:{duplicate.Key:N}");
        }

        var paths = new List<(Guid FileId, string NormalizedPath)>();
        foreach (StandaloneFileJournal file in files)
        {
            if (file.FileId == Guid.Empty)
                reasons.Add("journal_invalid_file_id:empty");
            if (file.Length < 0)
                reasons.Add($"journal_invalid_length:{file.FileId:N}");

            if (TryNormalizeRelativePath(file.RelativePath, out string normalizedPath))
                paths.Add((file.FileId, normalizedPath));
            else
                reasons.Add($"journal_invalid_relative_path:{file.FileId:N}");

            if (!TryNormalizeSha256(file.SourceSha256, out _))
                reasons.Add($"journal_invalid_source_sha256:{file.FileId:N}");
        }

        AddDuplicatePathReasons(paths, "journal_duplicate_relative_path", reasons);
    }

    private static void AddDuplicatePathReasons(
        IEnumerable<(Guid FileId, string NormalizedPath)> paths,
        string reason,
        ICollection<string> reasons)
    {
        foreach (IGrouping<string, (Guid FileId, string NormalizedPath)> duplicate in paths
                     .GroupBy(path => path.NormalizedPath, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            reasons.Add($"{reason}:{duplicate.Key}");
        }
    }

    private static void CompareFileFacts(
        ManifestEntry entry,
        StandaloneFileJournal file,
        ICollection<string> reasons)
    {
        if (TryNormalizeRelativePath(entry.RelativePath, out string manifestPath)
            && TryNormalizeRelativePath(file.RelativePath, out string journalPath)
            && !string.Equals(manifestPath, journalPath, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"relative_path_mismatch:{entry.Id:N}");
        }

        if (entry.FileSize != file.Length)
            reasons.Add($"length_mismatch:{entry.Id:N}");

        if (TryNormalizeSha256(entry.SourceHash, out string manifestHash)
            && TryNormalizeSha256(file.SourceSha256, out string journalHash)
            && !string.Equals(manifestHash, journalHash, StringComparison.Ordinal))
        {
            reasons.Add($"source_sha256_mismatch:{entry.Id:N}");
        }
    }

    private static bool TryNormalizeRelativePath(
        string? relativePath,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        string path = relativePath.Replace('/', '\\');
        if (Path.IsPathRooted(path)
            || path.StartsWith('\\')
            || path.EndsWith('\\'))
        {
            return false;
        }

        string[] segments = path.Split('\\');
        if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
            return false;

        foreach (string segment in segments)
        {
            if (segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Any(IsInvalidWindowsFileNameCharacter))
            {
                return false;
            }
        }

        normalizedPath = string.Join('\\', segments);
        return true;
    }

    private static bool IsInvalidWindowsFileNameCharacter(char value) =>
        char.IsControl(value) || value is '<' or '>' or ':' or '"' or '|' or '?' or '*';

    private static bool TryNormalizeSha256(string? value, out string normalizedHash)
    {
        normalizedHash = string.Empty;
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            return false;

        normalizedHash = value.ToUpperInvariant();
        return true;
    }
}
