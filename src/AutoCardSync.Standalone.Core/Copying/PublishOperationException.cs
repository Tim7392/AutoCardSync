using System.IO;

namespace AutoCardSync.Infrastructure.Copying;

/// <summary>
/// Reports a failed identity-bound publish operation with structured source,
/// destination, expected-object, and native error context.
/// </summary>
public sealed class PublishOperationException : IOException
{
    /// <summary>Creates a structured publish-operation failure.</summary>
    public PublishOperationException(
        string operation,
        string sourcePath,
        string destinationPath,
        string? expectedIdentity,
        long? expectedSize,
        string? expectedHash,
        int? nativeErrorCode,
        Exception? innerException = null)
        : base(BuildMessage(operation, sourcePath, destinationPath, nativeErrorCode), innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        Operation = operation;
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        ExpectedIdentity = expectedIdentity;
        ExpectedSize = expectedSize;
        ExpectedHash = expectedHash;
        NativeErrorCode = nativeErrorCode;
    }

    /// <summary>Gets the stable publish stage.</summary>
    public string Operation { get; }

    /// <summary>Gets the temporary source path.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the intended final path.</summary>
    public string DestinationPath { get; }

    /// <summary>Gets the expected temporary-object identity.</summary>
    public string? ExpectedIdentity { get; }

    /// <summary>Gets the expected object size.</summary>
    public long? ExpectedSize { get; }

    /// <summary>Gets the expected content hash. Callers must redact it before external reporting.</summary>
    public string? ExpectedHash { get; }

    /// <summary>Gets the native platform error code, when available.</summary>
    public int? NativeErrorCode { get; }

    private static string BuildMessage(
        string operation,
        string sourcePath,
        string destinationPath,
        int? nativeErrorCode)
    {
        string native = nativeErrorCode is int error ? $" (Win32 {error})" : string.Empty;
        return $"Identity-bound publish operation '{operation}' failed from '{sourcePath}' to '{destinationPath}'{native}.";
    }
}
