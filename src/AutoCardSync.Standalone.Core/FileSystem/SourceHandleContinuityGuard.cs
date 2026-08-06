using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace AutoCardSync.Infrastructure.FileSystem;

public record SourceObjectIdentity
{
    public required string VolumeGuid { get; init; }
    public required string CanonicalFinalPath { get; init; }
    public required string FileIdType { get; init; }
    public required string FileId { get; init; }
    public required long FileSize { get; init; }
    public required DateTimeOffset LastModifiedUtc { get; init; }
    public required string ContentHash { get; init; }
}

public record SourceContinuityResult
{
    public required bool IsContinuous { get; init; }
    public string? MismatchDetail { get; init; }
}

[SupportedOSPlatform("windows")]
public sealed class SourceReadContinuityLease : IDisposable, IAsyncDisposable
{
    private readonly SourceHandleContinuityGuard _guard;
    private readonly FileStream _stream;
    private bool _disposed;

    internal SourceReadContinuityLease(
        SourceHandleContinuityGuard guard,
        FileStream stream,
        SourceObjectIdentity identity)
    {
        _guard = guard;
        _stream = stream;
        Identity = identity;
    }

    public SourceObjectIdentity Identity { get; private set; }

    internal FileStream Stream
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stream;
        }
    }

    internal void BindContentHash(string contentHash)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        Identity = Identity with { ContentHash = contentHash };
    }

    public async Task<SourceContinuityResult> VerifyContinuityAsync(
        SourceObjectIdentity frozen,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string contentHash = await SourceHandleContinuityGuard.ComputeSha256Async(
            _stream, cancellationToken);
        SourceObjectIdentity current = _guard.CaptureMetadataIdentity(
            Identity.CanonicalFinalPath, contentHash);
        return SourceHandleContinuityGuard.Compare(frozen, current);
    }

    public SourceContinuityResult VerifyHandleContinuity(SourceObjectIdentity frozen)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FileIdentity.EnsurePathStillNamesOpenedObject(
            _stream.SafeFileHandle, Identity.CanonicalFinalPath, requireSingleLink: true);
        string currentFileId = SourceHandleContinuityGuard.FormatFileId(
            FileIdentity.GetFileIdentity(_stream.SafeFileHandle, Identity.CanonicalFinalPath));
        var mismatches = new List<string>();
        if (!string.Equals(frozen.FileIdType, "NTFS_FileId", StringComparison.Ordinal) ||
            !string.Equals(frozen.FileId, currentFileId, StringComparison.Ordinal))
        {
            mismatches.Add(
                $"FileId expected '{frozen.FileIdType}:{frozen.FileId}', " +
                $"got 'NTFS_FileId:{currentFileId}'");
        }
        if (_stream.Length != frozen.FileSize)
            mismatches.Add($"FileSize expected {frozen.FileSize}, got {_stream.Length}");
        DateTimeOffset currentModified = File.GetLastWriteTimeUtc(Identity.CanonicalFinalPath);
        if (currentModified.ToUniversalTime() != frozen.LastModifiedUtc.ToUniversalTime())
        {
            mismatches.Add(
                $"LastModifiedUtc expected {frozen.LastModifiedUtc:O}, got {currentModified:O}");
        }

        return new SourceContinuityResult
        {
            IsContinuous = mismatches.Count == 0,
            MismatchDetail = mismatches.Count == 0 ? null : string.Join("; ", mismatches),
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _stream.DisposeAsync();
    }
}

[SupportedOSPlatform("windows")]
public class SourceHandleContinuityGuard
{
    public SourceObjectIdentity CaptureIdentity(string filePath)
    {
        using SourceReadContinuityLease lease = AcquireReadLease(filePath);
        return lease.Identity;
    }

    public SourceReadContinuityLease AcquireReadLease(string filePath)
    {
        SourceReadContinuityLease lease = AcquireMetadataReadLease(filePath);
        try
        {
            lease.BindContentHash(ComputeSha256(lease.Stream));
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public SourceReadContinuityLease AcquireMetadataReadLease(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Source identity continuity requires Windows file identity APIs.");
        }

        string fullPath = Path.GetFullPath(filePath);
        string canonicalFinalPath = JunctionResolver.GetFinalTarget(fullPath);
        var stream = new FileStream(canonicalFinalPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 1024 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        try
        {
            SourceObjectIdentity identity = CaptureMetadataIdentity(canonicalFinalPath, string.Empty);
            FileIdentity.EnsurePathStillNamesOpenedObject(
                stream.SafeFileHandle, canonicalFinalPath, requireSingleLink: true);
            string openedFileId = FormatFileId(
                FileIdentity.GetFileIdentity(stream.SafeFileHandle, canonicalFinalPath));
            if (!string.Equals(identity.FileId, openedFileId, StringComparison.Ordinal))
            {
                throw new IdentityChangedException(
                    "source-open",
                    canonicalFinalPath,
                    identity.FileId,
                    openedFileId,
                    identity.FileSize,
                    stream.Length);
            }
            return new SourceReadContinuityLease(this, stream, identity);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public SourceContinuityResult VerifyContinuity(SourceObjectIdentity frozen, string filePath)
    {
        using SourceReadContinuityLease lease = AcquireReadLease(filePath);
        return Compare(frozen, lease.Identity);
    }

    internal SourceObjectIdentity CaptureMetadataIdentity(string canonicalFinalPath, string contentHash)
    {
        var fileInfo = new FileInfo(canonicalFinalPath);
        string volumeGuid = GetVolumeGuid(canonicalFinalPath);

        // FileIdentity uses FileIdInfo and falls back to the legacy volume/file-index
        // tuple for exFAT and compatible SMB implementations. If neither identity is
        // available, acquisition fails closed.
        FileIdentity identity = FileIdentity.GetFileIdentity(canonicalFinalPath);
        const string fileIdType = "NTFS_FileId";
        string fileId = FormatFileId(identity);

        return new SourceObjectIdentity
        {
            VolumeGuid = volumeGuid,
            CanonicalFinalPath = canonicalFinalPath,
            FileIdType = fileIdType,
            FileId = fileId,
            FileSize = fileInfo.Length,
            LastModifiedUtc = File.GetLastWriteTimeUtc(canonicalFinalPath),
            ContentHash = contentHash,
        };
    }

    internal static SourceContinuityResult Compare(
        SourceObjectIdentity frozen,
        SourceObjectIdentity current)
    {
        var mismatches = new System.Collections.Generic.List<string>();

        if (frozen.VolumeGuid != current.VolumeGuid)
            mismatches.Add($"VolumeGuid: expected '{frozen.VolumeGuid}', got '{current.VolumeGuid}'");

        if (!string.Equals(frozen.CanonicalFinalPath, current.CanonicalFinalPath, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"CanonicalFinalPath: expected '{frozen.CanonicalFinalPath}', got '{current.CanonicalFinalPath}'");

        if (frozen.FileIdType == "NTFS_FileId" && current.FileIdType == "NTFS_FileId")
        {
            if (frozen.FileId != current.FileId)
                mismatches.Add($"FileId (Windows): expected '{frozen.FileId}', got '{current.FileId}' — file was replaced");
        }
        else if (frozen.FileIdType != current.FileIdType)
        {
            mismatches.Add($"FileIdType: expected '{frozen.FileIdType}', got '{current.FileIdType}'");
        }

        if (frozen.FileSize != current.FileSize)
            mismatches.Add($"FileSize: expected {frozen.FileSize}, got {current.FileSize}");

        if (frozen.LastModifiedUtc.ToUniversalTime() != current.LastModifiedUtc.ToUniversalTime())
            mismatches.Add($"LastModifiedUtc: expected {frozen.LastModifiedUtc:O}, got {current.LastModifiedUtc:O}");

        if (!string.Equals(frozen.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase))
            mismatches.Add($"ContentHash: expected '{frozen.ContentHash}', got '{current.ContentHash}'");

        return new SourceContinuityResult
        {
            IsContinuous = mismatches.Count == 0,
            MismatchDetail = mismatches.Count == 0 ? null : string.Join("; ", mismatches),
        };
    }

    internal static async Task<string> ComputeSha256Async(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    private static string ComputeSha256(FileStream stream)
    {
        stream.Position = 0;
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToUpperInvariant();
    }

    internal static string FormatFileId(FileIdentity identity)
        => $"{identity.VolumeSerialNumber}:{identity.FileIndexHigh}:{identity.FileIndexLow}";

    private static string GetVolumeGuid(string filePath)
    {
        string? root = Path.GetPathRoot(filePath);
        if (string.IsNullOrEmpty(root))
            return string.Empty;

        var sb = new System.Text.StringBuilder(256);
        if (NativeMethods.GetVolumeNameForVolumeMountPoint(root, sb, (uint)sb.Capacity))
            return sb.ToString().TrimEnd('\\');

        return root;
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern bool GetVolumeNameForVolumeMountPoint(
            string lpszVolumeMountPoint,
            System.Text.StringBuilder lpszVolumeName,
            uint cchBufferLength);
    }
}
