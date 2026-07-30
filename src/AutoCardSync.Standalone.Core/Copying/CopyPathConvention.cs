using System.Security.Cryptography;
using System.Text;
using AutoCardSync.Infrastructure.FileSystem;

namespace AutoCardSync.Application.Copying;

public static class CopyPathConvention
{
    public static string GetFinalPath(string targetRoot, string relativePath)
        => SafePathResolver.ResolveSafePath(targetRoot, relativePath);

    public static string GetTempPath(
        string targetRoot,
        string relativePath,
        Guid taskId,
        Guid fileId,
        Guid targetId)
    {
        byte[] ownershipHash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{taskId:N}:{fileId:N}:{targetId:N}"));
        string ownershipToken = Convert.ToHexStringLower(ownershipHash.AsSpan(0, 16));
        return SafePathResolver.ResolveSafePath(
            targetRoot, $"{relativePath}.partial.{ownershipToken}");
    }
}
