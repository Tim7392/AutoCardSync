using System.Security.Cryptography;

namespace AutoCardSync.Domain.Common;

/// <summary>
/// Canonical hex-encoding and comparison helpers for SHA-256 hashes.
/// AutoCardSync standardizes on <b>lowercase</b> hex throughout the system
/// to avoid case-sensitivity bugs in hash comparisons and audit chain links.
/// </summary>
public static class HashFormatting
{
    /// <summary>
    /// Convert a raw hash byte array to a lowercase hex string.
    /// Equivalent to <c>Convert.ToHexStringLower(hash)</c> on .NET 9+,
    /// but available as an explicit, intention-revealing helper.
    /// </summary>
    public static string ToHexLower(byte[] hash)
    {
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Case-insensitive comparison of two hex-encoded hash strings.
    /// Returns <c>true</c> when both represent the same byte sequence
    /// regardless of casing (e.g., "aB" == "ab").
    /// </summary>
    public static bool CompareHashes(string a, string b)
    {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
