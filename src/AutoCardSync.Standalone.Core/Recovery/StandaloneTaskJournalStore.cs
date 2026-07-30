using System.Text.Json;
using AutoCardSync.Application.Copying;
using AutoCardSync.Infrastructure.Copying;
using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Recovery;

public sealed record JournalPersistenceMetrics(long BytesWritten, int AtomicWrites);

public sealed record CommonCheckpointTargetBinding(
    Guid TargetId,
    string TemporaryObjectIdentity);

public sealed class StandaloneTaskJournalStore : ICopyJournalSink
{
    private static readonly JsonSerializerOptions SizeOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly bool _sharded;
    private readonly AtomicJsonFileStore<StandaloneTaskJournal>? _legacyStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StandaloneTaskJournal? _current;
    private StandaloneFileJournal[]? _mutableFiles;
    private Dictionary<Guid, int>? _fileIndexes;
    private long _bytesWritten;
    private int _atomicWrites;

    public StandaloneTaskJournalStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _sharded = !string.Equals(Path.GetExtension(_path), ".json", StringComparison.OrdinalIgnoreCase);
        if (!_sharded)
            _legacyStore = new AtomicJsonFileStore<StandaloneTaskJournal>(_path);
    }

    public JournalPersistenceMetrics PersistenceMetrics =>
        new(Interlocked.Read(ref _bytesWritten), Volatile.Read(ref _atomicWrites));

    public async Task InitializeAsync(
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ValidateJournal(journal);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await LoadCoreAsync(cancellationToken) is not null)
                throw new IOException("A task recovery journal already exists at the requested path.");

            SetCurrent(RecomputeFileStates(journal with { UpdatedAtUtc = DateTimeOffset.UtcNow }));
            if (_sharded)
                await InitializeShardedAsync(_current!, cancellationToken);
            else
                await SaveLegacyAsync(_current!, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StandaloneTaskJournal?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            SetCurrent(await LoadCoreAsync(cancellationToken));
            return SnapshotCurrent();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask OnFileStartedAsync(
        CopyJournalFileStarted value,
        CancellationToken cancellationToken) =>
        UpdateAsync(value.TaskId, value.FileId, value.TargetId, target => target with
        {
            TemporaryPath = value.TemporaryPath,
            TemporaryObjectIdentity = string.IsNullOrWhiteSpace(value.TemporaryObjectId)
                ? target.TemporaryObjectIdentity
                : value.TemporaryObjectId,
            FinalPath = value.FinalPath,
            ExpectedSha256 = value.ExpectedSha256,
            State = StandaloneTargetState.Copying,
            Error = null,
        }, file => file with
        {
            SourceFileIdentity = value.SourceFileId,
            State = StandaloneFileState.Copying,
        }, cancellationToken);

    public ValueTask OnCheckpointCompletedAsync(
        CopyJournalCheckpointCompleted value,
        CancellationToken cancellationToken) =>
        UpdateAsync(value.TaskId, value.FileId, value.TargetId, target => target with
        {
            TemporaryObjectIdentity = value.TemporaryObjectId,
            State = StandaloneTargetState.Copying,
            Checkpoints = target.Checkpoints
                .Where(existing => existing.BlockIndex != value.Checkpoint.BlockIndex)
                .Append(new StandaloneBlockCheckpoint
                {
                    BlockIndex = value.Checkpoint.BlockIndex,
                    Offset = value.Checkpoint.Offset,
                    Length = value.Checkpoint.Length,
                    Sha256 = value.Checkpoint.BlockHash,
                    PersistedAtUtc = DateTimeOffset.UtcNow,
                })
                .OrderBy(checkpoint => checkpoint.BlockIndex)
                .ToArray(),
        }, file => file with { State = StandaloneFileState.Copying }, cancellationToken);

    public async ValueTask RecordCommonCheckpointAsync(
        Guid taskId,
        Guid fileId,
        string relativePath,
        IReadOnlyList<CommonCheckpointTargetBinding> targetBindings,
        BlockCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(targetBindings);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (targetBindings.Count is < 1 or > 2 ||
            targetBindings.Any(binding =>
                binding.TargetId == Guid.Empty ||
                string.IsNullOrWhiteSpace(binding.TemporaryObjectIdentity)) ||
            targetBindings.Select(binding => binding.TargetId).Distinct().Count() != targetBindings.Count)
        {
            throw new InvalidDataException("A common checkpoint must bind every selected target exactly once.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneTaskJournal current = await GetCurrentCoreAsync(cancellationToken);
            if (current.TaskId != taskId)
                throw new InvalidDataException("Common checkpoint task identity does not match the active journal.");
            int fileIndex = GetFileIndex(current, fileId,
                "Common checkpoint file identity is not part of the manifest.");
            StandaloneFileJournal file = current.Files[fileIndex];
            if (!string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Common checkpoint relative path changed from the frozen file journal.");

            Guid[] requiredTargetIds =
            [
                .. current.TargetMode.RequiresLocal() ? [file.LocalTarget.TargetId] : Array.Empty<Guid>(),
                .. current.TargetMode.RequiresNas() ? [file.NasTarget.TargetId] : Array.Empty<Guid>(),
            ];
            if (!requiredTargetIds.OrderBy(value => value).SequenceEqual(
                    targetBindings.Select(value => value.TargetId).OrderBy(value => value)))
            {
                throw new InvalidDataException("Common checkpoint bindings do not cover the complete selected target set.");
            }

            var bindings = targetBindings.ToDictionary(binding => binding.TargetId);
            StandaloneTargetFileJournal local = current.TargetMode.RequiresLocal()
                ? AddCommonCheckpoint(file.LocalTarget, bindings[file.LocalTarget.TargetId], checkpoint)
                : file.LocalTarget;
            StandaloneTargetFileJournal nas = current.TargetMode.RequiresNas()
                ? AddCommonCheckpoint(file.NasTarget, bindings[file.NasTarget.TargetId], checkpoint)
                : file.NasTarget;
            StandaloneFileJournal changedFile = file with
            {
                State = StandaloneFileState.Copying,
                LocalTarget = local,
                NasTarget = nas,
            };
            StandaloneFileJournal[] files = GetMutableFiles(current);
            files[fileIndex] = changedFile with
            {
                State = ComputeFileState(current.TargetMode, changedFile),
            };
            _current = current with
            {
                Files = files,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await SaveFileOrLegacyAsync(_current, fileId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask OnFileVerifiedAsync(
        CopyJournalFileVerified value,
        CancellationToken cancellationToken) =>
        UpdateAsync(value.TaskId, value.FileId, value.TargetId, target => target with
        {
            FinalPath = value.FinalPath,
            FinalObjectIdentity = value.FinalObjectId,
            FinalSha256 = value.Sha256,
            State = StandaloneTargetState.Verified,
            AtomicallyPublished = value.ReusedExisting ? target.AtomicallyPublished : true,
            FullRereadSha256Passed = true,
            ReusedExisting = value.ReusedExisting,
            Error = null,
        }, file => file, cancellationToken);

    public ValueTask OnFileFailedAsync(
        CopyJournalFileFailed value,
        CancellationToken cancellationToken) =>
        UpdateAsync(value.TaskId, value.FileId, value.TargetId, target => target with
        {
            State = StandaloneTargetState.Failed,
            Error = value.Error,
        }, file => file with { State = StandaloneFileState.Failed }, cancellationToken);

    public async Task RecordSourceContentAsync(
        Guid taskId,
        Guid fileId,
        string sourceFileIdentity,
        string sourceSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneTaskJournal current = await GetCurrentCoreAsync(cancellationToken);
            if (current.TaskId != taskId)
                throw new InvalidDataException("Source content event task identity does not match the active journal.");
            int fileIndex = GetFileIndex(current, fileId,
                "Source content event file identity is not part of the inventory manifest.");
            StandaloneFileJournal[] files = GetMutableFiles(current);
            StandaloneFileJournal file = files[fileIndex];
            StandaloneFileJournal changedFile = file with
            {
                SourceFileIdentity = sourceFileIdentity,
                SourceSha256 = sourceSha256,
                LocalTarget = file.LocalTarget with
                {
                    ExpectedSha256 = file.LocalTarget.State == StandaloneTargetState.NotRequired
                        ? null
                        : sourceSha256,
                },
                NasTarget = file.NasTarget with
                {
                    ExpectedSha256 = file.NasTarget.State == StandaloneTargetState.NotRequired
                        ? null
                        : sourceSha256,
                },
            };
            files[fileIndex] = changedFile with
            {
                State = ComputeFileState(current.TargetMode, changedFile),
            };
            _current = current with
            {
                Files = files,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await SaveFileOrLegacyAsync(_current, fileId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FreezeContentManifestAsync(
        string manifestHash,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestHash);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneTaskJournal current = await GetCurrentCoreAsync(cancellationToken);
            if (current.Files.Any(file => string.IsNullOrWhiteSpace(file.SourceSha256)))
                throw new InvalidDataException("The content manifest cannot freeze before every source SHA-256 is persisted.");
            _current = current with
            {
                ManifestHash = manifestHash,
                ContentManifestFrozen = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            if (_sharded)
            {
                await SaveMeasuredAsync(
                    ManifestStatePath,
                    new StandaloneManifestJournalState(
                        _current.InventoryManifestHash,
                        _current.ManifestHash,
                        _current.ContentManifestFrozen,
                        _current.UpdatedAtUtc),
                    cancellationToken);
            }
            else
            {
                await SaveLegacyAsync(_current, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkCompletionReceiptPersistedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneTaskJournal current = await GetCurrentCoreAsync(cancellationToken);
            _current = current with
            {
                LocalCompletionReceiptPersisted = true,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            if (_sharded)
            {
                await SaveMeasuredAsync(
                    StatePath,
                    new StandaloneTaskJournalState(true, _current.UpdatedAtUtc),
                    cancellationToken);
            }
            else
            {
                await SaveLegacyAsync(_current, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static StandaloneTargetFileJournal AddCommonCheckpoint(
        StandaloneTargetFileJournal target,
        CommonCheckpointTargetBinding binding,
        BlockCheckpoint checkpoint)
    {
        if (target.State is StandaloneTargetState.NotRequired or StandaloneTargetState.Failed ||
            target.TargetId != binding.TargetId)
        {
            throw new InvalidDataException("A common checkpoint target is not an active selected target.");
        }
        if (!string.IsNullOrWhiteSpace(target.TemporaryObjectIdentity) &&
            !string.Equals(
                target.TemporaryObjectIdentity,
                binding.TemporaryObjectIdentity,
                StringComparison.Ordinal))
        {
            throw new IOException("A common checkpoint temporary object identity changed.");
        }

        return target with
        {
            TemporaryObjectIdentity = binding.TemporaryObjectIdentity,
            State = StandaloneTargetState.Copying,
            Checkpoints = target.Checkpoints
                .Where(existing => existing.BlockIndex != checkpoint.BlockIndex)
                .Append(new StandaloneBlockCheckpoint
                {
                    BlockIndex = checkpoint.BlockIndex,
                    Offset = checkpoint.Offset,
                    Length = checkpoint.Length,
                    Sha256 = checkpoint.BlockHash,
                    PersistedAtUtc = DateTimeOffset.UtcNow,
                })
                .OrderBy(value => value.BlockIndex)
                .ToArray(),
        };
    }

    private async ValueTask UpdateAsync(
        Guid taskId,
        Guid fileId,
        Guid targetId,
        Func<StandaloneTargetFileJournal, StandaloneTargetFileJournal> updateTarget,
        Func<StandaloneFileJournal, StandaloneFileJournal> updateFile,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            StandaloneTaskJournal current = await GetCurrentCoreAsync(cancellationToken);
            if (current.TaskId != taskId)
                throw new InvalidDataException("Copy journal event task identity does not match the active journal.");
            int fileIndex = GetFileIndex(current, fileId,
                "Copy journal event file identity is not part of the manifest.");
            StandaloneFileJournal[] files = GetMutableFiles(current);
            StandaloneFileJournal changed = updateFile(files[fileIndex]);
            if (changed.LocalTarget.TargetId == targetId && changed.LocalTarget.State != StandaloneTargetState.NotRequired)
                changed = changed with { LocalTarget = updateTarget(changed.LocalTarget) };
            else if (changed.NasTarget.TargetId == targetId && changed.NasTarget.State != StandaloneTargetState.NotRequired)
                changed = changed with { NasTarget = updateTarget(changed.NasTarget) };
            else
                throw new InvalidDataException("Copy journal event target identity is not a required target for the file journal.");
            files[fileIndex] = changed with
            {
                State = ComputeFileState(current.TargetMode, changed),
            };
            _current = current with
            {
                Files = files,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await SaveFileOrLegacyAsync(_current, fileId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InitializeShardedAsync(
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(_path) &&
            (File.Exists(TaskHeaderPath) || Directory.EnumerateFileSystemEntries(_path).Any()))
        {
            throw new IOException("A task recovery journal already exists at the requested path.");
        }

        Directory.CreateDirectory(FilesDirectory);
        var header = new StandaloneTaskJournalHeader(
            journal.SchemaVersion,
            journal.TaskId,
            journal.SourceIdentity,
            journal.CardInstanceId,
            journal.TargetMode,
            journal.LocalTargetIdentity,
            journal.LocalTargetRoot,
            journal.NasTargetIdentity,
            journal.NasTargetRoot,
            journal.Files.Select(file => file.FileId).OrderBy(id => id).ToArray());
        await SaveMeasuredAsync(
            ManifestStatePath,
            new StandaloneManifestJournalState(
                journal.InventoryManifestHash,
                journal.ManifestHash,
                journal.ContentManifestFrozen,
                journal.UpdatedAtUtc),
            cancellationToken);
        await SaveMeasuredAsync(
            StatePath,
            new StandaloneTaskJournalState(
                journal.LocalCompletionReceiptPersisted,
                journal.UpdatedAtUtc),
            cancellationToken);
        foreach (StandaloneFileJournal file in journal.Files)
            await SaveMeasuredAsync(GetFilePath(file.FileId), file, cancellationToken);
        // task.json is the discoverability/commit marker and is published last.
        await SaveMeasuredAsync(TaskHeaderPath, header, cancellationToken);
    }

    private async Task<StandaloneTaskJournal?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!_sharded)
            return await _legacyStore!.LoadAsync(cancellationToken);
        if (!File.Exists(TaskHeaderPath))
            return null;

        StandaloneTaskJournalHeader header =
            await new AtomicJsonFileStore<StandaloneTaskJournalHeader>(TaskHeaderPath).LoadAsync(cancellationToken) ??
            throw new InvalidDataException("The task journal header cannot be loaded.");
        StandaloneManifestJournalState manifest =
            await new AtomicJsonFileStore<StandaloneManifestJournalState>(ManifestStatePath).LoadAsync(cancellationToken) ??
            throw new InvalidDataException("The task manifest journal state cannot be loaded.");
        StandaloneTaskJournalState state =
            await new AtomicJsonFileStore<StandaloneTaskJournalState>(StatePath).LoadAsync(cancellationToken) ??
            throw new InvalidDataException("The task journal state cannot be loaded.");

        var files = new List<StandaloneFileJournal>(header.FileIds.Count);
        foreach (Guid fileId in header.FileIds)
        {
            StandaloneFileJournal file =
                await new AtomicJsonFileStore<StandaloneFileJournal>(GetFilePath(fileId)).LoadAsync(cancellationToken) ??
                throw new InvalidDataException($"The task file journal '{fileId:N}' cannot be loaded.");
            if (file.FileId != fileId)
                throw new InvalidDataException("A task file journal identity does not match its file name.");
            files.Add(file);
        }

        DateTimeOffset fileUpdated = header.FileIds.Count == 0
            ? default
            : header.FileIds.Select(id => File.GetLastWriteTimeUtc(GetFilePath(id))).Max();
        DateTimeOffset updated = new[] { manifest.UpdatedAtUtc, state.UpdatedAtUtc, fileUpdated }.Max();
        StandaloneTaskJournal journal = new()
        {
            SchemaVersion = header.SchemaVersion,
            TaskId = header.TaskId,
            SourceIdentity = header.SourceIdentity,
            CardInstanceId = header.CardInstanceId,
            TargetMode = header.TargetMode,
            InventoryManifestHash = manifest.InventoryManifestHash,
            ManifestHash = manifest.ManifestHash,
            ContentManifestFrozen = manifest.ContentManifestFrozen,
            LocalTargetIdentity = header.LocalTargetIdentity,
            LocalTargetRoot = header.LocalTargetRoot,
            NasTargetIdentity = header.NasTargetIdentity,
            NasTargetRoot = header.NasTargetRoot,
            Files = files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            UpdatedAtUtc = updated,
            LocalCompletionReceiptPersisted = state.LocalCompletionReceiptPersisted,
        };
        ValidateJournal(journal);
        return RecomputeFileStates(journal);
    }

    private async Task<StandaloneTaskJournal> GetCurrentCoreAsync(CancellationToken cancellationToken)
    {
        if (_current is not null)
            return _current;
        SetCurrent(await LoadCoreAsync(cancellationToken));
        return _current ??
            throw new InvalidOperationException("The task recovery journal has not been initialized.");
    }

    private void SetCurrent(StandaloneTaskJournal? journal)
    {
        if (journal is null)
        {
            _current = null;
            _mutableFiles = null;
            _fileIndexes = null;
            return;
        }

        _mutableFiles = journal.Files.ToArray();
        _fileIndexes = _mutableFiles
            .Select((file, index) => (file.FileId, index))
            .ToDictionary(value => value.FileId, value => value.index);
        _current = journal with { Files = _mutableFiles };
    }

    private StandaloneTaskJournal? SnapshotCurrent() => _current is null
        ? null
        : _current with { Files = _current.Files.ToArray() };

    private StandaloneFileJournal[] GetMutableFiles(StandaloneTaskJournal current)
    {
        if (!ReferenceEquals(current, _current) || _mutableFiles is null)
            throw new InvalidOperationException("The mutable task journal cache is not initialized.");
        return _mutableFiles;
    }

    private int GetFileIndex(StandaloneTaskJournal current, Guid fileId, string error)
    {
        _ = GetMutableFiles(current);
        if (_fileIndexes is null || !_fileIndexes.TryGetValue(fileId, out int index))
            throw new InvalidDataException(error);
        return index;
    }

    private async Task SaveFileOrLegacyAsync(
        StandaloneTaskJournal journal,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        if (_sharded)
        {
            int index = GetFileIndex(journal, fileId, "The task file journal is not part of the active manifest.");
            await SaveMeasuredAsync(GetFilePath(fileId), journal.Files[index], cancellationToken);
        }
        else
        {
            await SaveLegacyAsync(journal, cancellationToken);
        }
    }

    private async Task SaveLegacyAsync(
        StandaloneTaskJournal journal,
        CancellationToken cancellationToken)
    {
        await _legacyStore!.SaveAsync(journal, cancellationToken);
        RecordWrite(journal);
    }

    private async Task SaveMeasuredAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await new AtomicJsonFileStore<T>(path).SaveAsync(value!, cancellationToken);
        RecordWrite(value!);
    }

    private void RecordWrite<T>(T value)
    {
        int bytes = JsonSerializer.SerializeToUtf8Bytes(value, SizeOptions).Length;
        Interlocked.Add(ref _bytesWritten, bytes);
        Interlocked.Increment(ref _atomicWrites);
    }

    private static StandaloneTaskJournal RecomputeFileStates(StandaloneTaskJournal journal) =>
        journal with
        {
            Files = journal.Files.Select(file => file with
            {
                State = ComputeFileState(journal.TargetMode, file),
            }).ToArray(),
        };

    private static StandaloneFileState ComputeFileState(
        StandaloneTargetMode mode,
        StandaloneFileJournal file)
    {
        StandaloneTargetFileJournal[] required =
        [
            .. mode.RequiresLocal() ? [file.LocalTarget] : Array.Empty<StandaloneTargetFileJournal>(),
            .. mode.RequiresNas() ? [file.NasTarget] : Array.Empty<StandaloneTargetFileJournal>(),
        ];
        if (required.Length == 0)
            return StandaloneFileState.Failed;
        if (required.Any(target => target.State == StandaloneTargetState.Failed))
            return StandaloneFileState.Failed;
        if (required.All(target => target.State == StandaloneTargetState.Verified))
            return StandaloneFileState.Verified;
        if (required.All(target => target.State == StandaloneTargetState.Pending))
            return StandaloneFileState.Pending;
        return StandaloneFileState.Copying;
    }

    private static void ValidateJournal(StandaloneTaskJournal journal)
    {
        if (journal.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(journal.SourceIdentity))
            throw new InvalidDataException("The task recovery journal is missing frozen source identities.");
        if (!Enum.IsDefined(journal.TargetMode))
            throw new InvalidDataException("The task recovery journal contains an unsupported target mode.");
        if (journal.SchemaVersion >= 2 && string.IsNullOrWhiteSpace(journal.InventoryManifestHash))
            throw new InvalidDataException("The task recovery journal is missing its inventory manifest hash.");
        if (journal.ContentManifestFrozen && string.IsNullOrWhiteSpace(journal.ManifestHash))
            throw new InvalidDataException("A frozen content manifest hash is missing.");
        if (journal.TargetMode.RequiresLocal() && string.IsNullOrWhiteSpace(journal.LocalTargetIdentity))
            throw new InvalidDataException("The required local target identity is missing.");
        if (journal.TargetMode.RequiresNas() && string.IsNullOrWhiteSpace(journal.NasTargetIdentity))
            throw new InvalidDataException("The required NAS target identity is missing.");
        if (journal.Files is null || journal.Files.Any(file => file is null))
            throw new InvalidDataException("The task recovery journal contains an invalid file collection.");
        if (journal.Files.Select(file => file.FileId).Distinct().Count() != journal.Files.Count)
            throw new InvalidDataException("The task recovery journal contains duplicate file identities.");

        foreach (StandaloneFileJournal file in journal.Files)
        {
            if (journal.ContentManifestFrozen && string.IsNullOrWhiteSpace(file.SourceSha256))
                throw new InvalidDataException($"Frozen journal file '{file.RelativePath}' has no source SHA-256 fact.");
            ValidateTargetBinding(
                file.RelativePath,
                file.LocalTarget,
                "local",
                journal.LocalTargetIdentity,
                journal.TargetMode.RequiresLocal());
            ValidateTargetBinding(
                file.RelativePath,
                file.NasTarget,
                "nas",
                journal.NasTargetIdentity,
                journal.TargetMode.RequiresNas());
        }
    }

    private static void ValidateTargetBinding(
        string relativePath,
        StandaloneTargetFileJournal target,
        string role,
        string expectedIdentity,
        bool required)
    {
        if (target is null)
            throw new InvalidDataException($"Journal file '{relativePath}' has no {role} target state.");
        if (!required)
        {
            if (target.State != StandaloneTargetState.NotRequired)
                throw new InvalidDataException($"Journal file '{relativePath}' has an active unselected {role} target.");
            return;
        }

        if (target.State == StandaloneTargetState.NotRequired ||
            target.TargetId == Guid.Empty ||
            !string.Equals(target.TargetRole, role, StringComparison.Ordinal) ||
            !string.Equals(target.TargetIdentity, expectedIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Journal file '{relativePath}' does not bind the required {role} target to the frozen task target.");
        }
    }

    private string TaskHeaderPath => Path.Combine(_path, "task.json");
    private string ManifestStatePath => Path.Combine(_path, "manifest.json");
    private string StatePath => Path.Combine(_path, "state.json");
    private string FilesDirectory => Path.Combine(_path, "files");
    private string GetFilePath(Guid fileId) => Path.Combine(FilesDirectory, $"{fileId:N}.json");

    private sealed record StandaloneTaskJournalHeader(
        int SchemaVersion,
        Guid TaskId,
        string SourceIdentity,
        Guid CardInstanceId,
        StandaloneTargetMode TargetMode,
        string LocalTargetIdentity,
        string LocalTargetRoot,
        string NasTargetIdentity,
        string NasTargetRoot,
        IReadOnlyList<Guid> FileIds);

    private sealed record StandaloneManifestJournalState(
        string InventoryManifestHash,
        string ManifestHash,
        bool ContentManifestFrozen,
        DateTimeOffset UpdatedAtUtc);

    private sealed record StandaloneTaskJournalState(
        bool LocalCompletionReceiptPersisted,
        DateTimeOffset UpdatedAtUtc);
}
