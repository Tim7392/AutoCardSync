using System.Reflection;
using AutoCardSync.Application.Copying;
using AutoCardSync.Application.Manifests;
using AutoCardSync.Domain.Manifests;
using AutoCardSync.Standalone.Core.Cards;

namespace AutoCardSync.Standalone.Core.Tests.Copying;

/// <summary>
/// Regression contract for schema-v3 flat destination paths.
/// </summary>
public sealed class SchemaV3DestinationPathRegressionTests
{
    [Fact]
    public void Unique_nested_source_names_keep_their_original_file_names()
    {
        IReadOnlyDictionary<string, string> map = BuildDestinationRelativePathMap(
            [@"DCIM\100MEDIA\A001.MOV", @"PRIVATE\AVCHD\B001.MP4"]);

        Assert.Equal("A001.MOV", map[@"DCIM\100MEDIA\A001.MOV"]);
        Assert.Equal("B001.MP4", map[@"PRIVATE\AVCHD\B001.MP4"]);
    }

    [Fact]
    public void V3_destination_paths_flatten_nested_sources_and_disambiguate_case_insensitively_stably()
    {
        string[] sources =
        [
            @"DCIM\100MEDIA\Clip.MOV",
            @"PRIVATE\AVCHD\Clip.MOV",
            @"MISC\clip.mov",
        ];

        IReadOnlyDictionary<string, string> forward = BuildDestinationRelativePathMap(sources);
        IReadOnlyDictionary<string, string> reversed = BuildDestinationRelativePathMap(sources.Reverse().ToArray());

        Assert.All(forward.Values, destination =>
        {
            Assert.DoesNotContain('\\', destination);
            Assert.DoesNotContain('/', destination);
            Assert.Contains("__", destination, StringComparison.Ordinal);
        });
        Assert.Equal(sources.Length, forward.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(forward.Count, reversed.Count);
        foreach (string source in sources)
            Assert.Equal(forward[source], reversed[source]);
    }

    [Fact]
    public void Schema_two_uses_source_path_while_schema_three_uses_destination_path()
    {
        const string sourceRelativePath = @"DCIM\100MEDIA\Clip.MOV";
        const string destinationRelativePath = "Clip--a1b2c3d4.MOV";

        Assert.Equal(
            sourceRelativePath,
            ResolveDestinationRelativePath(2, sourceRelativePath, destinationRelativePath));
        Assert.Equal(
            destinationRelativePath,
            ResolveDestinationRelativePath(3, sourceRelativePath, destinationRelativePath));
    }

    [Fact]
    public void Delta_manifest_omits_unchanged_files_and_keeps_existing_identity_churn_semantics()
    {
        const string policyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        TaskManifest currentInventory = CreateInventory(
            (@"DCIM\A.mov", 100, "replacement-a"),
            (@"DCIM\B.mov", 200, "file-b"),
            (@"DCIM\C.mov", 300, "file-c"));
        var baseline = new StandaloneInventoryBaseline
        {
            SchemaVersion = 2,
            CardInstanceId = Guid.NewGuid(),
            CameraTemplateId = Guid.NewGuid(),
            SourceIdentity = "source-a",
            SelectionPolicyHash = policyHash,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Entries =
            [
                new StandaloneInventoryBaselineEntry
                {
                    RelativePath = @"DCIM\A.mov",
                    Length = 100,
                    LastModifiedUtc = Timestamp,
                    SourceFileIdentity = "file-a",
                    SourceFileIdentityType = "test",
                },
                new StandaloneInventoryBaselineEntry
                {
                    RelativePath = @"DCIM\B.mov",
                    Length = 200,
                    LastModifiedUtc = Timestamp,
                    SourceFileIdentity = "file-b",
                    SourceFileIdentityType = "test",
                },
            ],
        };

        StandaloneInventoryDelta advisoryIdentityDelta = StandaloneInventoryBaselineStore.Compare(
            baseline,
            policyHash,
            currentInventory);
        TaskManifest manifest = ManifestBuilder.CreateDeltaInventoryManifest(
            currentInventory,
            advisoryIdentityDelta.TransferEntries.Select(entry => entry.RelativePath).ToArray());

        Assert.Equal([@"DCIM\C.mov"], manifest.Entries.Select(entry => entry.RelativePath));
        Assert.Equal([@"DCIM\A.mov"], advisoryIdentityDelta.IdentityChangedPaths);

        StandaloneInventoryDelta transferIdentityDelta = StandaloneInventoryBaselineStore.Compare(
            baseline,
            policyHash,
            currentInventory,
            identityChangesRequireTransfer: true);
        Assert.Equal(
            [@"DCIM\A.mov", @"DCIM\C.mov"],
            transferIdentityDelta.TransferEntries
                .Select(entry => entry.RelativePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> BuildDestinationRelativePathMap(
        IReadOnlyList<string> sourceRelativePaths)
    {
        MethodInfo method = typeof(CopyPathConvention).GetMethod(
            "BuildDestinationRelativePathMap",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(IReadOnlyList<string>)],
            modifiers: null) ??
            throw new MissingMethodException(
                typeof(CopyPathConvention).FullName,
                "BuildDestinationRelativePathMap(IReadOnlyList<string>)");

        object result = method.Invoke(null, [sourceRelativePaths]) ??
            throw new InvalidOperationException("The destination path map was null.");
        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(result);
    }

    private static string ResolveDestinationRelativePath(
        int schemaVersion,
        string sourceRelativePath,
        string destinationRelativePath)
    {
        MethodInfo method = typeof(CopyPathConvention).GetMethod(
            "ResolveDestinationRelativePath",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [typeof(int), typeof(string), typeof(string)],
            modifiers: null) ??
            throw new MissingMethodException(
                typeof(CopyPathConvention).FullName,
                "ResolveDestinationRelativePath(Int32, String, String)");

        return Assert.IsType<string>(method.Invoke(
            null,
            [schemaVersion, sourceRelativePath, destinationRelativePath]));
    }

    private static readonly DateTimeOffset Timestamp = new(2026, 8, 4, 8, 30, 0, TimeSpan.Zero);

    private static TaskManifest CreateInventory(params (string Path, long Length, string FileId)[] files)
    {
        var manifest = new TaskManifest(Guid.NewGuid()) { FilterRuleVersion = "metadata-only" };
        foreach ((string path, long length, string fileId) in files)
        {
            manifest.AddEntry(new ManifestEntry
            {
                RelativePath = path,
                FileSize = length,
                LastModifiedUtc = Timestamp,
                SourceFileId = fileId,
                SourceFileIdType = "test",
            });
        }
        manifest.Freeze();
        return manifest;
    }
}
