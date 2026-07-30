using AutoCardSync.Infrastructure.FileSystem;

namespace AutoCardSync.Standalone.Core.Configuration;

public sealed record SourceDirectorySelection(
    string FullPath,
    string VolumeRoot,
    string RelativePath);

public static class SourceDirectorySelectionResolver
{
    public static SourceDirectorySelection Resolve(string selectedPath, string volumeRoot)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            throw new InvalidDataException("请选择素材卡内的文件夹。");
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidDataException("无法确定所选文件夹所在的素材卡。");

        string normalizedSelected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(volumeRoot));
        if (!Directory.Exists(normalizedSelected))
            throw new DirectoryNotFoundException("所选素材文件夹不存在或当前不可访问。");

        string relative = Path.GetRelativePath(normalizedRoot, normalizedSelected);
        if (relative == ".")
            throw new InvalidDataException("请选择素材卡内的具体素材文件夹，不要选择整个盘符。");

        string resolved;
        try
        {
            resolved = SafePathResolver.ResolveSafePath(normalizedRoot, relative);
        }
        catch (Exception exception) when (exception is ArgumentException or PathViolationException)
        {
            throw new InvalidDataException("所选文件夹不在同一素材卡内，或包含不安全的路径跳转。", exception);
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(resolved),
                normalizedSelected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("所选素材文件夹的路径身份不一致。");
        }

        return new SourceDirectorySelection(normalizedSelected, normalizedRoot, relative);
    }
}
