using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Tests.Configuration;

public sealed class SourceDirectorySelectionTests : IDisposable
{
    private readonly string _volumeRoot = Path.Combine(
        Path.GetTempPath(),
        $"AutoCardSync-SourcePicker-{Guid.NewGuid():N}");

    [Fact]
    public void Selected_folder_is_converted_to_a_safe_volume_relative_directory()
    {
        string selected = Path.Combine(_volumeRoot, "XDROOT", "Clip");
        Directory.CreateDirectory(selected);

        SourceDirectorySelection result = SourceDirectorySelectionResolver.Resolve(selected, _volumeRoot);

        Assert.Equal(Path.GetFullPath(selected), result.FullPath);
        Assert.Equal(Path.GetFullPath(_volumeRoot), result.VolumeRoot);
        Assert.Equal(Path.Combine("XDROOT", "Clip"), result.RelativePath);
    }

    [Fact]
    public void Selecting_the_volume_root_is_preserved_as_the_explicit_root_scope()
    {
        Directory.CreateDirectory(_volumeRoot);

        SourceDirectorySelection result = SourceDirectorySelectionResolver.Resolve(_volumeRoot, _volumeRoot);

        Assert.Equal(Path.GetFullPath(_volumeRoot), result.FullPath);
        Assert.Equal(Path.GetFullPath(_volumeRoot), result.VolumeRoot);
        Assert.Equal(".", result.RelativePath);
    }

    [Fact]
    public void Folder_outside_the_selected_volume_is_rejected()
    {
        string selected = Path.Combine(Path.GetTempPath(), $"AutoCardSync-Outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_volumeRoot);
        Directory.CreateDirectory(selected);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                SourceDirectorySelectionResolver.Resolve(selected, _volumeRoot));
        }
        finally
        {
            Directory.Delete(selected, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_volumeRoot))
            Directory.Delete(_volumeRoot, recursive: true);
    }
}
