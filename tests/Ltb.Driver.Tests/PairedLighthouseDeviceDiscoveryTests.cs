using System.Text.Json;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class PairedLighthouseDeviceDiscoveryTests
{
    [Fact]
    public async Task EnumeratesGenericTrackersInCanonicalSerialOrderFromConfigIdentity()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "lhr-directory-name-must-not-win",
            DeviceJson("generic_tracker", "lhr-bb02", " VIVE Tracker 3.0 "));
        fixture.AddConfig(
            "zzz-unrelated-directory-name",
            DeviceJson("generic_tracker", "LhR-aA01", "VIVE Tracker Pro MV"));

        var result = await fixture.Discovery.DiscoverAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(PairedLighthouseDeviceDiagnosticCode.None, result.DiagnosticCode);
        Assert.Null(result.FailurePath);
        Assert.Collection(
            result.Devices,
            first =>
            {
                Assert.Equal("LHR-AA01", first.Serial);
                Assert.Equal("VIVE Tracker Pro MV", first.Model);
                Assert.True(first.HasSerial("lhr-aa01"));
            },
            second =>
            {
                Assert.Equal("LHR-BB02", second.Serial);
                Assert.Equal("VIVE Tracker 3.0", second.Model);
                Assert.True(second.HasSerial("LhR-Bb02"));
            });
    }

    [Fact]
    public async Task FiltersParsedNonTrackersAndKeepsValidTrackersDeterministically()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "03-controller",
            DeviceJson("controller", "LHR-CONTROLLER", "VIVE Controller MV"));
        fixture.AddConfig(
            "01-tracker-z",
            DeviceJson("generic_tracker", "LHR-Z900", "Tracker Z"));
        fixture.AddConfig(
            "02-hmd",
            DeviceJson("hmd", "LHR-HMD", "VIVE Pro"));
        fixture.AddConfig(
            "04-tracker-a",
            DeviceJson("generic_tracker", "LHR-A100", "Tracker A"));

        var result = await fixture.Discovery.DiscoverAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(["LHR-A100", "LHR-Z900"], result.Devices.Select(x => x.Serial));
    }

    [Fact]
    public async Task RejectsCaseInsensitiveDuplicateTrackerSerials()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "first",
            DeviceJson("generic_tracker", "lhr-duplicate", "Tracker One"));
        fixture.AddConfig(
            "second",
            DeviceJson("generic_tracker", "LHR-DUPLICATE", "Tracker Two"));

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DuplicateTrackerSerial);
        Assert.Contains("LHR-DUPLICATE", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsMissingLighthouseDirectory()
    {
        using var fixture = new PairedDiscoveryFixture(createLighthouseDirectory: false);

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.LighthouseDirectoryMissing);
        Assert.Equal(fixture.LighthouseDirectory, result.FailurePath);
    }

    [Fact]
    public async Task ReportsEmptyDeviceDirectoryEnumeration()
    {
        using var fixture = new PairedDiscoveryFixture();

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.TrackerEnumerationEmpty);
    }

    [Fact]
    public async Task ReportsEmptyTrackerEnumerationWhenEveryValidConfigIsNonTracker()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "controller",
            DeviceJson("controller", "LHR-CONTROLLER", "VIVE Controller MV"));
        fixture.AddConfig(
            "hmd",
            DeviceJson("hmd", "LHR-HMD", "VIVE Pro"));

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.TrackerEnumerationEmpty);
    }

    [Fact]
    public async Task FailsClosedWhenAnyDeviceDirectoryLacksConfig()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "valid",
            DeviceJson("generic_tracker", "LHR-VALID", "Tracker"));
        fixture.AddDeviceDirectory("missing-config");

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMissing);
        Assert.EndsWith(
            Path.Combine("missing-config", "config.json"),
            result.FailurePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsClosedWhenAnyConfigIsUnreadable()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "valid",
            DeviceJson("generic_tracker", "LHR-VALID", "Tracker"));
        var unreadable = fixture.AddConfig(
            "unreadable",
            DeviceJson("generic_tracker", "LHR-UNREADABLE", "Tracker"));
        fixture.FileSystem.UnreadableFile = unreadable;

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigUnreadable);
        Assert.Equal(unreadable, result.FailurePath);
    }

    [Fact]
    public async Task FailsClosedWhenAnyConfigContainsMalformedJson()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "valid",
            DeviceJson("generic_tracker", "LHR-VALID", "Tracker"));
        fixture.AddConfig("malformed", """{ "device_class": "generic_tracker" """);

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed);
        Assert.Contains("JSON is malformed", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsTheFirstCanonicalDirectoryFailureDeterministically()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddDeviceDirectory("z-missing-config");
        fixture.AddConfig(
            "m-valid",
            DeviceJson("generic_tracker", "LHR-VALID", "Tracker"));
        var firstFailure = fixture.AddConfig(
            "a-malformed-config",
            """{"device_class":"generic_tracker" """);

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed);
        Assert.Equal(firstFailure, result.FailurePath);
    }

    [Theory]
    [InlineData("""{"device_class":"generic_tracker","model_number":"Tracker"}""",
        "device_serial_number")]
    [InlineData("""{"device_class":"generic_tracker","device_serial_number":"LHR-X"}""",
        "model_number")]
    [InlineData("""{"device_serial_number":"LHR-X","model_number":"Tracker"}""",
        "device_class")]
    [InlineData("""{"device_class":"generic_tracker","device_serial_number":7,"model_number":"Tracker"}""",
        "device_serial_number")]
    [InlineData("""{"device_class":"generic_tracker","device_serial_number":"LHR-X","model_number":false}""",
        "model_number")]
    public async Task RejectsStructurallyMalformedDeviceConfigs(
        string json,
        string expectedField)
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig("lhr-directory-is-not-identity", json);

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed);
        Assert.Contains(expectedField, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParsesNonTrackerBeforeFilteringAndRejectsItsInvalidClassField()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "valid",
            DeviceJson("generic_tracker", "LHR-VALID", "Tracker"));
        fixture.AddConfig(
            "invalid-non-tracker",
            """{"device_class":12,"device_serial_number":"LHR-HMD","model_number":"HMD"}""");

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed);
    }

    [Fact]
    public async Task ValidatesNonTrackerIdentityFieldsBeforeFiltering()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.AddConfig(
            "invalid-hmd",
            """{"device_class":"hmd","model_number":"VIVE Pro"}""");

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.DeviceConfigMalformed);
        Assert.Contains(
            "device_serial_number",
            result.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailsClosedWhenLighthouseDirectoryCannotBeEnumerated()
    {
        using var fixture = new PairedDiscoveryFixture();
        fixture.FileSystem.RefuseDirectoryEnumeration = true;

        var result = await fixture.Discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.LighthouseDirectoryUnreadable);
        Assert.Equal(fixture.LighthouseDirectory, result.FailurePath);
    }

    [Fact]
    public async Task PropagatesMissingOpenVrRegistryAsTypedEnumerationFailure()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ltb-paired-missing-openvr"));
        var discovery = new PairedLighthouseDeviceDiscovery(
            new SteamVrPathDiscovery(
                new FakeSteamVrHostEnvironment
                {
                    LocalApplicationDataPath = Path.Combine(root, "local"),
                },
                new MemorySteamVrFileSystem()),
            new MemoryPairedLighthouseDeviceFileSystem());

        var result = await discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMissing);
    }

    [Fact]
    public async Task PropagatesUnavailableConfigRootAsTypedEnumerationFailure()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            """{"config":[],"runtime":["/unused"]}""");
        var discovery = new PairedLighthouseDeviceDiscovery(
            new SteamVrPathDiscovery(
                new FakeSteamVrHostEnvironment
                {
                    LocalApplicationDataPath = fixture.LocalApplicationData,
                },
                fixture.FileSystem),
            new MemoryPairedLighthouseDeviceFileSystem());

        var result = await discovery.DiscoverAsync();

        AssertFailure(
            result,
            PairedLighthouseDeviceDiagnosticCode.ConfigRootUnavailable);
    }

    private static string DeviceJson(
        string deviceClass,
        string serial,
        string model) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["device_class"] = deviceClass,
            ["device_serial_number"] = serial,
            ["model_number"] = model,
        });

    private static void AssertFailure(
        PairedLighthouseDeviceDiscoveryResult result,
        PairedLighthouseDeviceDiagnosticCode expectedCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.DiagnosticCode);
        Assert.Empty(result.Devices);
        Assert.NotNull(result.FailurePath);
    }

    private sealed class PairedDiscoveryFixture : IDisposable
    {
        private readonly SteamVrLifecycleFixture _paths = new();

        public PairedDiscoveryFixture(bool createLighthouseDirectory = true)
        {
            LighthouseDirectory = FileSystem.GetCanonicalPath(
                Path.Combine(_paths.ConfigRoot, "lighthouse"));
            if (createLighthouseDirectory)
            {
                FileSystem.AddDirectory(LighthouseDirectory);
            }

            Discovery = new PairedLighthouseDeviceDiscovery(
                new SteamVrPathDiscovery(
                    new FakeSteamVrHostEnvironment
                    {
                        LocalApplicationDataPath = _paths.LocalApplicationData,
                    },
                    _paths.FileSystem),
                FileSystem);
        }

        public MemoryPairedLighthouseDeviceFileSystem FileSystem { get; } = new();

        public PairedLighthouseDeviceDiscovery Discovery { get; }

        public string LighthouseDirectory { get; }

        public string AddConfig(string directoryName, string json)
        {
            var deviceDirectory = AddDeviceDirectory(directoryName);
            var configFile = FileSystem.GetCanonicalPath(
                Path.Combine(deviceDirectory, "config.json"));
            FileSystem.AddFile(configFile, json);
            return configFile;
        }

        public string AddDeviceDirectory(string directoryName)
        {
            var deviceDirectory = FileSystem.GetCanonicalPath(
                Path.Combine(LighthouseDirectory, directoryName));
            FileSystem.AddDirectory(deviceDirectory);
            return deviceDirectory;
        }

        public void Dispose()
        {
            _paths.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private sealed class MemoryPairedLighthouseDeviceFileSystem :
        IPairedLighthouseDeviceFileSystem
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string? UnreadableFile { get; set; }

        public bool RefuseDirectoryEnumeration { get; set; }

        public string GetCanonicalPath(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        public bool DirectoryExists(string path) =>
            _directories.Contains(GetCanonicalPath(path));

        public IReadOnlyList<string> EnumerateDirectories(string path)
        {
            if (RefuseDirectoryEnumeration)
            {
                throw new UnauthorizedAccessException("Scripted directory access failure.");
            }

            var canonicalParent = GetCanonicalPath(path);
            return _directories
                .Where(candidate => string.Equals(
                    Path.GetDirectoryName(candidate),
                    canonicalParent,
                    StringComparison.Ordinal))
                .Reverse()
                .ToArray();
        }

        public bool FileExists(string path) =>
            _files.ContainsKey(GetCanonicalPath(path));

        public ValueTask<string> ReadAllTextAsync(
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalPath = GetCanonicalPath(path);
            if (string.Equals(
                    canonicalPath,
                    UnreadableFile,
                    StringComparison.Ordinal))
            {
                throw new IOException("Scripted config read failure.");
            }

            return ValueTask.FromResult(_files[canonicalPath]);
        }

        public void AddDirectory(string path) =>
            _directories.Add(GetCanonicalPath(path));

        public void AddFile(string path, string json) =>
            _files.Add(GetCanonicalPath(path), json);
    }
}
