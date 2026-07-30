using System.Security.Cryptography;
using AutoCardSync.Infrastructure.Copying;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

public sealed class AtomicFilePublisherProgressTests : IDisposable
{
    private const int PayloadBytes = 20 * 1024 * 1024;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AutoCardSync-V1-VerificationProgress", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Temporary_and_final_full_rereads_report_monotonic_actual_bytes()
    {
        Directory.CreateDirectory(_root);
        string temporaryPath = Path.Combine(_root, "payload.partial");
        string finalPath = Path.Combine(_root, "payload.bin");
        await WritePayloadAsync(temporaryPath);
        string expectedHash = await ComputeSha256Async(temporaryPath);
        var temporaryProgress = new List<FileVerificationProgress>();
        var finalProgress = new List<FileVerificationProgress>();

        await using PublishResult published = await new AtomicFilePublisher().PublishAsync(
            temporaryPath,
            finalPath,
            expectedHash,
            PayloadBytes,
            CancellationToken.None,
            allowVerifiedExisting: false,
            temporaryVerificationProgress: new InlineProgress<FileVerificationProgress>(
                temporaryProgress.Add));

        Assert.True(published.Success, published.Error);
        AssertProgress(temporaryProgress);

        await published.EnsureContinuousAsync(
            CancellationToken.None,
            new InlineProgress<FileVerificationProgress>(finalProgress.Add));

        AssertProgress(finalProgress);
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(temporaryPath));
    }

    private static void AssertProgress(IReadOnlyList<FileVerificationProgress> values)
    {
        Assert.NotEmpty(values);
        Assert.Equal(0, values[0].BytesVerified);
        Assert.Equal(PayloadBytes, values[^1].BytesVerified);
        Assert.Contains(values, value => value.BytesVerified is > 0 and < PayloadBytes);
        Assert.All(values, value => Assert.Equal(PayloadBytes, value.TotalBytes));
        for (int index = 1; index < values.Count; index++)
            Assert.True(values[index].BytesVerified >= values[index - 1].BytesVerified);
    }

    private static async Task WritePayloadAsync(string path)
    {
        byte[] buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        for (int index = 0; index < PayloadBytes / buffer.Length; index++)
        {
            Array.Fill(buffer, (byte)index);
            await stream.WriteAsync(buffer);
        }
        await stream.FlushAsync();
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
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
