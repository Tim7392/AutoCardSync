using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Infrastructure.FileSystem;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class ChunkedFileCopierPipelineTests : IDisposable
{
    private const long PayloadBytes = 82L * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-Pipeline", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Single_source_pipeline_reads_ahead_within_16MiB_and_journals_only_after_all_targets_are_durable()
    {
        string sourcePath = Path.Combine(_root, "source.bin");
        string localTemp = Path.Combine(_root, "local", "source.tmp");
        string nasTemp = Path.Combine(_root, "nas", "source.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(localTemp)!);
        Directory.CreateDirectory(Path.GetDirectoryName(nasTemp)!);
        await WriteDeterministicFileAsync(sourcePath, PayloadBytes);

        var guard = new SourceHandleContinuityGuard();
        await using SourceReadContinuityLease sourceLease = guard.AcquireMetadataReadLease(sourcePath);
        await using var localStream = OpenTemp(localTemp);
        await using var nasStream = OpenTemp(nasTemp);
        var bothJournalCallbacksStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readAheadObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int firstCheckpointCallbacks = 0;
        long maximumSourceProgress = 0;

        ValueTask OnCheckpointAsync(BlockCheckpoint checkpoint, CancellationToken cancellationToken)
        {
            if (checkpoint.BlockIndex != 0)
                return ValueTask.CompletedTask;

            Assert.True(localStream.Length >= ChunkedFileCopier.BlockSizeBytes);
            Assert.True(nasStream.Length >= ChunkedFileCopier.BlockSizeBytes);
            if (Interlocked.Increment(ref firstCheckpointCallbacks) == 2)
                bothJournalCallbacksStarted.TrySetResult();
            return new ValueTask(WaitForReadAheadAsync(cancellationToken));
        }

        async Task WaitForReadAheadAsync(CancellationToken cancellationToken)
        {
            await bothJournalCallbacksStarted.Task.WaitAsync(cancellationToken);
            await readAheadObserved.Task.WaitAsync(cancellationToken);
        }

        var progress = new InlineProgress<CopyProgress>(value =>
        {
            UpdateMaximum(ref maximumSourceProgress, value.BytesCopied);
            if (value.BytesCopied > ChunkedFileCopier.BlockSizeBytes)
                readAheadObserved.TrySetResult();
        });
        var targets = new[]
        {
            new OpenedCopyTarget("local", localStream, 0, [], OnCheckpointAsync),
            new OpenedCopyTarget("nas", nasStream, 0, [], OnCheckpointAsync),
        };

        OpenedSourceCopyResult result = await new ChunkedFileCopier().CopyOpenedSourceAsync(
            sourceLease,
            targets,
            progress,
            CancellationToken.None);

        Assert.Equal(PayloadBytes, result.SourceBytesRead);
        Assert.Equal(PayloadBytes, localStream.Length);
        Assert.Equal(PayloadBytes, nasStream.Length);
        Assert.Equal(2, firstCheckpointCallbacks);
        Assert.True(maximumSourceProgress > ChunkedFileCopier.BlockSizeBytes);
        Assert.InRange(
            result.PeakReadAheadBeyondDurableCheckpointBytes,
            1,
            ChunkedFileCopier.MaxReadAheadBytes);
        Assert.Equal(result.SourceSha256, await ComputeSha256Async(localTemp));
        Assert.Equal(result.SourceSha256, await ComputeSha256Async(nasTemp));
    }

    private static FileStream OpenTemp(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.ReadWrite,
        Share = FileShare.Read,
        BufferSize = 1024 * 1024,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
    });

    private static async Task WriteDeterministicFileAsync(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long written = 0;
        while (written < length)
        {
            int count = (int)Math.Min(buffer.Length, length - written);
            for (int index = 0; index < count; index++)
                buffer[index] = unchecked((byte)((written + index) * 17 + ((written + index) >> 9)));
            await stream.WriteAsync(buffer.AsMemory(0, count));
            written += count;
        }
        await stream.FlushAsync();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }

    private static void UpdateMaximum(ref long location, long value)
    {
        long current = Volatile.Read(ref location);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
