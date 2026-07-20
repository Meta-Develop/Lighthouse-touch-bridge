using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ltb.Configuration.Tests;

public sealed class InternalDriverSettingsTests
{
    [Fact]
    public void AutomaticDiscoveryRoundTripHasDeterministicExactShape()
    {
        var settings = Settings(OpenVrPathsDiscovery.Automatic);

        var json = InternalDriverSettingsJson.Serialize(settings);
        var roundTrippedJson = InternalDriverSettingsJson.Serialize(
            InternalDriverSettingsJson.Deserialize(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(
            [
                "schema_version",
                "openvrpaths_discovery",
                "staged_driver_root",
                "calibration_profile_store_path",
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        var discovery = root.GetProperty("openvrpaths_discovery");
        Assert.Equal(["mode"], discovery.EnumerateObject().Select(property => property.Name));
        Assert.Equal("automatic", discovery.GetProperty("mode").GetString());
        Assert.Equal(json, roundTrippedJson);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitDiscoveryFileRoundTripsCanonicalAbsolutePaths()
    {
        var rootPath = TemporaryRootPath();
        var discoveryPath = Path.Combine(rootPath, "openvrpaths.vrpath");
        var settings = Settings(OpenVrPathsDiscovery.FromFile(discoveryPath), rootPath);

        var loaded = InternalDriverSettingsJson.Deserialize(
            InternalDriverSettingsJson.Serialize(settings));

        Assert.Equal(OpenVrPathsDiscoveryMode.ExplicitFile, loaded.OpenVrPathsDiscovery.Mode);
        Assert.Equal(discoveryPath, loaded.OpenVrPathsDiscovery.FilePath);
        Assert.Equal(Path.Combine(rootPath, "staged-driver"), loaded.StagedDriverRoot);
        Assert.Equal(
            Path.Combine(rootPath, "profiles", "calibration-profiles.json"),
            loaded.CalibrationProfileStorePath);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/tmp/../tmp/ltb-staged-driver")]
    [InlineData("/tmp/ltb-staged-driver/")]
    public void SettingsRejectRelativeOrNonCanonicalStagedDriverRoots(string path)
    {
        Assert.Throws<ArgumentException>(() => new InternalDriverSettings(
            InternalDriverSettingsSchema.CurrentVersion,
            OpenVrPathsDiscovery.Automatic,
            path,
            "/tmp/ltb-profiles.json"));
    }

    [Theory]
    [InlineData("profiles.json")]
    [InlineData("/tmp/../tmp/ltb-profiles.json")]
    [InlineData("/")]
    public void SettingsRejectRelativeOrNonCanonicalProfileStorePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new InternalDriverSettings(
            InternalDriverSettingsSchema.CurrentVersion,
            OpenVrPathsDiscovery.Automatic,
            "/tmp/ltb-staged-driver",
            path));
    }

    [Theory]
    [InlineData("openvrpaths.vrpath")]
    [InlineData("/tmp/../tmp/openvrpaths.vrpath")]
    [InlineData("/")]
    public void ExplicitDiscoveryRejectsRelativeOrNonCanonicalFilePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => OpenVrPathsDiscovery.FromFile(path));
    }

    [Fact]
    public void MalformedMissingAndUnknownSettingsMembersAreRejected()
    {
        var malformed = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize("{"));
        Assert.Equal(InternalDriverSettingsFormatReason.MalformedJson, malformed.Reason);

        var missing = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize("{\"schema_version\":1}"));
        Assert.Equal(InternalDriverSettingsFormatReason.MalformedJson, missing.Reason);

        var root = SerializedSettingsObject();
        root["unexpected"] = true;
        var unknown = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize(root.ToJsonString()));
        Assert.Equal(InternalDriverSettingsFormatReason.MalformedJson, unknown.Reason);
    }

    [Theory]
    [InlineData("schema_version", "\"1\"")]
    [InlineData("openvrpaths_discovery", "true")]
    [InlineData("staged_driver_root", "42")]
    [InlineData("calibration_profile_store_path", "[]")]
    public void WrongSettingsMemberTypesAreMalformed(string property, string replacementJson)
    {
        var root = SerializedSettingsObject();
        root[property] = JsonNode.Parse(replacementJson);

        var exception = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize(root.ToJsonString()));

        Assert.Equal(InternalDriverSettingsFormatReason.MalformedJson, exception.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedSettingsVersionsRequireExplicitMigration(int schemaVersion)
    {
        var root = SerializedSettingsObject();
        root["schema_version"] = schemaVersion;

        var exception = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize(root.ToJsonString()));

        Assert.Equal(InternalDriverSettingsFormatReason.UnsupportedSchemaVersion, exception.Reason);
        Assert.Contains("explicit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("automatic", "/tmp/openvrpaths.vrpath")]
    [InlineData("explicit_file", null)]
    [InlineData("explicit_file", "relative/openvrpaths.vrpath")]
    public void InvalidDiscoveryShapesAreRejected(string mode, string? filePath)
    {
        var root = SerializedSettingsObject();
        var discovery = root["openvrpaths_discovery"]!.AsObject();
        discovery["mode"] = mode;
        if (filePath is null)
        {
            discovery.Remove("file_path");
        }
        else
        {
            discovery["file_path"] = filePath;
        }

        var exception = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize(root.ToJsonString()));

        Assert.Equal(InternalDriverSettingsFormatReason.InvalidSettingsData, exception.Reason);
    }

    [Theory]
    [InlineData("staged_driver_root", "relative/driver")]
    [InlineData("staged_driver_root", "/tmp/../tmp/driver")]
    [InlineData("calibration_profile_store_path", "relative/profiles.json")]
    [InlineData("calibration_profile_store_path", "/tmp/../tmp/profiles.json")]
    public void DeserializationRejectsInvalidPersistedPaths(string property, string value)
    {
        var root = SerializedSettingsObject();
        root[property] = value;

        var exception = Assert.Throws<InternalDriverSettingsFormatException>(() =>
            InternalDriverSettingsJson.Deserialize(root.ToJsonString()));

        Assert.Equal(InternalDriverSettingsFormatReason.InvalidSettingsData, exception.Reason);
    }

    [Fact]
    public void FirstRunAbsenceIsDistinctFromMalformedSettings()
    {
        var rootPath = TemporaryRootPath();
        var path = Path.Combine(rootPath, "missing", "settings.json");

        var result = InternalDriverSettingsFile.TryLoad(path);

        Assert.Equal(InternalDriverSettingsLoadStatus.NotFound, result.Status);
        Assert.Null(result.Settings);
        Assert.Throws<FileNotFoundException>(() => InternalDriverSettingsFile.Load(path));
    }

    [Fact]
    public void AtomicSettingsPersistenceReplacesCompleteJsonWithoutTemporaryFiles()
    {
        var rootPath = TemporaryRootPath();
        Directory.CreateDirectory(rootPath);
        var settingsPath = Path.Combine(rootPath, "settings.json");
        var original = Settings(OpenVrPathsDiscovery.Automatic, rootPath);
        var replacement = Settings(
            OpenVrPathsDiscovery.FromFile(Path.Combine(rootPath, "openvrpaths.vrpath")),
            rootPath);

        try
        {
            InternalDriverSettingsFile.Save(settingsPath, original);
            InternalDriverSettingsFile.Save(settingsPath, replacement);

            var result = InternalDriverSettingsFile.TryLoad(settingsPath);
            var loaded = Assert.IsType<InternalDriverSettings>(result.Settings);
            Assert.Equal(InternalDriverSettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(OpenVrPathsDiscoveryMode.ExplicitFile, loaded.OpenVrPathsDiscovery.Mode);
            Assert.Equal(
                InternalDriverSettingsJson.Serialize(replacement),
                File.ReadAllText(settingsPath));
            Assert.Empty(Directory.GetFiles(rootPath, ".settings.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public void SettingsFileBoundaryRejectsRelativeStoragePath()
    {
        var settings = Settings(OpenVrPathsDiscovery.Automatic);

        Assert.Throws<ArgumentException>(() =>
            InternalDriverSettingsFile.Save("relative-settings.json", settings));
        Assert.Throws<ArgumentException>(() =>
            InternalDriverSettingsFile.TryLoad("relative-settings.json"));
    }

    private static InternalDriverSettings Settings(
        OpenVrPathsDiscovery discovery,
        string? rootPath = null)
    {
        var root = rootPath ?? TemporaryRootPath();
        return new InternalDriverSettings(
            InternalDriverSettingsSchema.CurrentVersion,
            discovery,
            Path.Combine(root, "staged-driver"),
            Path.Combine(root, "profiles", "calibration-profiles.json"));
    }

    private static JsonObject SerializedSettingsObject() =>
        JsonNode.Parse(InternalDriverSettingsJson.Serialize(
            Settings(OpenVrPathsDiscovery.Automatic)))!.AsObject();

    private static string TemporaryRootPath() => Path.Combine(
        Path.GetTempPath(),
        $"ltb-internal-settings-{Guid.NewGuid():N}");
}
