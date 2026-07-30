namespace AutoCardSync.Standalone.Core.Configuration;

public static class CorruptStateFileRecovery
{
    public static string Preserve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The corrupt state file no longer exists.", fullPath);

        string backupPath = CreateBackupPath(fullPath);
        File.Move(fullPath, backupPath, overwrite: false);
        return backupPath;
    }

    public static string PreserveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"The corrupt state directory no longer exists: '{fullPath}'.");

        string backupPath = CreateBackupPath(fullPath);
        Directory.Move(fullPath, backupPath);
        return backupPath;
    }

    private static string CreateBackupPath(string fullPath)
    {
        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException("The state path must have a parent directory.");
        string backupName = string.Concat(
            Path.GetFileName(fullPath),
            ".corrupt-",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            "-",
            Guid.NewGuid().ToString("N"),
            ".bak");
        return Path.Combine(directory, backupName);
    }
}
