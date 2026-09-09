using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using AutoCardSync.Application.Copying;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core.Recovery;

namespace AutoCardSync.Standalone.Core.Transfer;

[SupportedOSPlatform("windows")]
public static class FinalPublishedObjectVerifier
{
    public static async Task<FinalPublishedObjectLease> AcquireVerifiedLeaseAsync(
        string sourceRoot,
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Published object verification requires Windows file identity APIs.");
        }

        string normalizedSourceRoot = NormalizeSourceRoot(sourceRoot);
        if (journal.Files is null)
            throw new InvalidDataException("The task journal does not contain a file collection.");

        int objectsPerFile = 1 + (journal.TargetMode.RequiresLocal() ? 1 : 0) +
            (journal.TargetMode.RequiresNas() ? 1 : 0);
        var leasedObjects = new List<LeasedPublishedObject>(checked(journal.Files.Count * objectsPerFile));
        IReadOnlyList<Action> targetRootChecks = CreateTargetRootChecks(journal);
        try
        {
            foreach (Action targetRootCheck in targetRootChecks)
                targetRootCheck();

            foreach (StandaloneFileJournal? file in journal.Files)
            {
                if (file is null)
                    throw new InvalidDataException("The task journal contains a null file entry.");
                if (file.Length < 0)
                    throw new InvalidDataException($"Journal file '{file.RelativePath}' has a negative length.");
                if (string.IsNullOrWhiteSpace(file.SourceSha256))
                    throw new InvalidDataException($"Journal file '{file.RelativePath}' has no source SHA-256 fact.");

                leasedObjects.Add(OpenSource(normalizedSourceRoot, file));
                if (journal.TargetMode.RequiresLocal())
                    leasedObjects.Add(OpenTarget(journal, file, file.LocalTarget, "local", journal.LocalTargetRoot, journal.LocalTargetIdentity));
                if (journal.TargetMode.RequiresNas())
                    leasedObjects.Add(OpenTarget(journal, file, file.NasTarget, "nas", journal.NasTargetRoot, journal.NasTargetIdentity));
            }

            var lease = new FinalPublishedObjectLease(leasedObjects, targetRootChecks);
            await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await DisposeAllAsync(leasedObjects).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task VerifyAsync(
        string sourceRoot,
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        await using FinalPublishedObjectLease lease =
            await AcquireVerifiedLeaseAsync(sourceRoot, journal, cancellationToken).ConfigureAwait(false);
    }

    private static LeasedPublishedObject OpenSource(
        string normalizedSourceRoot,
        StandaloneFileJournal file)
    {
        if (string.IsNullOrWhiteSpace(file.RelativePath))
            throw new InvalidDataException("A journal file has no relative source path.");
        if (Path.IsPathRooted(file.RelativePath))
        {
            throw new InvalidDataException(
                $"Journal source path must be relative to the frozen source root: '{file.RelativePath}'.");
        }
        if (string.IsNullOrWhiteSpace(file.SourceFileIdentity))
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has no source file identity.");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(normalizedSourceRoot, file.RelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has an invalid relative source path.", ex);
        }

        string sourcePrefix = Path.EndsInDirectorySeparator(normalizedSourceRoot)
            ? normalizedSourceRoot
            : normalizedSourceRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Journal source path escapes the frozen source root: '{file.RelativePath}'.");
        }

        return OpenObject(
            fullPath,
            file.RelativePath,
            "source",
            file.Length,
            file.SourceFileIdentity,
            [("source", file.SourceSha256)]);
    }

    private static LeasedPublishedObject OpenTarget(
        StandaloneTaskJournal journal,
        StandaloneFileJournal file,
        StandaloneTargetFileJournal target,
        string role,
        string targetRoot,
        string expectedTargetIdentity)
    {
        if (target is null)
            throw new InvalidDataException($"Journal file '{file.RelativePath}' has no {role} target fact.");
        if (!string.Equals(target.TargetRole, role, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has an invalid {role} target role '{target.TargetRole}'.");
        }
        if (!string.Equals(target.TargetIdentity, expectedTargetIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has a {role} target identity that does not match the frozen task target.");
        }
        if (string.IsNullOrWhiteSpace(target.FinalPath))
            throw new InvalidDataException($"Journal file '{file.RelativePath}' has no {role} final path.");
        if (string.IsNullOrWhiteSpace(target.FinalObjectIdentity))
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has no {role} final object identity.");
        }
        if (string.IsNullOrWhiteSpace(target.FinalSha256))
            throw new InvalidDataException($"Journal file '{file.RelativePath}' has no {role} final SHA-256 fact.");

        string expectedPath;
        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(target.FinalPath))
            {
                throw new InvalidDataException(
                    $"Journal file '{file.RelativePath}' has a non-absolute {role} final path.");
            }

            expectedPath = SafePathResolver.ResolveSafePath(
                targetRoot,
                CopyPathConvention.GetDestinationRelativePath(journal, file));
            fullPath = Path.GetFullPath(target.FinalPath);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or PathViolationException)
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' has an invalid {role} final path.", ex);
        }

        if (!string.Equals(expectedPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Journal file '{file.RelativePath}' does not bind the {role} final object to the frozen target root.");
        }

        return OpenObject(
            fullPath,
            file.RelativePath,
            role,
            file.Length,
            target.FinalObjectIdentity,
            [("source", file.SourceSha256), ("journal final", target.FinalSha256)]);
    }

    private static IReadOnlyList<Action> CreateTargetRootChecks(StandaloneTaskJournal journal)
    {
        var checks = new List<Action>(2);
        if (journal.TargetMode.RequiresLocal())
        {
            checks.Add(CreateTargetRootCheck(
                journal.LocalTargetRoot,
                journal.LocalTargetIdentity,
                "local",
                journal.SchemaVersion));
        }
        if (journal.TargetMode.RequiresNas())
        {
            checks.Add(CreateTargetRootCheck(
                journal.NasTargetRoot,
                journal.NasTargetIdentity,
                "nas",
                journal.SchemaVersion));
        }
        return checks;
    }

    private static Action CreateTargetRootCheck(
        string targetRoot,
        string expectedTargetIdentity,
        string role,
        int schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(targetRoot) || !Path.IsPathFullyQualified(targetRoot))
            throw new InvalidDataException($"The frozen {role} target root is invalid.");
        if (string.IsNullOrWhiteSpace(expectedTargetIdentity))
            throw new InvalidDataException($"The frozen {role} target identity is missing.");

        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
            _ = SafePathResolver.ResolveSafePath(normalizedRoot, ".autocardsync-root-check");
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or PathViolationException)
        {
            throw new InvalidDataException($"The frozen {role} target root is unsafe.", ex);
        }

        if (schemaVersion < 2)
            return () => _ = SafePathResolver.ResolveSafePath(normalizedRoot, ".autocardsync-root-check");

        if (string.Equals(role, "local", StringComparison.Ordinal))
        {
            if (!expectedTargetIdentity.StartsWith("local-v1:", StringComparison.Ordinal))
                throw new InvalidDataException("The frozen local target identity is not canonical.");

            return () =>
            {
                string observed = new FaultDomainResolver().ResolveLocalDomain(normalizedRoot).StorageIdentity
                    ?? throw new IOException("Unable to recapture the local target storage identity.");
                if (!string.Equals(expectedTargetIdentity, observed, StringComparison.Ordinal))
                    throw new IOException("The local target storage identity changed after publication.");
            };
        }

        if (!expectedTargetIdentity.StartsWith("nas-v1:", StringComparison.Ordinal))
            throw new InvalidDataException("The frozen NAS target identity is not canonical.");

        return () =>
        {
            string observed = new NetworkStorageIdentityResolver().Capture(normalizedRoot).StorageIdentity;
            if (!string.Equals(expectedTargetIdentity, observed, StringComparison.Ordinal))
                throw new IOException("The NAS target storage identity changed after publication.");
        };
    }

    private static LeasedPublishedObject OpenObject(
        string fullPath,
        string relativePath,
        string role,
        long expectedLength,
        string expectedObjectIdentity,
        IReadOnlyList<(string FactName, string Sha256)> expectedHashes)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 1024 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            throw new IOException($"Unable to open {role} object '{fullPath}' read-only.", ex);
        }

        var leased = new LeasedPublishedObject(
            fullPath,
            relativePath,
            role,
            expectedLength,
            expectedObjectIdentity,
            expectedHashes,
            stream);

        try
        {
            leased.ValidateContinuity();
            return leased;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string NormalizeSourceRoot(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            throw new InvalidDataException("A frozen source root is required.");

        try
        {
            if (!Path.IsPathFullyQualified(sourceRoot))
                throw new InvalidDataException("The frozen source root must be an absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The frozen source root is invalid.", ex);
        }
    }

    private static async ValueTask DisposeAllAsync(IReadOnlyList<LeasedPublishedObject> objects)
    {
        for (int index = objects.Count - 1; index >= 0; index--)
            await objects[index].DisposeAsync().ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
public sealed class FinalPublishedObjectLease : IAsyncDisposable
{
    private readonly IReadOnlyList<LeasedPublishedObject> _objects;
    private readonly IReadOnlyList<Action> _targetRootChecks;
    private bool _disposed;

    internal FinalPublishedObjectLease(
        IReadOnlyList<LeasedPublishedObject> objects,
        IReadOnlyList<Action> targetRootChecks)
    {
        _objects = objects;
        _targetRootChecks = targetRootChecks;
    }

    public async Task RevalidateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (Action targetRootCheck in _targetRootChecks)
            targetRootCheck();

        foreach (LeasedPublishedObject publishedObject in _objects)
        {
            publishedObject.ValidateContinuity();
            await publishedObject.ValidateHashAsync(cancellationToken).ConfigureAwait(false);
            publishedObject.ValidateContinuity();
        }

        // Every source and target handle remains open while all hashes are read.
        // Recheck every path/object binding before the caller commits or releases the lease.
        foreach (LeasedPublishedObject publishedObject in _objects)
            publishedObject.ValidateContinuity();

        foreach (Action targetRootCheck in _targetRootChecks)
            targetRootCheck();
    }

    /// <summary>Checks bindings while the verified read-only handles still prevent writes.</summary>
    public void ValidateContinuity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (Action targetRootCheck in _targetRootChecks)
            targetRootCheck();
        foreach (LeasedPublishedObject publishedObject in _objects)
            publishedObject.ValidateContinuity();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        for (int index = _objects.Count - 1; index >= 0; index--)
            await _objects[index].DisposeAsync().ConfigureAwait(false);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class LeasedPublishedObject : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly long _expectedLength;
    private readonly string _expectedObjectIdentity;
    private readonly IReadOnlyList<(string FactName, string Sha256)> _expectedHashes;
    private readonly string _relativePath;
    private readonly string _role;

    public LeasedPublishedObject(
        string fullPath,
        string relativePath,
        string role,
        long expectedLength,
        string expectedObjectIdentity,
        IReadOnlyList<(string FactName, string Sha256)> expectedHashes,
        FileStream stream)
    {
        FullPath = fullPath;
        _relativePath = relativePath;
        _role = role;
        _expectedLength = expectedLength;
        _expectedObjectIdentity = expectedObjectIdentity;
        _expectedHashes = expectedHashes;
        _stream = stream;
    }

    public string FullPath { get; }

    public void ValidateContinuity()
    {
        EnsureNoReparsePoints(FullPath);
        FileIdentity.EnsurePathStillNamesOpenedObject(_stream.SafeFileHandle, FullPath);

        string actualIdentity = SourceHandleContinuityGuard.FormatFileId(
            FileIdentity.GetFileIdentity(_stream.SafeFileHandle, FullPath));
        if (!string.Equals(_expectedObjectIdentity, actualIdentity, StringComparison.Ordinal))
        {
            throw new IOException(
                $"{_role} object identity mismatch for '{_relativePath}': " +
                $"expected '{_expectedObjectIdentity}', got '{actualIdentity}'.");
        }

        if (_stream.Length != _expectedLength)
        {
            throw new IOException(
                $"{_role} object length mismatch for '{_relativePath}': " +
                $"expected {_expectedLength}, got {_stream.Length}.");
        }
    }

    public async Task ValidateHashAsync(CancellationToken cancellationToken)
    {
        _stream.Position = 0;
        using var sha256 = SHA256.Create();
        byte[] hash = await sha256.ComputeHashAsync(_stream, cancellationToken).ConfigureAwait(false);
        _stream.Position = 0;
        string actualSha256 = Convert.ToHexString(hash);

        foreach ((string factName, string expectedSha256) in _expectedHashes)
        {
            if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{_role} object SHA-256 does not match the {factName} fact for '{_relativePath}'.");
            }
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();

    private static void EnsureNoReparsePoints(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"Published object path has no root: '{fullPath}'.");
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = fullPath;

        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                throw new IOException($"Unable to inspect path component '{current}'.", ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Published object path contains a reparse point: '{current}'.");

            string normalizedCurrent = current.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedCurrent, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                break;

            string? parent = Path.GetDirectoryName(normalizedCurrent);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }
}
