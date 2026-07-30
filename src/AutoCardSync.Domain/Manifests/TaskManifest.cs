using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoCardSync.Domain.Manifests;

public static class ManifestExclusionRules
{
    public const string ExtensionNotIncluded = "Extension not included";
}

public sealed record ManifestEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string RelativePath { get; init; }
    public long FileSize { get; init; }
    public DateTimeOffset LastModifiedUtc { get; init; }
    public string? SourceHash { get; init; }
    public string? SourceFileId { get; init; }
    public string? SourceFileIdType { get; init; }
    public bool Excluded { get; init; }
    public string? ExclusionRule { get; init; }
}

public sealed class TaskManifest
{
    private readonly List<ManifestEntry> _entries = [];
    private bool _frozen;

    public Guid TaskId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? FrozenAt { get; private set; }
    public IReadOnlyList<ManifestEntry> Entries => _entries.AsReadOnly();
    public long TotalBytes => _entries.Where(e => !e.Excluded).Sum(e => e.FileSize);
    public int TotalFiles => _entries.Count(e => !e.Excluded);
    public int ExcludedFiles => _entries.Count(e => e.Excluded);
    public string ManifestHash { get; private set; } = string.Empty;
    public string? FilterRuleVersion { get; init; }

    public TaskManifest(Guid taskId)
    {
        TaskId = taskId;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void AddEntry(ManifestEntry entry)
    {
        ObjectDisposedException.ThrowIf(_frozen, this);

        if (_frozen)
            throw new InvalidOperationException("Cannot add entries to a frozen manifest.");

        if (string.IsNullOrWhiteSpace(entry.RelativePath))
            throw new ArgumentException("RelativePath cannot be null or empty.", nameof(entry));

        _entries.Add(entry);
    }

    public void Freeze()
    {
        if (_frozen)
            return;

        FrozenAt = DateTimeOffset.UtcNow;
        ManifestHash = ComputeCanonicalHash(_entries);
        _frozen = true;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath))
            {
                errors.Add($"Entry {entry.Id}: RelativePath is null or empty.");
                continue;
            }

            if (Path.IsPathRooted(entry.RelativePath))
            {
                errors.Add($"Entry {entry.Id}: RelativePath '{entry.RelativePath}' is an absolute path.");
            }

            string[] segments = entry.RelativePath.Split(
                new[] { (char)92, '/' },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                errors.Add($"Entry {entry.Id}: RelativePath '{entry.RelativePath}' contains path traversal.");
            }

            var invalidChars = Path.GetInvalidPathChars();
            if (entry.RelativePath.Any(c => invalidChars.Contains(c)))
            {
                errors.Add($"Entry {entry.Id}: RelativePath '{entry.RelativePath}' contains invalid characters.");
            }
        }

        return errors.AsReadOnly();
    }

    public static string ComputeCanonicalHash(IEnumerable<ManifestEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var snapshot = entries
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(e => new
            {
                e.Id,
                e.RelativePath,
                e.FileSize,
                e.LastModifiedUtc,
                e.SourceHash,
                e.SourceFileId,
                e.SourceFileIdType,
                e.Excluded,
                e.ExclusionRule,
            })
            .ToList();

        var json = JsonSerializer.Serialize(snapshot);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
