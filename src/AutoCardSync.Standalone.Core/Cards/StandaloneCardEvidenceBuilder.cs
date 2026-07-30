using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AutoCardSync.Application.Cards;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Infrastructure.FileSystem;
using AutoCardSync.Infrastructure.Storage;

namespace AutoCardSync.Standalone.Core.Cards;

public static partial class StandaloneCardEvidenceBuilder
{
    public static CardIdentityEvidence Build(
        string sourceRoot,
        FaultDomainInfo sourceDomain,
        TaskManifest manifest,
        string? observedFileSystem = null,
        long? observedCapacity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentNullException.ThrowIfNull(sourceDomain);
        ArgumentNullException.ThrowIfNull(manifest);
        string root = Path.GetPathRoot(Path.GetFullPath(sourceRoot)) ??
            throw new InvalidDataException("The source root does not have a volume root.");
        var drive = new DriveInfo(root);
        Guid? volumeGuid = ParseVolumeGuid(sourceDomain.VolumeGuid);
        uint? serial = sourceDomain.VolumeSerialNumber is > 0 and <= uint.MaxValue
            ? (uint)sourceDomain.VolumeSerialNumber.Value
            : null;

        string canonical = string.Join("\n", manifest.Entries
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => string.Join("|",
                entry.RelativePath.ToUpperInvariant(),
                entry.FileSize,
                entry.LastModifiedUtc.UtcTicks,
                entry.SourceFileIdType,
                entry.SourceFileId,
                entry.Excluded,
                entry.ExclusionRule)));
        string rootDirectoryHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

        return new CardIdentityEvidence
        {
            VolumeGuid = volumeGuid,
            VolumeSerialNumber = serial,
            FileSystem = !string.IsNullOrWhiteSpace(observedFileSystem)
                ? observedFileSystem.Trim()
                : drive.IsReady ? drive.DriveFormat : null,
            Capacity = observedCapacity is >= 0
                ? observedCapacity
                : drive.IsReady ? drive.TotalSize : null,
            RootDirectoryHash = rootDirectoryHash,
            SampleFingerprint = ComputeSampleFingerprint(sourceRoot, manifest),
        };
    }

    public static IReadOnlyList<string> GetSampledRelativePaths(TaskManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SelectSampleEntries(manifest)
            .Select(entry => entry.RelativePath)
            .ToArray();
    }

    private static ManifestEntry[] SelectSampleEntries(TaskManifest manifest)
    {
        ManifestEntry[] eligible = manifest.Entries
            .Where(entry => !entry.Excluded)
            .OrderBy(entry => entry.LastModifiedUtc)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eligible.Length == 0)
            return [];
        return new[] { 0, eligible.Length / 2, eligible.Length - 1 }
            .Distinct()
            .Select(index => eligible[index])
            .ToArray();
    }

    private static string? ComputeSampleFingerprint(string sourceRoot, TaskManifest manifest)
    {
        ManifestEntry[] anchors = SelectSampleEntries(manifest);
        if (anchors.Length == 0)
            return null;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        foreach (ManifestEntry entry in anchors)
        {
            string path = SafePathResolver.ResolveSafePath(sourceRoot, entry.RelativePath);
            var before = new FileInfo(path);
            if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.LastModifiedUtc.UtcDateTime)
                throw new IOException($"素材卡样本在身份检查前发生变化：{entry.RelativePath}");

            hash.AppendData(Encoding.UTF8.GetBytes(string.Join("|",
                entry.RelativePath.ToUpperInvariant(), entry.FileSize, entry.LastModifiedUtc.UtcTicks)));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, buffer.Length, FileOptions.SequentialScan);
            int head = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, stream.Length));
            hash.AppendData(buffer, 0, head);
            if (stream.Length > buffer.Length * 2L)
            {
                stream.Position = Math.Max(0, (stream.Length / 2) - (buffer.Length / 2));
                int middle = stream.Read(buffer, 0, buffer.Length);
                hash.AppendData(buffer, 0, middle);
            }
            if (stream.Length > buffer.Length)
            {
                stream.Position = Math.Max(0, stream.Length - buffer.Length);
                int tail = stream.Read(buffer, 0, buffer.Length);
                hash.AppendData(buffer, 0, tail);
            }

            before.Refresh();
            if (!before.Exists || before.Length != entry.FileSize || before.LastWriteTimeUtc != entry.LastModifiedUtc.UtcDateTime)
                throw new IOException($"素材卡样本在身份检查期间发生变化：{entry.RelativePath}");
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Guid? ParseVolumeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        Match match = VolumeGuidPattern().Match(value);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out Guid parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"Volume\{([0-9A-Fa-f-]{36})\}", RegexOptions.CultureInvariant)]
    private static partial Regex VolumeGuidPattern();
}
