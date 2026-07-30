using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace AutoCardSync.Infrastructure.FileSystem;

public enum ViolationType
{
    Traversal,
    AbsolutePath,
    ReservedName,
    TrailingDot,
    EscapesRoot,
    ReparsePoint,
    JunctionEscape,
    DeviceNamespace
}

public class PathViolationException : Exception
{
    public ViolationType ViolationType { get; }
    public string OffendingPath { get; }

    public PathViolationException(ViolationType violationType, string offendingPath, string message)
        : base(message)
    {
        ViolationType = violationType;
        OffendingPath = offendingPath;
    }
}

public class SafePathResolver
{
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ResolveSafePath(string basePath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            throw new ArgumentException("Base path must not be null or empty.", nameof(basePath));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path must not be null or empty.", nameof(relativePath));

        // Reject absolute paths in relativePath
        if (Path.IsPathRooted(relativePath))
            throw new PathViolationException(
                ViolationType.AbsolutePath,
                relativePath,
                $"Relative path must not be absolute: '{relativePath}'");

        string[] segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        // Reject only an actual parent-directory segment. Double dots inside
        // a legal file name (for example clip..001.mov) are not traversal.
        if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
            throw new PathViolationException(
                ViolationType.Traversal,
                relativePath,
                $"Relative path must not contain '..' segments: '{relativePath}'");

        // Normalize basePath
        string normalizedBase = Path.GetFullPath(basePath);

        // Validate each segment of the relative path
        foreach (string segment in segments)
        {
            ValidateSegment(segment);
        }

        // Combine and normalize the full path
        string combined = Path.Combine(normalizedBase, relativePath);
        string resolved = Path.GetFullPath(combined);

        // Canonical check: resolved path must be under basePath
        if (!IsSubpathOf(resolved, normalizedBase))
            throw new PathViolationException(
                ViolationType.EscapesRoot,
                relativePath,
                $"Resolved path '{resolved}' escapes base path '{normalizedBase}'");

        // Resolve every existing path component, not only the final leaf. A
        // junction ancestor can redirect a not-yet-created leaf outside the
        // root even though File.Exists(resolved) is false.
        if (OperatingSystem.IsWindows())
            ValidateExistingPathChain(normalizedBase, segments, relativePath);

        return resolved;
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateExistingPathChain(
        string normalizedBase,
        IReadOnlyList<string> relativeSegments,
        string offendingPath)
    {
        string? pathRoot = Path.GetPathRoot(normalizedBase);
        if (string.IsNullOrEmpty(pathRoot))
            throw new PathViolationException(
                ViolationType.JunctionEscape,
                offendingPath,
                $"Cannot determine the filesystem root for '{normalizedBase}'.");

        string current = pathRoot;
        ValidateExistingComponent(
            current, normalizedBase, offendingPath, enforceContainment: false);

        string[] baseSegments = Path.GetRelativePath(pathRoot, normalizedBase).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in baseSegments.Concat(relativeSegments))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;

            ValidateExistingComponent(
                current,
                normalizedBase,
                offendingPath,
                enforceContainment: IsSubpathOf(current, normalizedBase));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateExistingComponent(
        string path,
        string normalizedBase,
        string offendingPath,
        bool enforceContainment)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new PathViolationException(
                    ViolationType.ReparsePoint,
                    offendingPath,
                    $"Path contains a reparse point (junction/symlink/mount): '{path}'");
            }

            string finalTarget = JunctionResolver.GetFinalTarget(path);
            string canonicalBase = JunctionResolver.GetFinalTarget(normalizedBase);
            if (enforceContainment && !IsSubpathOf(finalTarget, canonicalBase))
            {
                throw new PathViolationException(
                    ViolationType.JunctionEscape,
                    offendingPath,
                    $"Path component resolved to '{finalTarget}' which escapes base '{canonicalBase}'");
            }
        }
        catch (PathViolationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PathViolationException(
                ViolationType.JunctionEscape,
                offendingPath,
                $"Cannot safely resolve existing path component '{path}': {ex.Message}");
        }
    }

    public static bool IsSubpathOf(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent))
            return false;

        string normalizedChild = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);

        // Case-insensitive on Windows, case-sensitive elsewhere
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (normalizedChild.Equals(normalizedParent, comparison))
            return true;

        string prefix = normalizedParent + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(prefix, comparison);
    }

    public static string ResolveUncPath(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
            throw new ArgumentException("UNC path must not be null or empty.", nameof(uncPath));

        string normalized = uncPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // Must start with \\
        if (!normalized.StartsWith(@"\\"))
            throw new PathViolationException(
                ViolationType.AbsolutePath,
                uncPath,
                $"UNC path must start with '\\\\': '{uncPath}'");

        // Strip prefix and validate structure: \\server\share[\...]
        string trimmed = normalized.Substring(2); // remove leading \\
        string[] parts = trimmed.Split(
            new[] { Path.DirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
            throw new PathViolationException(
                ViolationType.AbsolutePath,
                uncPath,
                $"UNC path must have at least server and share components: '{uncPath}'");

        string server = parts[0];
        string share = parts[1];

        if (string.IsNullOrWhiteSpace(server))
            throw new PathViolationException(
                ViolationType.AbsolutePath,
                uncPath,
                $"UNC server name must not be empty: '{uncPath}'");

        // BUG-15: Reject Windows device namespace paths.
        // \\.\ and \\?\ bypass normal path validation and can access device
        // objects directly (e.g., \\?\C:\, \\.\PhysicalDrive0). These must
        // never be accepted as UNC paths for file operations.
        if (server is "." or "?")
            throw new PathViolationException(
                ViolationType.DeviceNamespace,
                uncPath,
                $"UNC device namespace paths (\\\\.\\ or \\\\?\\) are not allowed: '{uncPath}'");

        if (string.IsNullOrWhiteSpace(share))
            throw new PathViolationException(
                ViolationType.AbsolutePath,
                uncPath,
                $"UNC share name must not be empty: '{uncPath}'");

        // Validate remaining path segments if any
        for (int i = 2; i < parts.Length; i++)
        {
            ValidateSegment(parts[i]);
        }

        // Rebuild canonical UNC path
        string canonical = @"\\" + server + @"\" + share;
        if (parts.Length > 2)
        {
            canonical = canonical + Path.DirectorySeparatorChar
                + string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(2));
        }

        return canonical;
    }

    private static void ValidateSegment(string segment)
    {
        // Check reserved device names
        string upper = segment.ToUpperInvariant();
        string nameWithoutExtension = upper.Contains('.')
            ? upper.Substring(0, upper.IndexOf('.'))
            : upper;

        if (ReservedNames.Contains(nameWithoutExtension))
            throw new PathViolationException(
                ViolationType.ReservedName,
                segment,
                $"Path segment is a reserved device name: '{segment}'");

        // Check trailing dots or spaces
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            throw new PathViolationException(
                ViolationType.TrailingDot,
                segment,
                $"Path segment must not end with dots or spaces: '{segment}'");
    }
}
