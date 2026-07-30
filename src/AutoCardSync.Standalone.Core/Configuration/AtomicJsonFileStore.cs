using System.Text.Json;

namespace AutoCardSync.Standalone.Core.Configuration;

public sealed class AtomicJsonFileStore<T>
{
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
        if (!File.Exists(_path))
            return default;

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        string directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("The JSON store path must have a parent directory.");
        Directory.CreateDirectory(directory);

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
            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _path);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated && File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
