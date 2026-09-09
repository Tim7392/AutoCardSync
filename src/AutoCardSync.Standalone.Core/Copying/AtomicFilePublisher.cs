using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.Copying;

public readonly record struct FileVerificationProgress(long BytesVerified, long TotalBytes);

/// <summary>
/// Owns the verified final-object continuity lease returned by an atomic publish or verified reuse.
/// The caller must keep the result alive through completion-receipt persistence and then dispose it.
/// </summary>
public sealed class PublishResult : IDisposable, IAsyncDisposable
{
    private VerifiedFileContinuityLease? _continuityLease;

    public bool Success { get; init; }
    public string? FinalPath { get; init; }
    public string? FileId { get; init; }
    public long FileSize { get; init; }
    public string? Hash { get; init; }
    public bool ReusedExisting { get; init; }
    public string? Error { get; init; }

    internal static PublishResult Verified(
        VerifiedFileContinuityLease lease,
        bool reusedExisting = false) => new()
    {
        Success = true,
        FinalPath = lease.FinalPath,
        FileId = lease.FileId,
        FileSize = lease.FileSize,
        Hash = lease.Hash,
        ReusedExisting = reusedExisting,
        _continuityLease = lease,
    };

    /// <summary>
    /// Rereads the leased final object and verifies its identity, size, and SHA-256 while the lease is held.
    /// </summary>
    public Task EnsureContinuousAsync(
        CancellationToken cancellationToken,
        IProgress<FileVerificationProgress>? progress = null)
    {
        if (!Success || _continuityLease is null)
            throw new InvalidOperationException("Only a verified publish result has a continuity lease.");
        return _continuityLease.EnsureContinuousAsync(cancellationToken, progress);
    }

    /// <summary>
    /// Verifies that the currently opened final-object handle still names the published object.
    /// </summary>
    public void EnsureHandleContinuous()
    {
        if (!Success || _continuityLease is null)
            throw new InvalidOperationException("Only a verified publish result has a continuity lease.");
        _continuityLease.EnsureHandleContinuous();
    }

    public void Dispose()
    {
        _continuityLease?.Dispose();
        _continuityLease = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_continuityLease is null)
            return;
        await _continuityLease.DisposeAsync();
        _continuityLease = null;
    }
}

/// <summary>
/// Reports that an identity-bound publish cannot safely claim the requested final path.
/// </summary>
public class ConflictException : IOException
{
    public string ExistingPath { get; }
    public string Reason { get; }
    public long? ExpectedSize { get; }
    public string? ExpectedHash { get; }
    public int? NativeErrorCode { get; }

    /// <summary>Creates a structured final-path conflict without overwriting the existing object.</summary>
    public ConflictException(
        string existingPath,
        string reason,
        long? expectedSize = null,
        string? expectedHash = null,
        int? nativeErrorCode = null,
        Exception? innerException = null)
        : base($"Conflict at '{existingPath}': {reason}", innerException)
    {
        ExistingPath = existingPath;
        Reason = reason;
        ExpectedSize = expectedSize;
        ExpectedHash = expectedHash;
        NativeErrorCode = nativeErrorCode;
    }
}

/// <summary>
/// Verifies a caller-owned temporary file, atomically renames it to the final path, and returns
/// a continuity lease. Existing final files are reused only after full size and SHA-256 verification.
/// </summary>
public class AtomicFilePublisher
{
    private const int MaximumHashMismatchRetries = 2;

    /// <summary>
    /// Publishes one temporary object without overwriting conflicting final content.
    /// </summary>
    /// <remarks>
    /// The temporary object is opened by handle, checked against the expected identity, size, and SHA-256,
    /// atomically renamed, reread from the final path, and kept leased until the returned result is disposed.
    /// Target-continuity callbacks must fail closed when the selected storage identity changes.
    /// </remarks>
    public async Task<PublishResult> PublishAsync(
        string tempPath,
        string finalPath,
        string expectedHash,
        long expectedSize,
        CancellationToken ct,
        Action? targetContinuityCheck = null,
        Action<SafeFileHandle, string>? openedTargetHandleCheck = null,
        FileIdentity? expectedTempIdentity = null,
        bool allowVerifiedExisting = true,
        string? expectedTempObjectIdentity = null,
        IProgress<FileVerificationProgress>? temporaryVerificationProgress = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Identity-bound publish requires Windows file identity APIs.");

        targetContinuityCheck?.Invoke();
        string normalizedTempPath = Path.GetFullPath(tempPath);
        string normalizedFinalPath = Path.GetFullPath(finalPath);
        if (string.Equals(
            normalizedTempPath,
            normalizedFinalPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Temporary and final paths must be different.", nameof(tempPath));
        }

        SafeFileHandle tempHandle = OpenTemporaryForPublish(normalizedTempPath);
        if (tempHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            tempHandle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return new PublishResult { Success = false, Error = $"Temp file not found: {normalizedTempPath}" };
            throw new IOException(
                $"Unable to open temporary file for identity-bound publish '{normalizedTempPath}' (Win32 {error}).",
                new Win32Exception(error));
        }

        FileStream? tempStream = null;
        try
        {
            tempStream = new FileStream(
                tempHandle, FileAccess.Read, bufferSize: 81920, isAsync: false);
            openedTargetHandleCheck?.Invoke(tempHandle, normalizedTempPath);
            FileIdentity verifiedTempIdentity = FileIdentity.GetFileIdentity(
                tempHandle, normalizedTempPath);
            if (expectedTempIdentity is FileIdentity expectedIdentity &&
                verifiedTempIdentity != expectedIdentity)
            {
                throw new IdentityChangedException(
                    "temporary-verify",
                    normalizedTempPath,
                    SourceHandleContinuityGuard.FormatFileId(expectedIdentity),
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    expectedSize,
                    tempStream.Length);
            }
            if (!string.IsNullOrWhiteSpace(expectedTempObjectIdentity) &&
                !string.Equals(
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    expectedTempObjectIdentity,
                    StringComparison.Ordinal))
            {
                throw new IdentityChangedException(
                    "frozen-recovery-temporary-verify",
                    normalizedTempPath,
                    expectedTempObjectIdentity,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    expectedSize,
                    tempStream.Length);
            }

            long tempSize = tempStream.Length;
            if (tempSize != expectedSize)
            {
                return new PublishResult
                {
                    Success = false,
                    Error = $"Size mismatch: expected {expectedSize}, got {tempSize}",
                };
            }

            string tempHash = string.Empty;
            int maximumVerificationAttempts = MaximumHashMismatchRetries + 1;
            for (int verificationAttempt = 1;
                 verificationAttempt <= maximumVerificationAttempts;
                 verificationAttempt++)
            {
                tempHash = await ComputeSha256Async(
                    tempStream, expectedSize, temporaryVerificationProgress, ct);
                if (string.Equals(tempHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    break;

                if (verificationAttempt == maximumVerificationAttempts)
                {
                    return new PublishResult
                    {
                        Success = false,
                        Error = $"Hash mismatch after {verificationAttempt} verification attempts: " +
                                $"expected {expectedHash}, got {tempHash}",
                    };
                }

                // A retry rereads the same exclusively opened temporary object. Before each reread,
                // fail closed if the target binding, file identity, or length has changed.
                targetContinuityCheck?.Invoke();
                openedTargetHandleCheck?.Invoke(tempHandle, normalizedTempPath);
                FileIdentity retryIdentity = FileIdentity.GetFileIdentity(
                    tempHandle, normalizedTempPath);
                if (retryIdentity != verifiedTempIdentity)
                {
                    throw new IdentityChangedException(
                        "temporary-hash-retry",
                        normalizedTempPath,
                        SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                        SourceHandleContinuityGuard.FormatFileId(retryIdentity),
                        expectedSize,
                        tempStream.Length);
                }
                if (tempStream.Length != expectedSize)
                {
                    return new PublishResult
                    {
                        Success = false,
                        Error = $"Size changed before hash retry: expected {expectedSize}, got {tempStream.Length}",
                    };
                }
            }

            openedTargetHandleCheck?.Invoke(tempHandle, normalizedTempPath);
            FileIdentity currentTempIdentity = FileIdentity.GetFileIdentity(
                tempHandle, normalizedTempPath);
            if (currentTempIdentity != verifiedTempIdentity)
            {
                throw new IdentityChangedException(
                    "temporary-content-verify",
                    normalizedTempPath,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    SourceHandleContinuityGuard.FormatFileId(currentTempIdentity),
                    expectedSize,
                    tempStream.Length);
            }

            targetContinuityCheck?.Invoke();
            string? targetDir = Path.GetDirectoryName(normalizedFinalPath);
            if (targetDir != null && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);
            targetContinuityCheck?.Invoke();

            if (File.Exists(normalizedFinalPath))
            {
                if (!allowVerifiedExisting)
                {
                    throw new ConflictException(
                        normalizedFinalPath,
                        "Final path must be a newly created object and cannot reuse an existing file",
                        expectedSize,
                        expectedHash);
                }

                PublishResult existing = await VerifyExistingAsync(
                    normalizedFinalPath, expectedHash, expectedSize, ct, targetContinuityCheck,
                    openedTargetHandleCheck);
                if (existing.Success)
                {
                    try
                    {
                        DeleteOpenedFile(tempHandle, normalizedTempPath);
                        await tempStream.DisposeAsync();
                        tempStream = null;
                        targetContinuityCheck?.Invoke();
                        return existing;
                    }
                    catch
                    {
                        await existing.DisposeAsync();
                        throw;
                    }
                }

                await existing.DisposeAsync();
                throw new ConflictException(
                    normalizedFinalPath,
                    "File already exists with different content",
                    expectedSize,
                    expectedHash);
            }

            targetContinuityCheck?.Invoke();
            try
            {
                RenameOpenedFile(
                    tempHandle,
                    normalizedTempPath,
                    normalizedFinalPath,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    expectedSize,
                    expectedHash);
            }
            catch (IOException exception) when (File.Exists(normalizedFinalPath))
            {
                throw new ConflictException(
                    normalizedFinalPath,
                    "File was created by another process before handle-bound publish",
                    expectedSize,
                    expectedHash,
                    (exception as PublishOperationException)?.NativeErrorCode,
                    exception);
            }

            openedTargetHandleCheck?.Invoke(tempHandle, normalizedFinalPath);
            FileIdentity publishedIdentity = FileIdentity.GetFileIdentity(
                tempHandle, normalizedFinalPath);
            if (publishedIdentity != verifiedTempIdentity)
            {
                throw new IdentityChangedException(
                    "publish-rename",
                    normalizedFinalPath,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    SourceHandleContinuityGuard.FormatFileId(publishedIdentity),
                    expectedSize,
                    tempStream.Length);
            }
            if (tempStream.Length != expectedSize)
            {
                throw new IdentityChangedException(
                    "publish-size-verify",
                    normalizedFinalPath,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    SourceHandleContinuityGuard.FormatFileId(publishedIdentity),
                    expectedSize,
                    tempStream.Length);
            }

            await using var transitionStream = new FileStream(normalizedFinalPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read | FileShare.Delete,
                BufferSize = 1,
                Options = FileOptions.None,
            });
            openedTargetHandleCheck?.Invoke(
                transitionStream.SafeFileHandle, normalizedFinalPath);
            FileIdentity transitionIdentity = FileIdentity.GetFileIdentity(
                transitionStream.SafeFileHandle, normalizedFinalPath);
            if (transitionIdentity != verifiedTempIdentity)
            {
                throw new IdentityChangedException(
                    "publish-path-transition",
                    normalizedFinalPath,
                    SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                    SourceHandleContinuityGuard.FormatFileId(transitionIdentity),
                    expectedSize,
                    transitionStream.Length);
            }

            await tempStream.DisposeAsync();
            tempStream = null;
            var leaseStream = new FileStream(normalizedFinalPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            try
            {
                openedTargetHandleCheck?.Invoke(leaseStream.SafeFileHandle, normalizedFinalPath);
                FileIdentity leaseIdentity = FileIdentity.GetFileIdentity(
                    leaseStream.SafeFileHandle, normalizedFinalPath);
                if (leaseIdentity != verifiedTempIdentity || leaseStream.Length != expectedSize)
                {
                    throw new IdentityChangedException(
                        "publish-lease-transfer",
                        normalizedFinalPath,
                        SourceHandleContinuityGuard.FormatFileId(verifiedTempIdentity),
                        SourceHandleContinuityGuard.FormatFileId(leaseIdentity),
                        expectedSize,
                        leaseStream.Length);
                }

                targetContinuityCheck?.Invoke();
                string fileId = SourceHandleContinuityGuard.FormatFileId(leaseIdentity);
                var lease = new VerifiedFileContinuityLease(
                    normalizedFinalPath, leaseStream, fileId, expectedSize, tempHash,
                    targetContinuityCheck, openedTargetHandleCheck);
                leaseStream = null!;
                return PublishResult.Verified(lease);
            }
            finally
            {
                if (leaseStream is not null)
                    await leaseStream.DisposeAsync();
            }
        }
        finally
        {
            if (tempStream is not null)
                await tempStream.DisposeAsync();
            else
                tempHandle.Dispose();
        }
    }

    /// <summary>
    /// Fully verifies an existing final object and returns a continuity lease when it matches.
    /// This method never treats mere path existence as successful publication.
    /// </summary>
    public async Task<PublishResult> VerifyExistingAsync(
        string finalPath,
        string expectedHash,
        long expectedSize,
        CancellationToken ct,
        Action? targetContinuityCheck = null,
        Action<SafeFileHandle, string>? openedTargetHandleCheck = null,
        IProgress<FileVerificationProgress>? verificationProgress = null)
    {
        targetContinuityCheck?.Invoke();
        string normalizedFinalPath = Path.GetFullPath(finalPath);
        if (!File.Exists(normalizedFinalPath))
        {
            return new PublishResult
            {
                Success = false,
                Error = $"Final file not found: {normalizedFinalPath}",
            };
        }

        return await VerifyFinalAsync(
            normalizedFinalPath, expectedHash, expectedSize, ct, targetContinuityCheck,
            openedTargetHandleCheck, verificationProgress, reusedExisting: true);
    }

    private static SafeFileHandle OpenTemporaryForPublish(string path) =>
        CreateFileW(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagSequentialScan,
            IntPtr.Zero);

    private static void DeleteOpenedFile(SafeFileHandle handle, string displayPath)
    {
        var disposition = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle, FileDispositionInfoClass, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Unable to mark verified temporary file for deletion '{displayPath}' (Win32 {error}).",
                new Win32Exception(error));
        }
    }

    private static void RenameOpenedFile(
        SafeFileHandle handle,
        string sourcePath,
        string finalPath,
        string expectedIdentity,
        long expectedSize,
        string expectedHash)
    {
        string nativePath = ToNativeRenamePath(finalPath);
        byte[] fileNameBytes = System.Text.Encoding.Unicode.GetBytes(nativePath);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = IntPtr.Size == 8 ? 16 : 8;
        int nameOffset = IntPtr.Size == 8 ? 20 : 12;
        int bufferSize = nameOffset + fileNameBytes.Length + sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int index = 0; index < bufferSize; index++)
                Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, fileNameBytes.Length);
            Marshal.Copy(fileNameBytes, 0, IntPtr.Add(buffer, nameOffset), fileNameBytes.Length);
            if (!SetFileInformationByHandle(
                    handle, FileRenameInfoClass, buffer,
                    (uint)bufferSize))
            {
                int error = Marshal.GetLastWin32Error();
                throw new PublishOperationException(
                    "handle-bound-rename",
                    sourcePath,
                    finalPath,
                    expectedIdentity,
                    expectedSize,
                    expectedHash,
                    error,
                    new Win32Exception(error));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ToNativeRenamePath(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Deletes only the temporary object whose opened identity still matches <paramref name="expectedIdentity"/>.
    /// A missing object is accepted; an identity mismatch fails closed and is never deleted.
    /// </summary>
    public Task RollbackAsync(
        string tempPath,
        FileIdentity expectedIdentity,
        Action<SafeFileHandle, string>? openedTargetHandleCheck = null,
        Action? targetContinuityCheck = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Safe temporary-file cleanup requires Windows handle APIs.");

        string normalizedTempPath = Path.GetFullPath(tempPath);
        targetContinuityCheck?.Invoke();
        SafeFileHandle handle = CreateFileW(
            normalizedTempPath,
            DeleteAccess | FileReadAttributes,
            FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return Task.CompletedTask;
            throw new IOException(
                $"Unable to open temporary file for identity-bound cleanup '{normalizedTempPath}' (Win32 {error}).",
                new Win32Exception(error));
        }

        using (handle)
        {
            openedTargetHandleCheck?.Invoke(handle, normalizedTempPath);
            FileIdentity actualIdentity = FileIdentity.GetFileIdentity(handle, normalizedTempPath);
            if (actualIdentity != expectedIdentity)
            {
                throw new IOException(
                    $"Temporary file identity changed; cleanup refused for '{normalizedTempPath}'.");
            }

            targetContinuityCheck?.Invoke();
            DeleteOpenedFile(handle, normalizedTempPath);
        }

        targetContinuityCheck?.Invoke();
        return Task.CompletedTask;
    }

    private static async Task<PublishResult> VerifyFinalAsync(
        string finalPath,
        string expectedHash,
        long expectedSize,
        CancellationToken ct,
        Action? targetContinuityCheck,
        Action<SafeFileHandle, string>? openedTargetHandleCheck,
        IProgress<FileVerificationProgress>? verificationProgress,
        bool reusedExisting)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Final file identity requires Windows file identity APIs.");

        var stream = new FileStream(finalPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        try
        {
            openedTargetHandleCheck?.Invoke(stream.SafeFileHandle, finalPath);
            long fileSize = stream.Length;
            if (fileSize != expectedSize)
            {
                await stream.DisposeAsync();
                return new PublishResult
                {
                    Success = false,
                    Error = $"Final size mismatch: expected {expectedSize}, got {fileSize}",
                };
            }

            string fileId = SourceHandleContinuityGuard.FormatFileId(
                FileIdentity.GetFileIdentity(stream.SafeFileHandle, finalPath));
            string hash = await ComputeSha256Async(
                stream, expectedSize, verificationProgress, ct);
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                await stream.DisposeAsync();
                return new PublishResult
                {
                    Success = false,
                    Error = $"Post-publish verification failed: expected {expectedHash}, got {hash}",
                };
            }

            targetContinuityCheck?.Invoke();
            string currentFileId = SourceHandleContinuityGuard.FormatFileId(
                FileIdentity.GetFileIdentity(stream.SafeFileHandle, finalPath));
            if (!string.Equals(fileId, currentFileId, StringComparison.Ordinal))
            {
                await stream.DisposeAsync();
                return new PublishResult
                {
                    Success = false,
                    Error = $"Final file identity changed after verification: expected {fileId}, got {currentFileId}",
                };
            }

            var lease = new VerifiedFileContinuityLease(
                finalPath, stream, fileId, fileSize, hash, targetContinuityCheck,
                openedTargetHandleCheck);
            return PublishResult.Verified(lease, reusedExisting);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    internal static async Task<string> ComputeSha256Async(
        FileStream stream,
        long expectedBytes,
        IProgress<FileVerificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int BufferSize = 1024 * 1024;
        const long ByteReportInterval = 16L * 1024 * 1024;
        long timeReportInterval = Math.Max(1, Stopwatch.Frequency / 4);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesVerified = 0;
            long lastReportedBytes = 0;
            long lastReportedAt = Stopwatch.GetTimestamp();
            ReportVerificationProgress(progress, 0, expectedBytes);

            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0)
                    break;

                hash.AppendData(buffer, 0, read);
                bytesVerified = checked(bytesVerified + read);
                long now = Stopwatch.GetTimestamp();
                if (bytesVerified == expectedBytes ||
                    bytesVerified - lastReportedBytes >= ByteReportInterval ||
                    now - lastReportedAt >= timeReportInterval)
                {
                    ReportVerificationProgress(progress, bytesVerified, expectedBytes);
                    lastReportedBytes = bytesVerified;
                    lastReportedAt = now;
                }
            }

            if (bytesVerified != expectedBytes)
            {
                throw new IOException(
                    $"Verification length changed: expected {expectedBytes}, read {bytesVerified}.");
            }
            if (lastReportedBytes != bytesVerified)
                ReportVerificationProgress(progress, bytesVerified, expectedBytes);

            stream.Position = 0;
            return Convert.ToHexString(hash.GetHashAndReset()).ToUpperInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReportVerificationProgress(
        IProgress<FileVerificationProgress>? progress,
        long bytesVerified,
        long totalBytes)
    {
        try
        {
            progress?.Report(new FileVerificationProgress(bytesVerified, totalBytes));
        }
        catch
        {
            // Progress is observational and must never alter verification correctness.
        }
    }
}

internal sealed class VerifiedFileContinuityLease : IDisposable, IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly Action? _targetContinuityCheck;
    private readonly Action<SafeFileHandle, string>? _openedTargetHandleCheck;
    private bool _disposed;

    public VerifiedFileContinuityLease(
        string finalPath,
        FileStream stream,
        string fileId,
        long fileSize,
        string hash,
        Action? targetContinuityCheck,
        Action<SafeFileHandle, string>? openedTargetHandleCheck)
    {
        FinalPath = finalPath;
        _stream = stream;
        FileId = fileId;
        FileSize = fileSize;
        Hash = hash;
        _targetContinuityCheck = targetContinuityCheck;
        _openedTargetHandleCheck = openedTargetHandleCheck;
    }

    public string FinalPath { get; }
    public string FileId { get; }
    public long FileSize { get; }
    public string Hash { get; }

    public async Task EnsureContinuousAsync(
        CancellationToken cancellationToken,
        IProgress<FileVerificationProgress>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Final file identity requires Windows file identity APIs.");
        EnsureHandleContinuous();

        _stream.Position = 0;
        string currentHash = await AtomicFilePublisher.ComputeSha256Async(
            _stream, FileSize, progress, cancellationToken);
        if (!string.Equals(Hash, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                $"Final file content changed after verification: expected {Hash}, got {currentHash}.");
        }

        EnsureHandleContinuous();
    }

    public void EnsureHandleContinuous()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Final file identity requires Windows file identity APIs.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        _targetContinuityCheck?.Invoke();
        _openedTargetHandleCheck?.Invoke(_stream.SafeFileHandle, FinalPath);

        string currentFileId = SourceHandleContinuityGuard.FormatFileId(
            FileIdentity.GetFileIdentity(_stream.SafeFileHandle, FinalPath));
        if (!string.Equals(FileId, currentFileId, StringComparison.Ordinal))
        {
            throw new IOException(
                $"Final file identity changed after verification: expected {FileId}, got {currentFileId}.");
        }
        if (_stream.Length != FileSize)
        {
            throw new IOException(
                $"Final file size changed after verification: expected {FileSize}, got {_stream.Length}.");
        }

        _targetContinuityCheck?.Invoke();
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
