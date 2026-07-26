using System.Text.Json;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class SteamVrPathDiscoveryTests
{
    [Fact]
    public async Task ConfigRootDiscoveryDoesNotRequireRuntimeOrSettingsArtifacts()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ltb-config-root-only"));
        var localApplicationData = Path.Combine(root, "local");
        var configRoot = Path.Combine(root, "steam-config");
        var openVrPathsFile = Path.Combine(
            localApplicationData,
            "openvr",
            "openvrpaths.vrpath");
        var fileSystem = new MemorySteamVrFileSystem();
        fileSystem.AddFile(
            openVrPathsFile,
            JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["config"] = [configRoot],
            }));
        var discovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = localApplicationData,
            },
            fileSystem);

        var result = await discovery.DiscoverConfigRootAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(PairedLighthouseDeviceDiagnosticCode.None, result.DiagnosticCode);
        Assert.Equal(Path.GetFullPath(openVrPathsFile), result.OpenVrPathsFile);
        Assert.Equal(Path.GetFullPath(configRoot), result.ConfigRoot);
    }

    [Fact]
    public async Task ConfigRootsDiscoveryReturnsEveryCanonicalDistinctConfiguredRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "ltb-config-roots"));
        var localApplicationData = Path.Combine(root, "local");
        var first = Path.Combine(root, "config-a");
        var second = Path.Combine(root, "config-b");
        var openVrPathsFile = Path.Combine(
            localApplicationData,
            "openvr",
            "openvrpaths.vrpath");
        var fileSystem = new MemorySteamVrFileSystem();
        fileSystem.AddFile(
            openVrPathsFile,
            JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["config"] = [second, first, first],
            }));
        var discovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = localApplicationData,
            },
            fileSystem);

        var result = await discovery.DiscoverConfigRootsAsync();

        Assert.True(result.IsSuccess, result.Diagnostic);
        Assert.Equal(
            [Path.GetFullPath(first), Path.GetFullPath(second)],
            result.ConfigRoots);
    }

    [Fact]
    public async Task ConfigRootDiscoveryReportsMissingOpenVrRegistryAsTypedResult()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ltb-missing-registry"));
        var discovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = Path.Combine(root, "local"),
            },
            new MemorySteamVrFileSystem());

        var result = await discovery.DiscoverConfigRootAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(
            PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMissing,
            result.DiagnosticCode);
        Assert.NotNull(result.OpenVrPathsFile);
        Assert.Null(result.ConfigRoot);
    }

    [Fact]
    public async Task ConfigRootDiscoveryReportsMissingOrEmptyConfigRootAsTypedResult()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            """
            {
              "config": ["", "   "],
              "runtime": ["/unused"]
            }
            """);
        var discovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = fixture.LocalApplicationData,
            },
            fixture.FileSystem);

        var result = await discovery.DiscoverConfigRootAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(
            PairedLighthouseDeviceDiagnosticCode.ConfigRootUnavailable,
            result.DiagnosticCode);
        Assert.Equal(Path.GetFullPath(fixture.OpenVrPathsFile), result.OpenVrPathsFile);
        Assert.Null(result.ConfigRoot);
    }

    [Fact]
    public async Task ConfigRootDiscoveryDistinguishesMalformedAndUnreadableRegistry()
    {
        using var malformedFixture = new SteamVrLifecycleFixture();
        malformedFixture.FileSystem.Write(
            malformedFixture.OpenVrPathsFile,
            """{ "config": [""");
        var malformedDiscovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = malformedFixture.LocalApplicationData,
            },
            malformedFixture.FileSystem);

        var malformed = await malformedDiscovery.DiscoverConfigRootAsync();

        Assert.Equal(
            PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMalformed,
            malformed.DiagnosticCode);

        using var unreadableFixture = new SteamVrLifecycleFixture();
        unreadableFixture.FileSystem.ThrowReadPath =
            unreadableFixture.FileSystem.GetCanonicalPath(
                unreadableFixture.OpenVrPathsFile);
        unreadableFixture.FileSystem.ThrowReadNumber = 1;
        var unreadableDiscovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = unreadableFixture.LocalApplicationData,
            },
            unreadableFixture.FileSystem);

        var unreadable = await unreadableDiscovery.DiscoverConfigRootAsync();

        Assert.Equal(
            PairedLighthouseDeviceDiagnosticCode.OpenVrPathsUnreadable,
            unreadable.DiagnosticCode);
    }

    [Fact]
    public async Task DiscoversCurrentUserOpenVrRegistryRuntimeAndConfigArrays()
    {
        using var fixture = new SteamVrLifecycleFixture();

        var paths = await fixture.Lifecycle.DiscoverAsync();

        Assert.Equal(Path.GetFullPath(fixture.OpenVrPathsFile), paths.OpenVrPathsFile);
        Assert.Equal(Path.GetFullPath(fixture.RuntimeRoot), paths.RuntimeRoot);
        Assert.Equal(Path.GetFullPath(fixture.ConfigRoot), paths.ConfigRoot);
        Assert.Equal(Path.GetFullPath(fixture.VrPathRegExecutable), paths.VrPathRegExecutable);
        Assert.Equal(Path.GetFullPath(fixture.SettingsFile), paths.SettingsFile);
        Assert.StartsWith(
            Path.GetFullPath(fixture.LocalApplicationData),
            paths.OpenVrPathsFile,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryRefusesNonWindowsHostBeforeReadingPaths()
    {
        var fileSystem = new MemorySteamVrFileSystem();
        using var lifecycle = new SteamVrDriverLifecycle(
            new FakeSteamVrHostEnvironment
            {
                IsWindows = false,
                LocalApplicationDataPath = "/owner/AppData/Local",
            },
            fileSystem,
            new FakeVrPathRegRunner(fileSystem, "/unused/openvrpaths.vrpath"));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => lifecycle.DiscoverAsync().AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.PlatformUnsupported, failure.DiagnosticCode);
    }

    [Fact]
    public async Task DiscoveryRejectsRegistryWithoutDeclaredRuntimeArray()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["config"] = [@"C:\Users\owner\AppData\Local\Steam\config"],
            }));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.DiscoverAsync().AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.OpenVrPathsInvalid, failure.DiagnosticCode);
        Assert.StartsWith(
            "OpenVR path registry is invalid: required 'runtime' array",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            Path.GetFullPath(fixture.OpenVrPathsFile),
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryUsesOnlyTheInjectedLocalApplicationDataRegistry()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var decoyLocalApplicationData = Path.Combine(fixture.Root, "real-os-decoy");
        var decoyOpenVrPathsFile = Path.Combine(
            decoyLocalApplicationData,
            "openvr",
            "openvrpaths.vrpath");
        fixture.FileSystem.AddFile(decoyOpenVrPathsFile, fixture.OpenVrJson([]));
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                ["config"] = [@"C:\Users\owner\AppData\Local\Steam\config"],
            }));

        var discovery = new SteamVrPathDiscovery(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = fixture.LocalApplicationData,
            },
            fixture.FileSystem);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => discovery.DiscoverAsync().AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.OpenVrPathsInvalid, failure.DiagnosticCode);
        Assert.StartsWith(
            "OpenVR path registry is invalid: required 'runtime' array",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            Path.GetFullPath(fixture.OpenVrPathsFile),
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Path.GetFullPath(decoyOpenVrPathsFile),
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryDistinguishesMalformedJsonFromMissingRuntimeArray()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            """
            {
              "config": ["C:\Users\owner\AppData\Local\Steam\config"]
            }
            """);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.DiscoverAsync().AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.OpenVrPathsInvalid, failure.DiagnosticCode);
        Assert.Contains("not valid JSON", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("required 'runtime' array", failure.Message, StringComparison.Ordinal);
    }
}
