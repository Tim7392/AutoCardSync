using AutoCardSync.Standalone.Core.Configuration;

namespace AutoCardSync.Standalone.Core.Tests.Configuration;

public sealed class StandaloneConfigurationTests
{
    [Fact]
    public void Validate_accepts_normal_user_configuration()
    {
        var value = new StandaloneConfiguration
        {
            ApprovedSourceDirectories = ["DCIM", "PRIVATE\\AVCHD"],
            ApprovedExtensions = [".jpg", "MP4"],
            LocalTargetPath = @"C:\AutoCardSync-Test\Pictures",
            NasMappedTargetPath = @"Z:\AutoCardSync",
            TargetNamingRule = TargetNamingRule.ImportDate,
            AutoStartOnLogin = true,
        };

        ConfigurationValidationResult result = value.Validate();

        Assert.True(result.IsValid);
        Assert.Equal([".jpg", ".mp4"], value.NormalizedExtensions);
    }

    [Theory]
    [InlineData(@"..\DCIM")]
    [InlineData(@"C:\DCIM")]
    [InlineData(@"DCIM\..\PRIVATE")]
    [InlineData("")]
    public void Validate_rejects_unsafe_source_directory(string sourceDirectory)
    {
        StandaloneConfiguration value = ValidConfiguration() with
        {
            ApprovedSourceDirectories = [sourceDirectory],
        };

        ConfigurationValidationResult result = value.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "source_directory_invalid");
    }

    [Fact]
    public void Validate_rejects_same_local_and_mapped_target()
    {
        StandaloneConfiguration value = ValidConfiguration() with
        {
            LocalTargetPath = @"Z:\AutoCardSync",
            NasMappedTargetPath = @"Z:\AutoCardSync\",
        };

        ConfigurationValidationResult result = value.Validate();

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "targets_overlap");
    }

    [Fact]
    public void Nas_only_requires_only_the_mapped_nas_target()
    {
        StandaloneConfiguration value = ValidConfiguration() with
        {
            TargetMode = StandaloneTargetMode.NasOnly,
            LocalTargetPath = string.Empty,
        };

        Assert.True(value.Validate().IsValid);
    }

    [Fact]
    public void Local_only_requires_only_the_local_target()
    {
        StandaloneConfiguration value = ValidConfiguration() with
        {
            TargetMode = StandaloneTargetMode.LocalOnly,
            NasMappedTargetPath = string.Empty,
        };

        Assert.True(value.Validate().IsValid);
    }

    [Fact]
    public void Dual_target_requires_both_paths_and_rejects_overlap()
    {
        StandaloneConfiguration missingNas = ValidConfiguration() with
        {
            TargetMode = StandaloneTargetMode.LocalAndNas,
            NasMappedTargetPath = string.Empty,
        };
        StandaloneConfiguration overlapping = ValidConfiguration() with
        {
            TargetMode = StandaloneTargetMode.LocalAndNas,
            LocalTargetPath = @"Z:\AutoCardSync",
            NasMappedTargetPath = @"Z:\AutoCardSync\Child",
        };

        Assert.Contains(missingNas.Validate().Errors, error => error.Code == "nas_target_invalid");
        Assert.Contains(overlapping.Validate().Errors, error => error.Code == "targets_overlap");
    }

    [Fact]
    public void Undefined_target_mode_is_rejected_fail_closed()
    {
        StandaloneConfiguration value = ValidConfiguration() with
        {
            TargetMode = (StandaloneTargetMode)999,
        };

        Assert.Contains(value.Validate().Errors, error => error.Code == "target_mode_invalid");
    }
    private static StandaloneConfiguration ValidConfiguration() => new()
    {
        ApprovedSourceDirectories = ["DCIM"],
        ApprovedExtensions = [".jpg"],
        LocalTargetPath = @"C:\AutoCardSync\Local",
        NasMappedTargetPath = @"Z:\AutoCardSync",
        TargetNamingRule = TargetNamingRule.PreserveRelativePath,
        AutoStartOnLogin = true,
    };
}
