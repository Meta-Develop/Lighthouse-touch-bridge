using System.Text.Json.Nodes;
using Ltb.App;
using Ltb.Configuration;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverSettingsPreparationTests
{
    [Fact]
    public void FirstRunCreatesCurrentPackageSettingsAtomically()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = Paths(root, "current-package");

            var result = ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(paths);

            Assert.Equal(InternalDriverSettingsPreparationStatus.Created, result.Status);
            Assert.Contains("current package", result.Diagnostic, StringComparison.Ordinal);
            var settings = InternalDriverSettingsFile.Load(paths.SettingsPath);
            Assert.Equal(OpenVrPathsDiscoveryMode.Automatic, settings.OpenVrPathsDiscovery.Mode);
            Assert.Equal(paths.StagedDriverRoot, settings.StagedDriverRoot);
            Assert.Equal(paths.CalibrationProfileStorePath, settings.CalibrationProfileStorePath);
            Assert.Empty(TemporarySettingsFiles(paths.SettingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RelocatedPackageAtomicallyUpdatesOnlyAppOwnedStagedDriverRoot()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = Paths(root, "current-package");
            var previousPackageRoot = Path.Combine(root, "previous-package", "driver_ltb");
            InternalDriverSettingsFile.Save(
                paths.SettingsPath,
                new InternalDriverSettings(
                    InternalDriverSettingsSchema.CurrentVersion,
                    OpenVrPathsDiscovery.Automatic,
                    previousPackageRoot,
                    paths.CalibrationProfileStorePath));
            var expectedJson = JsonNode.Parse(File.ReadAllText(paths.SettingsPath))!.AsObject();
            expectedJson["staged_driver_root"] = paths.StagedDriverRoot;

            var result = ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(paths);

            Assert.Equal(
                InternalDriverSettingsPreparationStatus.StagedDriverRootUpdated,
                result.Status);
            Assert.Contains("relocated package", result.Diagnostic, StringComparison.Ordinal);
            var migrated = InternalDriverSettingsFile.Load(paths.SettingsPath);
            Assert.Equal(OpenVrPathsDiscoveryMode.Automatic, migrated.OpenVrPathsDiscovery.Mode);
            Assert.Equal(paths.StagedDriverRoot, migrated.StagedDriverRoot);
            Assert.Equal(paths.CalibrationProfileStorePath, migrated.CalibrationProfileStorePath);
            Assert.True(JsonNode.DeepEquals(
                expectedJson,
                JsonNode.Parse(File.ReadAllText(paths.SettingsPath))));
            Assert.Empty(TemporarySettingsFiles(paths.SettingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitOpenVrDiscoveryFailsClosedWithoutChangingSettings()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = Paths(root, "current-package");
            InternalDriverSettingsFile.Save(
                paths.SettingsPath,
                new InternalDriverSettings(
                    InternalDriverSettingsSchema.CurrentVersion,
                    OpenVrPathsDiscovery.FromFile(Path.Combine(root, "openvrpaths.vrpath")),
                    Path.Combine(root, "previous-package", "driver_ltb"),
                    paths.CalibrationProfileStorePath));
            var original = File.ReadAllBytes(paths.SettingsPath);

            var exception = Assert.Throws<InvalidDataException>(() =>
                ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(paths));

            Assert.Contains("automatic OpenVR discovery", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllBytes(paths.SettingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CalibrationProfilePathMismatchFailsClosedWithoutChangingSettings()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = Paths(root, "current-package");
            InternalDriverSettingsFile.Save(
                paths.SettingsPath,
                new InternalDriverSettings(
                    InternalDriverSettingsSchema.CurrentVersion,
                    OpenVrPathsDiscovery.Automatic,
                    Path.Combine(root, "previous-package", "driver_ltb"),
                    Path.Combine(root, "other-profiles", "calibration-profiles.json")));
            var original = File.ReadAllBytes(paths.SettingsPath);

            var exception = Assert.Throws<InvalidDataException>(() =>
                ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(paths));

            Assert.Contains("calibration profile path", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllBytes(paths.SettingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedAndUnsupportedSettingsFailClosedWithoutChangingSettings(
        bool unsupportedSchema)
    {
        var root = CreateTemporaryRoot();
        try
        {
            var paths = Paths(root, "current-package");
            Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsPath)!);
            var contents = unsupportedSchema
                ? InternalDriverSettingsJson.Serialize(new InternalDriverSettings(
                    InternalDriverSettingsSchema.CurrentVersion,
                    OpenVrPathsDiscovery.Automatic,
                    Path.Combine(root, "previous-package", "driver_ltb"),
                    paths.CalibrationProfileStorePath)).Replace(
                        "\"schema_version\": 1",
                        "\"schema_version\": 2",
                        StringComparison.Ordinal)
                : "{";
            File.WriteAllText(paths.SettingsPath, contents);
            var original = File.ReadAllBytes(paths.SettingsPath);

            var exception = Assert.Throws<InternalDriverSettingsFormatException>(() =>
                ProductionInternalDriverSessionRuntime.EnsureDefaultSettings(paths));

            Assert.Equal(
                unsupportedSchema
                    ? InternalDriverSettingsFormatReason.UnsupportedSchemaVersion
                    : InternalDriverSettingsFormatReason.MalformedJson,
                exception.Reason);
            Assert.Equal(original, File.ReadAllBytes(paths.SettingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static InternalDriverResolvedPaths Paths(string root, string packageName) => new(
        Path.Combine(root, "settings", "internal-driver.json"),
        Path.Combine(root, "profiles", "calibration-profiles.json"),
        Path.Combine(root, packageName, "driver_ltb"),
        Path.Combine(root, "logs", "internal-driver.jsonl"),
        Path.Combine(root, "driver", "registration-receipts.json"));

    private static string[] TemporarySettingsFiles(string settingsPath) =>
        Directory.GetFiles(
            Path.GetDirectoryName(settingsPath)!,
            $".{Path.GetFileName(settingsPath)}.*.tmp");

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ltb-settings-preparation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
