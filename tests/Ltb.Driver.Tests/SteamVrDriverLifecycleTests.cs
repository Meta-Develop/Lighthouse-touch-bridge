using System.Text.Json.Nodes;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class SteamVrDriverLifecycleTests
{
    [Fact]
    public async Task RegisterAddsOnlyCanonicalStagedRootAndEnablesMultipleDrivers()
    {
        using var fixture = new SteamVrLifecycleFixture();

        var result = await fixture.Lifecycle.RegisterAsync(
            Path.Combine(fixture.StagedDriverRoot, "."));

        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.RestartRequired, result.Readiness);
        Assert.Contains("restart SteamVR", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(SteamVrLifecycleFixture.BuildId, result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(
            [fixture.OtherDriverRoot, Path.GetFullPath(fixture.StagedDriverRoot)],
            fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            result.Receipt.PriorActivateMultipleDrivers);
        var call = Assert.Single(fixture.ProcessRunner.Calls);
        Assert.Equal(fixture.VrPathRegExecutable, call.Executable);
        Assert.Equal("adddriver", call.Verb);
        Assert.Equal(Path.GetFullPath(fixture.StagedDriverRoot), call.DriverRoot);
    }

    [Theory]
    [InlineData(true, false, SteamVrDriverDiagnosticCode.StagedManifestMissing)]
    [InlineData(false, true, SteamVrDriverDiagnosticCode.StagedBinaryMissing)]
    public async Task RegisterRejectsSourceOrBuildDirectoryWithoutCompleteStagedLayout(
        bool omitManifest,
        bool omitBinary,
        SteamVrDriverDiagnosticCode expectedCode)
    {
        using var fixture = new SteamVrLifecycleFixture();
        var unstagedRoot = Path.Combine(fixture.Root, "source-or-build");
        if (!omitManifest)
        {
            fixture.FileSystem.AddFile(Path.Combine(unstagedRoot, "driver.vrdrivermanifest"));
        }

        if (!omitBinary)
        {
            fixture.FileSystem.AddFile(
                Path.Combine(unstagedRoot, "bin", "win64", "driver_ltb.dll"));
        }

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(unstagedRoot).AsTask());

        Assert.Equal(expectedCode, failure.DiagnosticCode);
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RegisterRequiresStagedBuildIdentityMarker()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var unstagedRoot = Path.Combine(fixture.Root, "stage-without-build-id");
        fixture.FileSystem.AddFile(
            Path.Combine(unstagedRoot, SteamVrDriverLifecycle.DriverManifestRelativePath));
        fixture.FileSystem.AddFile(
            Path.Combine(unstagedRoot, SteamVrDriverLifecycle.DriverBinaryRelativePath));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(unstagedRoot).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.StagedBuildIdMissing, failure.DiagnosticCode);
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Theory]
    [InlineData("")]
    [InlineData("driver_ltb-0.1-ipc-1.0")]
    [InlineData("driver_ltb-0.1.0-ipc-1")]
    [InlineData(" driver_ltb-0.1.0-ipc-1.0\n")]
    [InlineData("driver_ltb-0.1.0-ipc-1.0\nsecond-line\n")]
    public async Task RegisterRejectsMalformedStagedBuildIdentity(string buildIdText)
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(fixture.BuildIdFile, buildIdText);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.StagedBuildIdInvalid, failure.DiagnosticCode);
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task InspectReadsBuildAndRegistrationStateWithoutMutation()
    {
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);

        var inspection = await fixture.Lifecycle.InspectAsync(
            Path.Combine(fixture.StagedDriverRoot, "."));

        Assert.Equal(Path.GetFullPath(fixture.StagedDriverRoot), inspection.CanonicalDriverRoot);
        Assert.Equal(SteamVrLifecycleFixture.BuildId, inspection.StagedBuildId);
        Assert.True(inspection.IsRegistered);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            inspection.ActivateMultipleDrivers);
        Assert.Equal(fixture.OpenVrPathsFile, inspection.Paths.OpenVrPathsFile);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Theory]
    [InlineData("driver_ltb-0.1.0-ipc-1.0")]
    [InlineData("driver_ltb-0.1.0-ipc-1.0\n")]
    [InlineData("driver_ltb-0.1.0-ipc-1.0\r\n")]
    public async Task InspectAcceptsPortableStagedBuildIdentityLineEndings(string buildIdText)
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(fixture.BuildIdFile, buildIdText);

        var inspection = await fixture.Lifecycle.InspectAsync(fixture.StagedDriverRoot);

        Assert.Equal(SteamVrLifecycleFixture.BuildId, inspection.StagedBuildId);
    }

    [Fact]
    public async Task InspectRejectsNonCanonicalEquivalentRegistrationWithoutMutation()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var nonCanonicalEquivalent = Path.Combine(
            fixture.StagedDriverRoot,
            "..",
            Path.GetFileName(fixture.StagedDriverRoot));
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, nonCanonicalEquivalent]));
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.InspectAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task RegisterReportsSettingsOnlyMutationAsRestartRequired()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));

        var result = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);

        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.RestartRequired, result.Readiness);
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task RegisterAndRemoveAreIdempotentAndRestorePriorDisabledSetting()
    {
        using var fixture = new SteamVrLifecycleFixture();

        var firstRegistration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var repeatedRegistration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var removal = await fixture.Lifecycle.RemoveAsync(repeatedRegistration.Receipt);
        var repeatedRemoval = await fixture.Lifecycle.RemoveAsync(repeatedRegistration.Receipt);

        Assert.True(firstRegistration.Changed);
        Assert.False(repeatedRegistration.Changed);
        Assert.False(repeatedRegistration.RestartRequired);
        Assert.Equal(
            SteamVrDriverReadiness.RuntimeVerificationRequired,
            repeatedRegistration.Readiness);
        Assert.DoesNotContain(
            "restart SteamVR",
            repeatedRegistration.Diagnostic,
            StringComparison.Ordinal);
        Assert.Equal(firstRegistration.Receipt, repeatedRegistration.Receipt);
        Assert.True(removal.Changed);
        Assert.True(removal.RestartRequired);
        Assert.False(repeatedRemoval.Changed);
        Assert.False(repeatedRemoval.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.NotRegistered, repeatedRemoval.Readiness);
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Collection(
            fixture.ProcessRunner.Calls,
            call => Assert.Equal("adddriver", call.Verb),
            call => Assert.Equal("removedriver", call.Verb));
    }

    [Fact]
    public async Task RemoveRestoresAbsentSettingAndRemovesCreatedEmptySteamVrSection()
    {
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Absent,
            steamVrSectionPresent: false);
        var before = JsonNode.Parse(fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);

        await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        var after = JsonNode.Parse(fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        Assert.False(after.ContainsKey("steamvr"));
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RemoveRestoresAbsentSettingInsideExistingSteamVrSection()
    {
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Absent,
            steamVrSectionPresent: true);
        var before = JsonNode.Parse(fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);

        await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        var after = JsonNode.Parse(fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        Assert.True(after.ContainsKey("steamvr"));
        Assert.False(after["steamvr"]!.AsObject().ContainsKey("activateMultipleDrivers"));
        Assert.True(after["steamvr"]!.AsObject()["allowAsyncReprojection"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Fact]
    public async Task RemoveLeavesPriorEnabledSettingEnabled()
    {
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled);
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);

        var removal = await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        Assert.False(registration.Receipt.ActivateMultipleDriversChanged);
        Assert.True(removal.Changed);
        Assert.True(removal.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.RestartRequired, removal.Readiness);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RemoveReportsSettingsOnlyRestorationAsRestartRequired()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot]));

        var removal = await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        Assert.True(removal.Changed);
        Assert.True(removal.RestartRequired);
        Assert.Equal(SteamVrDriverReadiness.RestartRequired, removal.Readiness);
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
        Assert.Collection(
            fixture.ProcessRunner.Calls,
            call => Assert.Equal("adddriver", call.Verb));
    }

    [Fact]
    public async Task RegistrationProcessFailureRollsBackPartialExternalDriverMutation()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.ProcessRunner.FailCallNumber = 1;
        fixture.ProcessRunner.MutateBeforeFailure = true;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ProcessFailed, failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
    }

    [Fact]
    public async Task NonCanonicalRegistrationVerificationFailureRollsBackExternalDrivers()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        fixture.ProcessRunner.SkipMutation = true;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task ForgedReceiptCannotRemoveOwnedRegistration()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var forgedReceipt = registration.Receipt with { OwnershipToken = Guid.NewGuid() };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(forgedReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task ReceiptIssuedByAnotherLifecycleCannotRemoveRegistration()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        using var unrelatedLifecycle = new SteamVrDriverLifecycle(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = fixture.LocalApplicationData,
            },
            fixture.FileSystem,
            new FakeVrPathRegRunner(fixture.FileSystem, fixture.OpenVrPathsFile));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => unrelatedLifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Contains(fixture.StagedDriverRoot, fixture.ExternalDrivers());
    }

    [Fact]
    public async Task ReceiptBecomesStaleAfterRemovalAndFreshRegistration()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var firstRegistration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        await fixture.Lifecycle.RemoveAsync(firstRegistration.Receipt);
        var secondRegistration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(firstRegistration.Receipt).AsTask());

        Assert.NotEqual(
            firstRegistration.Receipt.OwnershipToken,
            secondRegistration.Receipt.OwnershipToken);
        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Contains(fixture.StagedDriverRoot, fixture.ExternalDrivers());
    }

    [Fact]
    public async Task CanonicalEquivalentRegistrationOutputIsRejectedAndRolledBack()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        fixture.ProcessRunner.AddedPathOverride = Path.Combine(
            fixture.StagedDriverRoot,
            "..",
            Path.GetFileName(fixture.StagedDriverRoot));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task SettingsOwnershipFailureRollsBackRegisteredDriver()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.FileSystem.RefuseReplaceNumber = 1;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ConcurrentModification, failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
    }

    [Fact]
    public async Task FinalConditionalUpdateRacePreservesConcurrentSettingsAndRollsBackDriver()
    {
        using var fixture = new SteamVrLifecycleFixture();
        fixture.FileSystem.BeforeConditionalCommit = (fileSystem, path) =>
        {
            if (string.Equals(path, fixture.SettingsFile, StringComparison.Ordinal))
            {
                var root = JsonNode.Parse(fileSystem.Read(path))!.AsObject();
                root["concurrentSetting"] = "preserve-me";
                fileSystem.Write(path, root.ToJsonString() + "\n");
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        var settings = JsonNode.Parse(
            fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        Assert.Equal(SteamVrDriverDiagnosticCode.ConcurrentModification, failure.DiagnosticCode);
        Assert.Equal("preserve-me", settings["concurrentSetting"]!.GetValue<string>());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task SettingsPostWriteVerificationFailureRollsBackBothFiles()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.FileSystem.AfterSuccessfulReplace = (fileSystem, path) =>
        {
            if (string.Equals(path, fixture.SettingsFile, StringComparison.Ordinal))
            {
                fileSystem.Write(path, originalSettings);
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
    }

    [Fact]
    public async Task FinalSettingsVerificationFailureRollsBackBothOwnedFiles()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.FileSystem.ThrowReadPath = fixture.SettingsFile;
        fixture.FileSystem.ThrowReadNumber = 3;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
    }

    [Fact]
    public async Task RollbackRestoresOnlyOwnedSettingAndPreservesConcurrentSetting()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var concurrentSettingInjected = false;
        fixture.FileSystem.AfterSuccessfulReplace = (fileSystem, path) =>
        {
            if (!concurrentSettingInjected &&
                string.Equals(path, fixture.SettingsFile, StringComparison.Ordinal))
            {
                concurrentSettingInjected = true;
                var root = JsonNode.Parse(fileSystem.Read(path))!.AsObject();
                root["concurrentSetting"] = "preserve-me";
                fileSystem.Write(path, root.ToJsonString() + "\n");
            }
        };
        fixture.FileSystem.ThrowReadPath = fixture.OpenVrPathsFile;
        fixture.FileSystem.ThrowReadNumber = 4;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        var settings = JsonNode.Parse(
            fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal("preserve-me", settings["concurrentSetting"]!.GetValue<string>());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal([fixture.OtherDriverRoot], fixture.ExternalDrivers());
    }

    [Fact]
    public async Task CancellationAfterExternalDriverMutationRollsBackBeforePropagating()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.FileSystem.CancelReplaceNumber = 1;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
    }

    [Fact]
    public async Task RemovalSettingsFailureRollsBackDriverRemoval()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var registeredOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var registeredSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.FileSystem.RefuseReplaceNumber = 2;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ConcurrentModification, failure.DiagnosticCode);
        Assert.Equal(registeredOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(registeredSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RemovalProcessFailureRollsBackPartialExternalDriverMutation()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var registeredOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var registeredSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.ProcessRunner.FailCallNumber = 2;
        fixture.ProcessRunner.MutateBeforeFailure = true;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ProcessFailed, failure.DiagnosticCode);
        Assert.Equal(registeredOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(registeredSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
    }

    [Fact]
    public async Task ConcurrentDriverAddedBetweenRegistrationProcessAndRereadSurvivesRollback()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var concurrentDriver = Path.Combine(fixture.Root, "drivers", "concurrent-window");
        fixture.ProcessRunner.AfterMutation = (fileSystem, verb, _) =>
        {
            if (verb == "adddriver")
            {
                fileSystem.Write(
                    fixture.OpenVrPathsFile,
                    fixture.OpenVrJson(
                        [fixture.OtherDriverRoot, fixture.StagedDriverRoot, concurrentDriver]));
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, concurrentDriver],
            fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task ConcurrentDriverAddedBetweenRemovalProcessAndRereadSurvivesRollback()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var concurrentDriver = Path.Combine(fixture.Root, "drivers", "concurrent-window");
        fixture.ProcessRunner.AfterMutation = (fileSystem, verb, _) =>
        {
            if (verb == "removedriver")
            {
                fileSystem.Write(
                    fixture.OpenVrPathsFile,
                    fixture.OpenVrJson([fixture.OtherDriverRoot, concurrentDriver]));
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, concurrentDriver, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
    }

    [Fact]
    public async Task ConcurrentExternalChangeDuringRollbackIsPreservedWhileOwnedTargetIsRemoved()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var concurrentDriver = Path.Combine(fixture.Root, "drivers", "concurrent");
        fixture.FileSystem.RefuseReplaceNumber = 1;
        fixture.FileSystem.BeforeRefusedReplace = (fileSystem, path) =>
        {
            if (string.Equals(path, fixture.SettingsFile, StringComparison.Ordinal))
            {
                fileSystem.Write(
                    fixture.OpenVrPathsFile,
                    fixture.OpenVrJson(
                        [fixture.OtherDriverRoot, fixture.StagedDriverRoot, concurrentDriver]));
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ConcurrentModification, failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, concurrentDriver],
            fixture.ExternalDrivers());
    }

    [Fact]
    public async Task RemovalRefusesToOverwriteUnexpectedActivateMultipleDriversChange()
    {
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Absent);
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var root = JsonNode.Parse(fixture.FileSystem.Read(fixture.SettingsFile))!.AsObject();
        root["steamvr"]!.AsObject()["activateMultipleDrivers"] = false;
        fixture.FileSystem.Write(fixture.SettingsFile, root.ToJsonString() + "\n");

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Contains(fixture.StagedDriverRoot, fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
    }
}
