using System.Security.Cryptography;

namespace AutoCardSync.Infrastructure.Hashing;

public sealed class Sha256Verifier
{
    private const int BufferSize = 81920;

    public async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);
        return await ComputeStreamHashAsync(stream, ct);
    }

    public async Task<string> ComputeStreamHashAsync(Stream stream, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        var buffer = new byte[BufferSize];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha256.Hash!);
    }

    public async Task<string> ComputeBlockHashAsync(Stream stream, long offset, int length, CancellationToken ct)
    {
        if (stream.CanSeek)
            stream.Seek(offset, SeekOrigin.Begin);
        else if (offset > 0)
        {
            var skip = new byte[Math.Min(offset, BufferSize)];
            long remaining = offset;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, skip.Length);
                int read = await stream.ReadAsync(skip.AsMemory(0, toRead), ct);
                if (read == 0)
                    break;
                remaining -= read;
            }
        }

        using var sha256 = SHA256.Create();
        var buffer = new byte[BufferSize];
        int totalRead = 0;
        while (totalRead < length)
        {
            int toRead = Math.Min(BufferSize, length - totalRead);
            int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (bytesRead == 0)
                break;
            sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            totalRead += bytesRead;
        }
        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha256.Hash!);
    }

    public bool CompareHashes(string expected, string actual)
    {
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}
