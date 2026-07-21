using System.Text.Json;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class SteamVrPathDiscoveryTests
{
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
