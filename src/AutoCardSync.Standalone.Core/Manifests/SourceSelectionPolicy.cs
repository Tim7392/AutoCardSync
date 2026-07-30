namespace AutoCardSync.Application.Manifests;

public sealed record SourceSelectionPolicy(
    IReadOnlyList<string> IncludedRelativeRoots,
    IReadOnlyList<string> IncludedExtensions)
{
    public static SourceSelectionPolicy Unfiltered { get; } = new(["."], ["*"]);

    public void Validate()
    {
        if (IncludedRelativeRoots is null || IncludedRelativeRoots.Count == 0)
            throw new InvalidDataException("At least one included relative root is required.");
        if (IncludedExtensions is null || IncludedExtensions.Count == 0)
            throw new InvalidDataException("At least one included extension is required.");

        string[] roots = GetNormalizedRoots();
        if (roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
            throw new InvalidDataException("Included relative roots must be unique.");
        for (int first = 0; first < roots.Length; first++)
        {
            for (int second = first + 1; second < roots.Length; second++)
            {
                if (IsSameOrDescendant(roots[first], roots[second]) ||
                    IsSameOrDescendant(roots[second], roots[first]))
                    throw new InvalidDataException("Included relative roots must not overlap.");
            }
        }

        string[] extensions = GetNormalizedExtensions();
        if (extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extensions.Length)
            throw new InvalidDataException("Included extensions must be unique.");
    }

    public string[] GetNormalizedRoots()
    {
        if (IncludedRelativeRoots is null)
            throw new InvalidDataException("Included relative roots are required.");
        return IncludedRelativeRoots.Select(NormalizeRoot).ToArray();
    }

    public bool IncludesExtension(string relativePath)
    {
        string[] extensions = GetNormalizedExtensions();
        return extensions.Contains("*", StringComparer.Ordinal) ||
            extensions.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase);
    }

    private string[] GetNormalizedExtensions()
    {
        if (IncludedExtensions is null)
            throw new InvalidDataException("Included extensions are required.");
        return IncludedExtensions.Select(NormalizeExtension).ToArray();
    }

    private static string NormalizeRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || Path.IsPathFullyQualified(value))
            throw new InvalidDataException("Included roots must be non-empty relative paths.");
        string normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);
        if (normalized == ".")
            return string.Empty;
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Included roots cannot contain traversal segments.");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string NormalizeExtension(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new InvalidDataException("Included extensions cannot be empty.");
        string normalized = value.Trim();
        if (normalized == "*")
            return normalized;
        if (!normalized.StartsWith('.') || normalized.Length == 1 ||
            normalized.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException("Included extensions must use the .ext form.");
        return normalized;
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            return true;
        if (parent.Length == 0)
            return true;
        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

