using System.Runtime.InteropServices;
using System.IO;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Services;

/// <summary>Reports that a required target cannot hold the remaining durable payload.</summary>
public sealed class InsufficientTargetSpaceException : IOException
{
    public InsufficientTargetSpaceException(string targetRole, long requiredBytes, long availableBytes, string targetRoot)
        : base("The selected target does not have enough available space.")
    {
        TargetRole = targetRole;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
        TargetRoot = targetRoot;
    }

    public string TargetRole { get; }
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }
    public string TargetRoot { get; }
}

/// <summary>Reports that free capacity for a selected target could not be established.</summary>
public sealed class TargetCapacityUnavailableException : IOException
{
    public TargetCapacityUnavailableException(string targetRole, string targetRoot, Exception? innerException = null)
        : base("Available capacity for the selected target could not be determined.", innerException)
    {
        TargetRole = targetRole;
        TargetRoot = targetRoot;
    }

    public string TargetRole { get; }
    public string TargetRoot { get; }
}

/// <summary>Reports that the mapped NAS target is unavailable before a copy starts.</summary>
public sealed class NasTargetUnavailableException : IOException
{
    public NasTargetUnavailableException(string targetRoot, Exception innerException)
        : base("The selected NAS target is unavailable.", innerException)
    {
        TargetRoot = targetRoot;
    }

    public string TargetRoot { get; }
}

/// <summary>Reports that a persisted recovery binding no longer matches the selected task state.</summary>
public sealed class RecoveryStateChangedException : IOException
{
    public RecoveryStateChangedException(IReadOnlyList<string> reasons)
        : base("The persisted recovery state no longer matches the selected task.")
    {
        Reasons = reasons?.ToArray() ?? [];
    }

    public IReadOnlyList<string> Reasons { get; }
}

internal static class StandaloneTargetCapacityPreflight
{
    private const long HeadroomBytes = 128L * 1024 * 1024;

    public static void EnsureSufficientSpace(
        string targetRole,
        string targetRoot,
        TaskManifest manifest,
        StandaloneTaskJournal? resumeJournal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        string normalizedRoot = Path.GetFullPath(targetRoot);
        long remainingBytes = RemainingBytes(targetRole, normalizedRoot, manifest, resumeJournal);
        long requiredBytes = remainingBytes == 0 ? 0 : checked(remainingBytes + HeadroomBytes);
        long availableBytes = GetAvailableBytes(targetRole, normalizedRoot);
        if (availableBytes < requiredBytes)
            throw new InsufficientTargetSpaceException(
                targetRole, requiredBytes, availableBytes, normalizedRoot);
    }

    private static long RemainingBytes(
        string targetRole,
        string targetRoot,
        TaskManifest manifest,
        StandaloneTaskJournal? resumeJournal)
    {
        IReadOnlyDictionary<Guid, StandaloneFileJournal>? journalFiles = null;
        if (resumeJournal?.Files is not null)
        {
            try
            {
                journalFiles = resumeJournal.Files.ToDictionary(file => file.FileId);
            }
            catch (ArgumentException)
            {
                // A malformed journal cannot earn a capacity deduction.
            }
        }

        long remaining = 0;
        foreach (ManifestEntry entry in manifest.Entries.Where(entry => !entry.Excluded))
        {
            if (entry.FileSize < 0)
                throw new InvalidDataException("The selected manifest contains an invalid file length.");

            long persistedBytes = 0;
            if (journalFiles is not null && journalFiles.TryGetValue(entry.Id, out StandaloneFileJournal? file))
            {
                StandaloneTargetFileJournal target = SelectTarget(targetRole, file);
                persistedBytes = IsVerifiedFinal(target)
                    ? entry.FileSize
                    : GetProvenCheckpointBytes(targetRoot, target, entry.FileSize);
            }

            remaining = checked(remaining + Math.Max(0, entry.FileSize - persistedBytes));
        }
        return remaining;
    }

    private static StandaloneTargetFileJournal SelectTarget(
        string targetRole,
        StandaloneFileJournal file) =>
        string.Equals(targetRole, "nas", StringComparison.Ordinal)
            ? file.NasTarget
            : file.LocalTarget;

    private static bool IsVerifiedFinal(StandaloneTargetFileJournal target) =>
        target.State == StandaloneTargetState.Verified &&
        (target.AtomicallyPublished || target.ReusedExisting) &&
        target.FullRereadSha256Passed &&
        !string.IsNullOrWhiteSpace(target.FinalObjectIdentity) &&
        !string.IsNullOrWhiteSpace(target.FinalSha256);

    private static long GetProvenCheckpointBytes(
        string targetRoot,
        StandaloneTargetFileJournal target,
        long fileLength)
    {
        if (fileLength <= 0 ||
            string.IsNullOrWhiteSpace(target.TemporaryPath) ||
            string.IsNullOrWhiteSpace(target.TemporaryObjectIdentity) ||
            !IsPathWithinRoot(target.TemporaryPath, targetRoot))
        {
            return 0;
        }

        long temporaryLength;
        try
        {
            if (!File.Exists(target.TemporaryPath))
                return 0;
            FileIdentity identity = FileIdentity.GetFileIdentity(target.TemporaryPath);
            string actualIdentity = $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";
            if (!string.Equals(actualIdentity, target.TemporaryObjectIdentity, StringComparison.Ordinal))
                return 0;
            temporaryLength = new FileInfo(target.TemporaryPath).Length;
            FileIdentity identityAfterLengthRead = FileIdentity.GetFileIdentity(target.TemporaryPath);
            string identityAfterLengthReadText = $"{identityAfterLengthRead.VolumeSerialNumber}:{identityAfterLengthRead.FileIndexHigh}:{identityAfterLengthRead.FileIndexLow}";
            if (!string.Equals(identityAfterLengthReadText, target.TemporaryObjectIdentity, StringComparison.Ordinal))
                return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        long contiguousBytes = 0;
        int expectedBlockIndex = 0;
        foreach (StandaloneBlockCheckpoint checkpoint in target.Checkpoints
                     .OrderBy(checkpoint => checkpoint.Offset)
                     .ThenBy(checkpoint => checkpoint.BlockIndex))
        {
            if (checkpoint.BlockIndex != expectedBlockIndex || checkpoint.Offset != contiguousBytes ||
                checkpoint.Length <= 0 || string.IsNullOrWhiteSpace(checkpoint.Sha256) ||
                checkpoint.PersistedAtUtc == default)
            {
                break;
            }

            long next = checked(contiguousBytes + checkpoint.Length);
            if (next > temporaryLength || next > fileLength)
                break;
            contiguousBytes = next;
            expectedBlockIndex++;
        }

        return contiguousBytes;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string normalizedPath = Path.GetFullPath(path);
            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static long GetAvailableBytes(string targetRole, string targetRoot)
    {
        string probePath;
        try
        {
            probePath = FindExistingAncestor(targetRoot);
            if (!OperatingSystem.IsWindows() ||
                !GetDiskFreeSpaceEx(probePath, out ulong available, out _, out _))
            {
                throw new IOException("Disk capacity API did not return a usable value.");
            }
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new TargetCapacityUnavailableException(targetRole, targetRoot, exception);
        }
    }

    private static string FindExistingAncestor(string targetRoot)
    {
        string candidate = Path.GetFullPath(targetRoot);
        while (!Directory.Exists(candidate))
        {
            string? parent = Directory.GetParent(candidate)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException("No existing ancestor is available for capacity preflight.");
            }
            candidate = Path.GetFullPath(parent);
        }
        return candidate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
