using System.IO;

namespace AutoCardSync.Infrastructure.FileSystem;

/// <summary>
/// Reports that an opened source or target object no longer has the identity or size
/// captured at the safety boundary for the current operation.
/// </summary>
public sealed class IdentityChangedException : IOException
{
    /// <summary>
    /// Creates a structured identity-continuity failure.
    /// </summary>
    /// <param name="operation">Stable operation stage used for diagnostics.</param>
    /// <param name="path">Path associated with the opened object.</param>
    /// <param name="expectedIdentity">Identity captured at the safety boundary.</param>
    /// <param name="actualIdentity">Identity observed when continuity was rechecked.</param>
    /// <param name="expectedSize">Expected object size, when applicable.</param>
    /// <param name="actualSize">Observed object size, when applicable.</param>
    /// <param name="detail">Additional mismatch detail that does not fit the structured fields.</param>
    /// <param name="innerException">Underlying platform or I/O failure, when present.</param>
    public IdentityChangedException(
        string operation,
        string path,
        string? expectedIdentity,
        string? actualIdentity,
        long? expectedSize = null,
        long? actualSize = null,
        string? detail = null,
        Exception? innerException = null)
        : base(BuildMessage(operation, path, expectedIdentity, actualIdentity, expectedSize, actualSize, detail),
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Operation = operation;
        Path = path;
        ExpectedIdentity = expectedIdentity;
        ActualIdentity = actualIdentity;
        ExpectedSize = expectedSize;
        ActualSize = actualSize;
        Detail = detail;
    }

    /// <summary>Gets the stable operation stage at which continuity failed.</summary>
    public string Operation { get; }

    /// <summary>Gets the path associated with the opened object.</summary>
    public string Path { get; }

    /// <summary>Gets the identity captured at the safety boundary.</summary>
    public string? ExpectedIdentity { get; }

    /// <summary>Gets the identity observed during the failed recheck.</summary>
    public string? ActualIdentity { get; }

    /// <summary>Gets the expected object size, when applicable.</summary>
    public long? ExpectedSize { get; }

    /// <summary>Gets the observed object size, when applicable.</summary>
    public long? ActualSize { get; }

    /// <summary>Gets additional mismatch detail.</summary>
    public string? Detail { get; }

    private static string BuildMessage(
        string operation,
        string path,
        string? expectedIdentity,
        string? actualIdentity,
        long? expectedSize,
        long? actualSize,
        string? detail)
    {
        string identity = expectedIdentity is null && actualIdentity is null
            ? string.Empty
            : $" Expected identity '{expectedIdentity ?? "<unknown>"}', observed '{actualIdentity ?? "<unknown>"}'.";
        string size = expectedSize is null && actualSize is null
            ? string.Empty
            : $" Expected size {expectedSize?.ToString() ?? "<unknown>"}, observed {actualSize?.ToString() ?? "<unknown>"}.";
        string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        return $"Object identity changed during '{operation}' for '{path}'.{identity}{size}{suffix}";
    }
}
