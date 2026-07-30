using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using AutoCardSync.Infrastructure.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace AutoCardSync.Infrastructure.Copying;

public sealed class ChunkedFileCopier
{
    public const int BlockSizeBytes = 64 * 1024 * 1024; // logical checkpoint block
    public const int MaxReadAheadBytes = 16 * 1024 * 1024;
    private const int IoBufferSizeBytes = 1024 * 1024;
    private const int MaxReadAheadChunks = MaxReadAheadBytes / IoBufferSizeBytes;

    public Task<IReadOnlyList<BlockCheckpoint>> CopyFileAsync(
        string sourcePath,
        string tempTargetPath,
        IProgress<CopyProgress>? progress,
        CancellationToken ct,
        Action<SafeFileHandle, string>? openedTargetHandleCheck = null)
        => CopyFileAsync(
            sourcePath, tempTargetPath, 0, progress, ct, openedTargetHandleCheck, false, null);

    public async Task<IReadOnlyList<BlockCheckpoint>> CopyFileAsync(
        string sourcePath,
        string tempTargetPath,
        long resumeOffset,
        IProgress<CopyProgress>? progress,
        CancellationToken ct,
        Action<SafeFileHandle, string>? openedTargetHandleCheck = null,
        bool useExistingTarget = false,
        Func<BlockCheckpoint, CancellationToken, ValueTask>? checkpointCompleted = null)
    {
        var fileInfo = new FileInfo(sourcePath);
        long totalBytes = fileInfo.Length;
        if (resumeOffset < 0 || resumeOffset > totalBytes)
            throw new ArgumentOutOfRangeException(nameof(resumeOffset));
        if (resumeOffset % BlockSizeBytes != 0)
            throw new ArgumentException(
                $"Resume offset must be aligned to {BlockSizeBytes} bytes.", nameof(resumeOffset));

        bool openExisting = useExistingTarget || resumeOffset > 0;
        if (openExisting)
        {
            if (!File.Exists(tempTargetPath))
                throw new FileNotFoundException("Resume target does not exist.", tempTargetPath);
            if (new FileInfo(tempTargetPath).Length != resumeOffset)
                throw new InvalidDataException(
                    "Resume target length must exactly match the requested resume offset.");
        }
        else if (File.Exists(tempTargetPath))
        {
            throw new IOException($"Fresh temporary target already exists: '{tempTargetPath}'.");
        }

        int totalBlocks = (int)Math.Ceiling((double)totalBytes / BlockSizeBytes);
        if (totalBytes == 0)
            totalBlocks = 0;

        var checkpoints = new List<BlockCheckpoint>();

        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: IoBufferSizeBytes,
            useAsync: true);

        await using var targetStream = new FileStream(
            tempTargetPath,
            openExisting ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: IoBufferSizeBytes,
            useAsync: true);
        openedTargetHandleCheck?.Invoke(targetStream.SafeFileHandle, tempTargetPath);

        if (targetStream.Length != resumeOffset)
        {
            throw new InvalidDataException(
                "Resume target changed before its exclusive handle was acquired.");
        }
        if (resumeOffset > 0)
            await VerifyPrefixMatchesAsync(sourceStream, targetStream, resumeOffset, ct);

        sourceStream.Position = resumeOffset;
        targetStream.Position = resumeOffset;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);
        int blockIndex = checked((int)(resumeOffset / BlockSizeBytes));
        long bytesCopied = resumeOffset;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int blockBytes = 0;
                using var blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                while (blockBytes < BlockSizeBytes)
                {
                    int requested = Math.Min(buffer.Length, BlockSizeBytes - blockBytes);
                    int bytesRead = await sourceStream.ReadAsync(
                        buffer.AsMemory(0, requested), ct);

                    if (bytesRead == 0)
                        break;

                    await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    blockHash.AppendData(buffer.AsSpan(0, bytesRead));
                    blockBytes += bytesRead;
                }

                if (blockBytes == 0)
                    break;

                long offset = blockIndex * (long)BlockSizeBytes;

                await targetStream.FlushAsync(ct);
                targetStream.Flush(flushToDisk: true);

                string blockHashHex = Convert.ToHexStringLower(blockHash.GetHashAndReset());

                var checkpoint = new BlockCheckpoint(
                    BlockIndex: blockIndex,
                    Offset: offset,
                    Length: blockBytes,
                    BlockHash: blockHashHex,
                    ComputedAt: DateTimeOffset.UtcNow);

                checkpoints.Add(checkpoint);
                if (checkpointCompleted is not null)
                    await checkpointCompleted(checkpoint, ct);

                bytesCopied += blockBytes;
                blockIndex++;

                progress?.Report(new CopyProgress(
                    BytesCopied: bytesCopied,
                    BlocksCompleted: blockIndex,
                    TotalBlocks: totalBlocks,
                    PercentComplete: totalBytes > 0
                        ? Math.Round(bytesCopied * 100.0 / totalBytes, 2)
                        : 100.0));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return checkpoints;
    }

    [SupportedOSPlatform("windows")]
    public async Task<OpenedSourceCopyResult> CopyOpenedSourceAsync(
        SourceReadContinuityLease sourceLease,
        IReadOnlyList<OpenedCopyTarget> targets,
        IProgress<CopyProgress>? progress,
        CancellationToken ct,
        Func<BlockCheckpoint, CancellationToken, ValueTask>? commonCheckpointCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(sourceLease);
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
            throw new ArgumentException("At least one selected target is required.", nameof(targets));

        FileStream sourceStream = sourceLease.Stream;
        long totalBytes = sourceStream.Length;
        foreach (OpenedCopyTarget target in targets)
        {
            if (target.ResumeOffset < 0 || target.ResumeOffset > totalBytes)
                throw new ArgumentOutOfRangeException(nameof(targets), "Resume offset is outside the source length.");
            if (target.ResumeOffset != totalBytes && target.ResumeOffset % BlockSizeBytes != 0)
            {
                throw new ArgumentException(
                    $"Resume offset must be aligned to {BlockSizeBytes} bytes unless the file is already fully staged.",
                    nameof(targets));
            }
            if (target.Stream.Length != target.ResumeOffset)
                throw new InvalidDataException("The opened temporary object length does not match its persisted checkpoint.");
            target.Stream.Position = 0;
        }

        sourceStream.Position = 0;
        int totalBlocks = totalBytes == 0
            ? 0
            : checked((int)Math.Ceiling((double)totalBytes / BlockSizeBytes));
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var readAheadSlots = new SemaphoreSlim(MaxReadAheadChunks, MaxReadAheadChunks);
        var outstanding = new ConcurrentDictionary<long, PipelinedSourceChunk>();
        var channels = targets.ToDictionary(
            target => target,
            _ => Channel.CreateUnbounded<PipelinedSourceChunk>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            }));
        long commonDurableOffset = targets.Min(target => target.ResumeOffset);
        long peakReadAheadBytes = 0;

        void MarkCommonDurable(long blockEnd) =>
            Interlocked.Exchange(ref commonDurableOffset, blockEnd);
        void ReleaseChunk(PipelinedSourceChunk chunk) =>
            outstanding.TryRemove(chunk.Sequence, out _);

        Task[] targetWriters = targets.Select(target => WriteTargetPipelineAsync(
            target,
            channels[target].Reader,
            commonCheckpointCompleted,
            linkedCancellation,
            linkedCancellation.Token)).ToArray();
        Task<SourcePipelineResult> producer = ProduceSourcePipelineAsync(
            sourceStream,
            totalBytes,
            totalBlocks,
            targets.Count,
            channels.Values.Select(channel => channel.Writer).ToArray(),
            readAheadSlots,
            outstanding,
            MarkCommonDurable,
            ReleaseChunk,
            () => Volatile.Read(ref commonDurableOffset),
            value => UpdateMaximum(ref peakReadAheadBytes, value),
            progress,
            linkedCancellation,
            linkedCancellation.Token);

        try
        {
            await Task.WhenAll(targetWriters.Append(producer));
            SourcePipelineResult produced = await producer;
            string sourceSha256 = produced.SourceSha256;
            sourceLease.BindContentHash(sourceSha256);
            SourceContinuityResult continuity = sourceLease.VerifyHandleContinuity(sourceLease.Identity);
            if (!continuity.IsContinuous)
                throw new IOException($"Source identity changed during its single content read: {continuity.MismatchDetail}");

            return new OpenedSourceCopyResult(
                SourceSha256: sourceSha256,
                SourceBytesRead: produced.SourceBytesRead,
                Checkpoints: produced.Checkpoints,
                PeakReadAheadBeyondDurableCheckpointBytes: Volatile.Read(ref peakReadAheadBytes));
        }
        catch
        {
            linkedCancellation.Cancel();
            foreach (Channel<PipelinedSourceChunk> channel in channels.Values)
                channel.Writer.TryComplete();
            try
            {
                await Task.WhenAll(targetWriters);
            }
            catch
            {
                // Preserve the original producer/writer failure.
            }
            throw;
        }
        finally
        {
            linkedCancellation.Cancel();
            foreach (PipelinedSourceChunk chunk in outstanding.Values)
                chunk.Abort();
        }
    }

    private static async Task<SourcePipelineResult> ProduceSourcePipelineAsync(
        FileStream sourceStream,
        long totalBytes,
        int totalBlocks,
        int targetCount,
        IReadOnlyList<ChannelWriter<PipelinedSourceChunk>> writers,
        SemaphoreSlim readAheadSlots,
        ConcurrentDictionary<long, PipelinedSourceChunk> outstanding,
        Action<long> commonDurable,
        Action<PipelinedSourceChunk> released,
        Func<long> getCommonDurableOffset,
        Action<long> observeReadAhead,
        IProgress<CopyProgress>? progress,
        CancellationTokenSource linkedCancellation,
        CancellationToken cancellationToken)
    {
        var checkpoints = new List<BlockCheckpoint>(totalBlocks);
        using var fullHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        IncrementalHash blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long sourceOffset = 0;
        long blockOffset = 0;
        long latestProducedCheckpointOffset = getCommonDurableOffset();
        int blockBytes = 0;
        int blockIndex = 0;
        long sequence = 0;
        Exception? completionError = null;

        try
        {
            while (sourceOffset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await readAheadSlots.WaitAsync(cancellationToken);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);
                bool handedOff = false;
                try
                {
                    int requested = (int)Math.Min(IoBufferSizeBytes, totalBytes - sourceOffset);
                    int bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                    if (bytesRead != requested)
                        throw new EndOfStreamException("The source object ended before its frozen length.");

                    long chunkOffset = sourceOffset;
                    fullHash.AppendData(buffer.AsSpan(0, bytesRead));
                    blockHash.AppendData(buffer.AsSpan(0, bytesRead));
                    sourceOffset += bytesRead;
                    blockBytes += bytesRead;

                    BlockCheckpoint? checkpoint = null;
                    if (blockBytes == BlockSizeBytes || sourceOffset == totalBytes)
                    {
                        checkpoint = new BlockCheckpoint(
                            BlockIndex: blockIndex,
                            Offset: blockOffset,
                            Length: blockBytes,
                            BlockHash: Convert.ToHexStringLower(blockHash.GetHashAndReset()),
                            ComputedAt: DateTimeOffset.UtcNow);
                        checkpoints.Add(checkpoint);
                        latestProducedCheckpointOffset = sourceOffset;
                        blockHash.Dispose();
                        blockHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        blockIndex++;
                        blockOffset = sourceOffset;
                        blockBytes = 0;
                    }

                    var chunk = new PipelinedSourceChunk(
                        sequence++,
                        chunkOffset,
                        buffer,
                        bytesRead,
                        checkpoint,
                        targetCount,
                        readAheadSlots,
                        commonDurable,
                        released);
                    if (!outstanding.TryAdd(chunk.Sequence, chunk))
                        throw new InvalidOperationException("A source pipeline chunk sequence was duplicated.");
                    foreach (ChannelWriter<PipelinedSourceChunk> writer in writers)
                        await writer.WriteAsync(chunk, cancellationToken);
                    handedOff = true;

                    if (latestProducedCheckpointOffset > getCommonDurableOffset())
                    {
                        observeReadAhead(Math.Max(0, sourceOffset - latestProducedCheckpointOffset));
                    }
                    progress?.Report(new CopyProgress(
                        BytesCopied: sourceOffset,
                        BlocksCompleted: checkpoint is null ? blockIndex : checkpoint.BlockIndex + 1,
                        TotalBlocks: totalBlocks,
                        PercentComplete: totalBytes > 0
                            ? Math.Round(sourceOffset * 100.0 / totalBytes, 2)
                            : 100.0));
                }
                finally
                {
                    if (!handedOff)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        readAheadSlots.Release();
                    }
                }
            }

            return new SourcePipelineResult(
                Convert.ToHexStringLower(fullHash.GetHashAndReset()),
                sourceOffset,
                checkpoints);
        }
        catch (Exception exception)
        {
            completionError = exception;
            linkedCancellation.Cancel();
            throw;
        }
        finally
        {
            blockHash.Dispose();
            foreach (ChannelWriter<PipelinedSourceChunk> writer in writers)
                writer.TryComplete(completionError);
        }
    }

    private static async Task WriteTargetPipelineAsync(
        OpenedCopyTarget target,
        ChannelReader<PipelinedSourceChunk> reader,
        Func<BlockCheckpoint, CancellationToken, ValueTask>? commonCheckpointCompleted,
        CancellationTokenSource linkedCancellation,
        CancellationToken cancellationToken)
    {
        byte[] compareBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);
        try
        {
            await foreach (PipelinedSourceChunk chunk in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    long chunkEnd = chunk.Offset + chunk.Count;
                    if (chunk.Offset < target.ResumeOffset)
                    {
                        if (chunkEnd > target.ResumeOffset)
                        {
                            throw new InvalidDataException(
                                $"A resume boundary split a source pipeline chunk for target '{target.Role}'.");
                        }
                        int targetRead = await target.Stream.ReadAsync(
                            compareBuffer.AsMemory(0, chunk.Count), cancellationToken);
                        if (targetRead != chunk.Count ||
                            !compareBuffer.AsSpan(0, chunk.Count).SequenceEqual(chunk.Memory.Span))
                        {
                            throw new InvalidDataException(
                                $"Persisted temporary prefix no longer matches the source for target '{target.Role}'.");
                        }
                        target.PrefixBytesRead += chunk.Count;
                    }
                    else
                    {
                        await target.Stream.WriteAsync(chunk.Memory, cancellationToken);
                        target.BytesWritten += chunk.Count;
                    }

                    if (chunk.Checkpoint is BlockCheckpoint checkpoint)
                    {
                        long blockEnd = checkpoint.Offset + checkpoint.Length;
                        bool alreadyPersisted = blockEnd <= target.ResumeOffset;
                        if (alreadyPersisted)
                        {
                            BlockCheckpoint persisted = target.PersistedCheckpoints.SingleOrDefault(
                                value => value.BlockIndex == checkpoint.BlockIndex) ??
                                throw new InvalidDataException(
                                    $"Persisted checkpoint {checkpoint.BlockIndex} is missing for target '{target.Role}'.");
                            if (persisted.Offset != checkpoint.Offset ||
                                persisted.Length != checkpoint.Length ||
                                !string.Equals(persisted.BlockHash, checkpoint.BlockHash, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException(
                                    $"Persisted checkpoint {checkpoint.BlockIndex} changed for target '{target.Role}'.");
                            }
                        }
                        else
                        {
                            await target.Stream.FlushAsync(cancellationToken);
                            target.Stream.Flush(flushToDisk: true);
                        }

                        chunk.MarkTargetDurable();
                        await chunk.AllTargetsDurable.WaitAsync(cancellationToken);
                        if (commonCheckpointCompleted is not null)
                        {
                            await chunk.PersistCommonCheckpointAsync(
                                commonCheckpointCompleted,
                                cancellationToken);
                        }
                        else if (!alreadyPersisted && target.CheckpointCompleted is not null)
                        {
                            await target.CheckpointCompleted(checkpoint, cancellationToken);
                        }
                        chunk.MarkJournalPersisted();
                        await chunk.AllJournalsPersisted.WaitAsync(cancellationToken);
                    }
                }
                finally
                {
                    chunk.ReleaseTarget();
                }
            }
        }
        catch
        {
            linkedCancellation.Cancel();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compareBuffer);
        }
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

    public async Task<string> VerifyCompleteHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: IoBufferSizeBytes,
            useAsync: true);

        using var sha256 = SHA256.Create();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, IoBufferSizeBytes), ct);
                if (bytesRead == 0)
                    break;

                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            return Convert.ToHexStringLower(sha256.Hash!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task VerifyPrefixMatchesAsync(
        FileStream source,
        FileStream target,
        long length,
        CancellationToken ct)
    {
        byte[] sourceBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);
        byte[] targetBuffer = ArrayPool<byte>.Shared.Rent(IoBufferSizeBytes);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(IoBufferSizeBytes, remaining);
                int sourceRead = await source.ReadAsync(sourceBuffer.AsMemory(0, requested), ct);
                int targetRead = await target.ReadAsync(targetBuffer.AsMemory(0, requested), ct);

                if (sourceRead != requested
                    || targetRead != requested
                    || !sourceBuffer.AsSpan(0, requested).SequenceEqual(
                        targetBuffer.AsSpan(0, requested)))
                {
                    throw new InvalidDataException(
                        "Existing resume prefix does not match the current source file.");
                }

                remaining -= requested;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }
}

public sealed class OpenedCopyTarget
{
    public OpenedCopyTarget(
        string role,
        FileStream stream,
        long resumeOffset,
        IReadOnlyList<BlockCheckpoint> persistedCheckpoints,
        Func<BlockCheckpoint, CancellationToken, ValueTask>? checkpointCompleted)
    {
        Role = role;
        Stream = stream;
        ResumeOffset = resumeOffset;
        PersistedCheckpoints = persistedCheckpoints;
        CheckpointCompleted = checkpointCompleted;
    }

    public string Role { get; }
    public FileStream Stream { get; }
    public long ResumeOffset { get; }
    public IReadOnlyList<BlockCheckpoint> PersistedCheckpoints { get; }
    public Func<BlockCheckpoint, CancellationToken, ValueTask>? CheckpointCompleted { get; }
    public long BytesWritten { get; internal set; }
    public long PrefixBytesRead { get; internal set; }
}

internal sealed record SourcePipelineResult(
    string SourceSha256,
    long SourceBytesRead,
    IReadOnlyList<BlockCheckpoint> Checkpoints);

internal sealed class PipelinedSourceChunk
{
    private readonly SemaphoreSlim _readAheadSlots;
    private readonly Action<long> _commonDurable;
    private readonly Action<PipelinedSourceChunk> _released;
    private readonly TaskCompletionSource<bool> _allTargetsDurable = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _allJournalsPersisted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _targetCount;
    private readonly object _commonCheckpointGate = new();
    private Task? _commonCheckpointTask;
    private int _remainingTargets;
    private int _durableTargets;
    private int _persistedJournals;
    private int _completed;

    public PipelinedSourceChunk(
        long sequence,
        long offset,
        byte[] buffer,
        int count,
        BlockCheckpoint? checkpoint,
        int targetCount,
        SemaphoreSlim readAheadSlots,
        Action<long> commonDurable,
        Action<PipelinedSourceChunk> released)
    {
        Sequence = sequence;
        Offset = offset;
        Buffer = buffer;
        Count = count;
        Checkpoint = checkpoint;
        _targetCount = targetCount;
        _remainingTargets = targetCount;
        _readAheadSlots = readAheadSlots;
        _commonDurable = commonDurable;
        _released = released;
    }

    public long Sequence { get; }
    public long Offset { get; }
    public byte[] Buffer { get; }
    public int Count { get; }
    public BlockCheckpoint? Checkpoint { get; }
    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Count);
    public Task AllTargetsDurable => _allTargetsDurable.Task;
    public Task AllJournalsPersisted => _allJournalsPersisted.Task;

    public void MarkTargetDurable()
    {
        if (Interlocked.Increment(ref _durableTargets) == _targetCount)
            _allTargetsDurable.TrySetResult(true);
    }

    public Task PersistCommonCheckpointAsync(
        Func<BlockCheckpoint, CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Checkpoint is not BlockCheckpoint checkpoint)
            throw new InvalidOperationException("Only a checkpoint boundary can persist a common checkpoint.");
        lock (_commonCheckpointGate)
        {
            _commonCheckpointTask ??= callback(checkpoint, cancellationToken).AsTask();
            return _commonCheckpointTask;
        }
    }

    public void MarkJournalPersisted()
    {
        if (Interlocked.Increment(ref _persistedJournals) != _targetCount)
            return;
        if (Checkpoint is BlockCheckpoint checkpoint)
            _commonDurable(checkpoint.Offset + checkpoint.Length);
        _allJournalsPersisted.TrySetResult(true);
    }

    public void ReleaseTarget()
    {
        if (Interlocked.Decrement(ref _remainingTargets) == 0)
            Complete();
    }

    public void Abort() => Complete();

    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;
        _released(this);
        ArrayPool<byte>.Shared.Return(Buffer);
        _readAheadSlots.Release();
    }
}

public sealed record OpenedSourceCopyResult(
    string SourceSha256,
    long SourceBytesRead,
    IReadOnlyList<BlockCheckpoint> Checkpoints,
    long PeakReadAheadBeyondDurableCheckpointBytes = 0);

public record BlockCheckpoint(
    int BlockIndex,
    long Offset,
    long Length,
    string BlockHash,
    DateTimeOffset ComputedAt);

public record CopyProgress(
    long BytesCopied,
    int BlocksCompleted,
    int TotalBlocks,
    double PercentComplete);
