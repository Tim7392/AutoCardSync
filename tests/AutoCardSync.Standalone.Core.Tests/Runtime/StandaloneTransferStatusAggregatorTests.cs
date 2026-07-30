using AutoCardSync.Application.Copying;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests.Runtime;

public sealed class StandaloneTransferStatusAggregatorTests
{
    [Fact]
    public void Local_update_then_nas_update_preserves_local_snapshot()
    {
        TestTransfer transfer = CreateTransfer();
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(isLocal: true, bytesCopied: 600, bytesTransferred: 600,
                filesVerified: 3, currentFile: "DCIM\\local.jpg"), out _));
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(isLocal: false, bytesCopied: 250, bytesTransferred: 250,
                filesVerified: 1, currentFile: "DCIM\\nas.jpg"),
            out StandaloneTransferStatusSnapshot snapshot));

        Assert.Equal(600, snapshot.Local.BytesTransferred);
        Assert.Equal(3, snapshot.Local.FilesVerified);
        Assert.Equal("DCIM\\local.jpg", snapshot.Local.CurrentFile);
        Assert.Equal(250, snapshot.Nas.BytesTransferred);
        Assert.Equal("DCIM\\nas.jpg", snapshot.CurrentFile);
    }

    [Fact]
    public void Nas_update_then_local_update_preserves_nas_snapshot()
    {
        TestTransfer transfer = CreateTransfer();
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(isLocal: false, bytesCopied: 700, bytesTransferred: 700,
                filesVerified: 4, currentFile: "DCIM\\nas.mov"), out _));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(isLocal: true, bytesCopied: 300, bytesTransferred: 300,
                filesVerified: 2, currentFile: "DCIM\\local.mov"),
            out StandaloneTransferStatusSnapshot snapshot));

        Assert.Equal(700, snapshot.Nas.BytesTransferred);
        Assert.Equal(4, snapshot.Nas.FilesVerified);
        Assert.Equal("DCIM\\nas.mov", snapshot.Nas.CurrentFile);
        Assert.Equal(300, snapshot.Local.BytesTransferred);
        Assert.Equal("DCIM\\local.mov", snapshot.CurrentFile);
    }

    [Fact]
    public void Overall_progress_uses_unique_source_and_both_latest_targets_without_regressing()
    {
        TestTransfer transfer = CreateTransfer();
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 800), out _));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(isLocal: true, bytesCopied: 800, bytesTransferred: 800),
            out StandaloneTransferStatusSnapshot localOnly));
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(isLocal: false, bytesCopied: 400, bytesTransferred: 400),
            out StandaloneTransferStatusSnapshot bothTargets));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(isLocal: true, bytesCopied: 900, bytesTransferred: 900),
            out StandaloneTransferStatusSnapshot advanced));

        Assert.Equal(22.86, localOnly.OverallPercent);
        Assert.Equal(28.57, bothTargets.OverallPercent);
        Assert.Equal(30, advanced.OverallPercent);
        Assert.True(advanced.OverallPercent >= bothTargets.OverallPercent);
    }

    [Theory]
    [InlineData(StandaloneTargetMode.LocalOnly, true)]
    [InlineData(StandaloneTargetMode.NasOnly, false)]
    public void Single_target_mode_does_not_average_against_an_unselected_target(
        StandaloneTargetMode mode,
        bool updateLocal)
    {
        TestTransfer transfer = CreateTransfer(mode: mode);
        Assert.True(transfer.Aggregator.TryUpdateSource(transfer.SourceStatus(bytesRead: 800), out _));
        bool updated = updateLocal
            ? transfer.Aggregator.TryUpdateLocal(
                transfer.TargetStatus(true, bytesCopied: 800, bytesTransferred: 800), out StandaloneTransferStatusSnapshot snapshot)
            : transfer.Aggregator.TryUpdateNas(
                transfer.TargetStatus(false, bytesCopied: 800, bytesTransferred: 800), out snapshot);

        Assert.True(updated);
        Assert.Equal(40, snapshot.OverallPercent);
        Assert.False(updateLocal
            ? transfer.Aggregator.TryUpdateNas(transfer.TargetStatus(false, bytesTransferred: 800), out _)
            : transfer.Aggregator.TryUpdateLocal(transfer.TargetStatus(true, bytesTransferred: 800), out _));
    }

    [Fact]
    public void Completed_file_count_does_not_reset_on_single_target_callback()
    {
        TestTransfer transfer = CreateTransfer();
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, filesVerified: 3), out _));
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(false, filesVerified: 2), out StandaloneTransferStatusSnapshot twoComplete));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, filesVerified: 4), out StandaloneTransferStatusSnapshot later));

        Assert.Equal(2, twoComplete.CompletedFiles);
        Assert.Equal(2, later.CompletedFiles);
    }

    [Fact]
    public void Source_speed_comes_only_from_unique_source_counter_not_target_byte_sum()
    {
        TestTransfer transfer = CreateTransfer();
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 100), out StandaloneTransferStatusSnapshot source));
        Assert.Equal(100, source.SourceBytesPerSecond, 6);

        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, bytesCopied: 100, bytesTransferred: 100), out _));
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(false, bytesCopied: 100, bytesTransferred: 100),
            out StandaloneTransferStatusSnapshot targets));

        Assert.Equal(100, targets.SourceBytesPerSecond, 6);
        Assert.Equal(100, targets.CurrentBytesPerSecond, 6);
        Assert.NotEqual(200, targets.SourceBytesPerSecond);
    }

    [Fact]
    public void Source_speed_uses_four_second_rolling_window_and_monotonic_time()
    {
        TestTransfer transfer = CreateTransfer(totalBytes: 10_000);
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 100), out StandaloneTransferStatusSnapshot first));
        transfer.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 700), out StandaloneTransferStatusSnapshot fourthSecond));
        transfer.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 1_100), out StandaloneTransferStatusSnapshot sixthSecond));

        Assert.Equal(100, first.SourceBytesPerSecond, 6);
        Assert.Equal(175, fourthSecond.SourceBytesPerSecond, 6);
        Assert.Equal(200, sixthSecond.SourceBytesPerSecond, 6);
    }

    [Fact]
    public void Local_and_nas_write_speeds_are_measured_independently()
    {
        TestTransfer transfer = CreateTransfer();
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, bytesTransferred: 120), out _));
        Assert.True(transfer.Aggregator.TryUpdateNas(
            transfer.TargetStatus(false, bytesTransferred: 40), out StandaloneTransferStatusSnapshot snapshot));

        Assert.Equal(120, snapshot.LocalBytesPerSecond, 6);
        Assert.Equal(40, snapshot.NasBytesPerSecond, 6);
        Assert.Equal(0, snapshot.SourceBytesPerSecond);
    }

    [Fact]
    public void Eta_decreases_as_remaining_source_bytes_decrease_at_constant_source_speed()
    {
        TestTransfer transfer = CreateTransfer(totalBytes: 1_000);
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 100), out StandaloneTransferStatusSnapshot first));
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 200), out StandaloneTransferStatusSnapshot second));

        Assert.Equal(9, first.EtaSeconds, 6);
        Assert.Equal(8, second.EtaSeconds, 6);
        Assert.True(second.EtaSeconds < first.EtaSeconds);
    }

    [Fact]
    public void Source_average_freezes_and_active_speed_decays_when_source_stops_reading()
    {
        TestTransfer transfer = CreateTransfer(totalBytes: 1_000);
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 200), out StandaloneTransferStatusSnapshot copying));
        Assert.Equal(200, copying.SourceAverageBytesPerSecond, 6);

        transfer.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, bytesTransferred: 200), out StandaloneTransferStatusSnapshot stalled));
        Assert.Equal(0, stalled.SourceBytesPerSecond);
        Assert.Equal(200, stalled.SourceAverageBytesPerSecond, 6);

        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 200, phase: CopyPhase.TemporaryVerifying),
            out StandaloneTransferStatusSnapshot verifying));
        Assert.Equal(0, verifying.SourceBytesPerSecond);
        Assert.Equal(200, verifying.SourceAverageBytesPerSecond, 6);
        transfer.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, bytesTransferred: 300, phase: CopyPhase.TemporaryVerifying),
            out StandaloneTransferStatusSnapshot later));
        Assert.Equal(verifying.SourceAverageBytesPerSecond, later.SourceAverageBytesPerSecond, 6);
    }

    [Fact]
    public void New_task_does_not_inherit_previous_task_speed()
    {
        var clock = new ManualTimeProvider();
        var aggregator = new StandaloneTransferStatusAggregator(clock);
        Guid firstTask = Guid.NewGuid();
        aggregator.StartTask(firstTask, Guid.NewGuid(), Guid.NewGuid(), 1000, 5);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(aggregator.TryUpdateSource(new SourceReadStatus
        {
            TaskId = firstTask,
            BytesRead = 500,
            TotalBytes = 1000,
            TotalFiles = 5,
            Phase = CopyPhase.Copying,
        }, out StandaloneTransferStatusSnapshot first));
        Assert.Equal(500, first.SourceBytesPerSecond, 6);

        Guid secondTask = Guid.NewGuid();
        aggregator.StartTask(secondTask, Guid.NewGuid(), Guid.NewGuid(), 1000, 5);
        Assert.True(aggregator.TryUpdateSource(new SourceReadStatus
        {
            TaskId = secondTask,
            TotalBytes = 1000,
            TotalFiles = 5,
            Phase = CopyPhase.Copying,
        }, out StandaloneTransferStatusSnapshot reset));
        Assert.Equal(0, reset.SourceBytesPerSecond);
        Assert.Equal(0, reset.SourceAverageBytesPerSecond);
    }

    [Theory]
    [InlineData(CopyPhase.TemporaryVerifying)]
    [InlineData(CopyPhase.FinalVerifying)]
    [InlineData(CopyPhase.Completed)]
    [InlineData(CopyPhase.Failed)]
    public void Non_copying_source_phase_stops_active_speed(CopyPhase phase)
    {
        TestTransfer transfer = CreateTransfer();
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 100), out StandaloneTransferStatusSnapshot active));
        Assert.True(active.SourceBytesPerSecond > 0);

        transfer.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 100, phase: phase, isComplete: phase == CopyPhase.Completed),
            out StandaloneTransferStatusSnapshot stopped));
        Assert.Equal(0, stopped.SourceBytesPerSecond);
        Assert.Equal(0, stopped.CurrentBytesPerSecond);
    }

    [Fact]
    public void Verification_progress_uses_observed_bytes_instead_of_phase_jumps()
    {
        TestTransfer transfer = CreateTransfer(mode: StandaloneTargetMode.LocalOnly);
        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: 1000, completedFiles: 5, phase: CopyPhase.Completed, isComplete: true),
            out _));

        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(
                true, bytesCopied: 1000, bytesTransferred: 1000,
                temporaryBytesVerified: 250, phase: CopyPhase.TemporaryVerifying),
            out StandaloneTransferStatusSnapshot temporary));
        Assert.Equal(56.25, temporary.OverallPercent);

        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(
                true, bytesCopied: 1000, bytesTransferred: 1000,
                temporaryBytesVerified: 1000, finalBytesVerified: 250,
                phase: CopyPhase.FinalVerifying),
            out StandaloneTransferStatusSnapshot final));
        Assert.Equal(81.25, final.OverallPercent);
    }

    [Fact]
    public void Verification_speed_and_eta_are_based_on_observed_reread_bytes()
    {
        TestTransfer transfer = CreateTransfer(mode: StandaloneTargetMode.LocalOnly);
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(
                true, bytesCopied: 1000, bytesTransferred: 1000,
                temporaryBytesVerified: 100, phase: CopyPhase.TemporaryVerifying),
            out StandaloneTransferStatusSnapshot snapshot));

        Assert.Equal(100, snapshot.LocalVerificationBytesPerSecond, 6);
        Assert.Equal(19, snapshot.EtaSeconds, 6);
    }

    [Fact]
    public void One_tib_progress_math_stays_finite_and_preserves_long_byte_counts()
    {
        const long oneTib = 1_099_511_627_776;
        TestTransfer transfer = CreateTransfer(
            totalBytes: oneTib,
            totalFiles: 4096,
            mode: StandaloneTargetMode.LocalOnly);
        transfer.Clock.Advance(TimeSpan.FromSeconds(8));

        Assert.True(transfer.Aggregator.TryUpdateSource(
            transfer.SourceStatus(bytesRead: oneTib / 2, completedFiles: 2048), out _));
        Assert.True(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(
                true,
                bytesCopied: oneTib / 2,
                bytesTransferred: oneTib / 2,
                filesVerified: 2048),
            out StandaloneTransferStatusSnapshot snapshot));

        Assert.Equal(oneTib / 2, snapshot.SourceBytesRead);
        Assert.Equal(oneTib / 2, snapshot.Local.BytesTransferred);
        Assert.True(double.IsFinite(snapshot.OverallPercent));
        Assert.InRange(snapshot.OverallPercent, 0, 100);
        Assert.True(double.IsFinite(snapshot.SourceBytesPerSecond));
        Assert.True(double.IsFinite(snapshot.EtaSeconds));
    }

    [Fact]
    public void Stopped_task_rejects_late_source_and_target_updates()
    {
        TestTransfer transfer = CreateTransfer();
        transfer.Aggregator.Stop();
        transfer.Clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(transfer.Aggregator.TryUpdateSource(transfer.SourceStatus(bytesRead: 100), out _));
        Assert.False(transfer.Aggregator.TryUpdateLocal(
            transfer.TargetStatus(true, bytesTransferred: 100), out _));
        Assert.False(transfer.Aggregator.IsActive(transfer.TaskId));
    }

    private static TestTransfer CreateTransfer(
        long totalBytes = 1000,
        int totalFiles = 5,
        StandaloneTargetMode mode = StandaloneTargetMode.LocalAndNas)
    {
        var clock = new ManualTimeProvider();
        var aggregator = new StandaloneTransferStatusAggregator(clock);
        Guid taskId = Guid.NewGuid();
        Guid localTargetId = Guid.NewGuid();
        Guid nasTargetId = Guid.NewGuid();
        aggregator.StartTask(taskId, localTargetId, nasTargetId, totalBytes, totalFiles, mode);
        return new TestTransfer(
            aggregator,
            clock,
            taskId,
            localTargetId,
            nasTargetId,
            totalBytes,
            totalFiles);
    }

    private sealed record TestTransfer(
        StandaloneTransferStatusAggregator Aggregator,
        ManualTimeProvider Clock,
        Guid TaskId,
        Guid LocalTargetId,
        Guid NasTargetId,
        long TotalBytes,
        int TotalFiles)
    {
        public SourceReadStatus SourceStatus(
            long bytesRead = 0,
            int completedFiles = 0,
            string? currentFile = null,
            CopyPhase phase = CopyPhase.Copying,
            bool isComplete = false) => new()
            {
                TaskId = TaskId,
                BytesRead = bytesRead,
                TotalBytes = TotalBytes,
                CompletedFiles = completedFiles,
                TotalFiles = TotalFiles,
                CurrentFile = currentFile,
                Phase = phase,
                IsComplete = isComplete,
            };

        public TargetCopyStatus TargetStatus(
            bool isLocal,
            long bytesCopied = 0,
            long bytesTransferred = 0,
            long temporaryBytesVerified = 0,
            long finalBytesVerified = 0,
            int filesVerified = 0,
            string? currentFile = null,
            CopyPhase phase = CopyPhase.Copying) => new()
            {
                TaskId = TaskId,
                TargetId = isLocal ? LocalTargetId : NasTargetId,
                BytesCopied = bytesCopied,
                BytesTransferred = bytesTransferred,
                TemporaryBytesVerified = temporaryBytesVerified,
                FinalBytesVerified = finalBytesVerified,
                TotalBytes = TotalBytes,
                TotalFiles = TotalFiles,
                FilesVerified = filesVerified,
                CurrentFile = currentFile,
                Phase = phase,
                IsComplete = phase == CopyPhase.Completed,
            };
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            _timestamp += duration.Ticks;
        }
    }
}
