using System.Text.Json;

namespace AutoCardSync.Standalone.Core.Configuration;

public sealed class AtomicJsonFileStore<T>
{
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan[] CommitRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public AtomicJsonFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public async Task<T?> LoadAsync(CancellationToken cancellationToken)
    {
        TryDeleteStaleTemporaryFiles();
        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }

        await using (stream)
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The JSON store path must have a parent directory.");
        Directory.CreateDirectory(directory);
        TryDeleteStaleTemporaryFiles();

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        bool temporaryCreated = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await CommitTemporaryFileAsync(temporaryPath, cancellationToken);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
                TryDeleteFile(temporaryPath);
        }
    }

    private async Task CommitTemporaryFileAsync(
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(_path))
                    File.Replace(temporaryPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                else
                    File.Move(temporaryPath, _path);
                return;
            }
            catch (Exception exception) when (
                attempt < CommitRetryDelays.Length &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(CommitRetryDelays[attempt], cancellationToken);
            }
        }
    }

    private void TryDeleteStaleTemporaryFiles()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        string prefix = $".{Path.GetFileName(_path)}.";
        try
        {
            foreach (string candidatePath in Directory.EnumerateFiles(
                directory,
                $"{prefix}*.tmp",
                SearchOption.TopDirectoryOnly))
            {
                string candidateName = Path.GetFileName(candidatePath);
                int idLength = candidateName.Length - prefix.Length - ".tmp".Length;
                if (idLength != 32 ||
                    !Guid.TryParseExact(candidateName.Substring(prefix.Length, idLength), "N", out _) ||
                    File.GetLastWriteTimeUtc(candidatePath) > DateTime.UtcNow - StaleTemporaryFileAge)
                {
                    continue;
                }
                TryDeleteFile(candidatePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort. A locked state directory must not block reading the committed file.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A later load/save will retry once the stale temporary object is no longer locked.
        }
    }
}
