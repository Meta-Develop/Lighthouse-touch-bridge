using System.Security.Cryptography;
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
            new SteamVrDriverArtifactIdentity(
                SteamVrLifecycleFixture.BuildId,
                Sha256(fixture.FileSystem.ReadBytes(fixture.ManifestFile)),
                Sha256(fixture.FileSystem.ReadBytes(fixture.BinaryFile)),
                Sha256(fixture.FileSystem.ReadBytes(fixture.BuildIdFile))),
            result.Receipt.ArtifactIdentity);
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
    public async Task StartupInspectionPreservesNamedAndArbitraryUnrelatedDrivers()
    {
        using var fixture = new SteamVrLifecycleFixture();
        string[] unrelatedDrivers =
        [
            Path.Combine(fixture.Root, "drivers", "01spacecalibrator"),
            Path.Combine(fixture.Root, "drivers", "bigscreenbeyond"),
            Path.Combine(fixture.Root, "drivers", "vmt"),
            Path.Combine(fixture.Root, "drivers", "alvr_server"),
            Path.Combine(fixture.Root, "drivers", "arbitrary-third-party"),
        ];
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(unrelatedDrivers));
        var vmtRoot = unrelatedDrivers[2];
        fixture.FileSystem.AddFile(
            Path.Combine(vmtRoot, SteamVrDriverLifecycle.DriverManifestRelativePath),
            """{ "name": "vmt", "resourceOnly": false }""");
        fixture.FileSystem.AddFile(
            Path.Combine(vmtRoot, SteamVrDriverLifecycle.DriverBinaryRelativePath),
            "foreign bytes");
        fixture.FileSystem.AddFile(
            Path.Combine(vmtRoot, SteamVrDriverLifecycle.DriverBuildIdRelativePath),
            SteamVrLifecycleFixture.BuildId + "\n");
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.NoLtbRegistration,
            inspection.State);
        Assert.Empty(inspection.CanonicalLtbDriverRoots);
        Assert.Equal(unrelatedDrivers, inspection.UnrelatedExternalDriverRoots);
        Assert.Equal(
            [
                ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
                ExternalSteamVrIntegrationIdentity.BigscreenBeyond,
                ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
                ExternalSteamVrIntegrationIdentity.AlvrServer,
            ],
            inspection.ExternalIntegrationWarnings.Select(warning => warning.Identity));
        Assert.Equal(
            [
                ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation,
                ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation,
                ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict,
                ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict,
            ],
            inspection.ExternalIntegrationWarnings.Select(warning => warning.Category));
        Assert.Empty(inspection.DurableReceipts);
        Assert.False(inspection.CanRemoveAutomatically);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupWarningsDoNotChangeRegistrationOrRemovalTransactions()
    {
        using var fixture = new SteamVrLifecycleFixture();
        string[] originalDrivers =
        [
            Path.Combine(fixture.Root, "drivers", "vmt"),
            fixture.OtherDriverRoot,
            Path.Combine(fixture.Root, "drivers", "alvr_server"),
        ];
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(originalDrivers));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);
        var registration = await fixture.Lifecycle.RegisterAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(2, inspection.ExternalIntegrationWarnings.Count);
        Assert.Equal(
            [.. originalDrivers, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());

        await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        Assert.Equal(originalDrivers, fixture.ExternalDrivers());
    }

    [Fact]
    public async Task StartupInspectionFindsOneReceiptOwnedRegistration()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var receipt = Receipt(fixture.StagedDriverRoot);
        store.Save(receipt);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.ReceiptOwnedRegistration,
            inspection.State);
        Assert.Equal([fixture.StagedDriverRoot], inspection.CanonicalLtbDriverRoots);
        Assert.Equal([fixture.OtherDriverRoot], inspection.UnrelatedExternalDriverRoots);
        Assert.Equal([receipt], inspection.DurableReceipts);
        Assert.Equal(receipt, inspection.MatchingReceipt);
        Assert.True(inspection.CanRemoveAutomatically);
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupInspectionRefusesReceiptOwnedRootWithMissingArtifacts()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        var receipt = Receipt(relocatedRoot);
        store.Save(receipt);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, relocatedRoot]));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.StaleReceiptRegistrationMismatch,
            inspection.State);
        Assert.Empty(inspection.CanonicalLtbDriverRoots);
        Assert.Null(inspection.MatchingReceipt);
        Assert.False(inspection.CanRemoveAutomatically);
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupInspectionDistinguishesReceiptlessAndReceiptOnlyStates()
    {
        using (var receiptless = new SteamVrLifecycleFixture())
        {
            receiptless.FileSystem.Write(
                receiptless.OpenVrPathsFile,
                receiptless.OpenVrJson(
                    [receiptless.OtherDriverRoot, receiptless.StagedDriverRoot]));

            var inspection = await receiptless.Lifecycle.InspectStartupAsync(
                receiptless.StagedDriverRoot);

            Assert.Equal(
                SteamVrDriverStartupState.ReceiptlessArtifactProvenRegistration,
                inspection.State);
            Assert.True(inspection.CanRemoveAutomatically);
            Assert.Null(inspection.MatchingReceipt);
        }

        var store = new MemorySteamVrDriverReceiptStore();
        using var receiptOnly = new SteamVrLifecycleFixture(receiptStore: store);
        var receipt = Receipt(receiptOnly.StagedDriverRoot);
        store.Save(receipt);

        var receiptOnlyInspection = await receiptOnly.Lifecycle.InspectStartupAsync(
            receiptOnly.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.ReceiptOnlyNoRegistration,
            receiptOnlyInspection.State);
        Assert.Empty(receiptOnlyInspection.CanonicalLtbDriverRoots);
        Assert.Equal([receipt], receiptOnlyInspection.DurableReceipts);
        Assert.True(receiptOnlyInspection.CanRemoveAutomatically);
    }

    [Fact]
    public async Task StartupInspectionRefusesMismatchedOrExtraReceiptState()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        store.Save(Receipt(relocatedRoot));
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.StaleReceiptRegistrationMismatch,
            inspection.State);
        Assert.Equal([fixture.StagedDriverRoot], inspection.CanonicalLtbDriverRoots);
        Assert.False(inspection.CanRemoveAutomatically);
        Assert.Null(inspection.MatchingReceipt);
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupInspectionRefusesByteDriftAgainstIdentityBoundReceipt()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        fixture.ProcessRunner.Calls.Clear();
        fixture.FileSystem.Write(fixture.BinaryFile, "drifted driver bytes");

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.StaleReceiptRegistrationMismatch,
            inspection.State);
        Assert.False(inspection.CanRemoveAutomatically);
        Assert.Null(inspection.MatchingReceipt);
        Assert.Equal(
            registration.Receipt,
            Assert.Single(inspection.DurableReceipts));
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task LegacyForeignManifestRefusesRemovalWithoutMutation()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var legacyReceipt = Receipt(fixture.StagedDriverRoot);
        store.Save(legacyReceipt);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));
        fixture.FileSystem.Write(
            fixture.ManifestFile,
            SteamVrLifecycleFixture.DriverManifestJson.Replace(
                "\"name\": \"ltb\"",
                "\"name\": \"foreign\"",
                StringComparison.Ordinal));
        var originalOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var originalSettings = fixture.FileSystem.Read(fixture.SettingsFile);

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(legacyReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(originalOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(originalSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal(legacyReceipt, store.TryLoad(fixture.StagedDriverRoot));
    }

    [Fact]
    public async Task StartupInspectionSeparatesExactDuplicatesFromAliasAmbiguity()
    {
        using (var duplicate = new SteamVrLifecycleFixture())
        {
            duplicate.FileSystem.Write(
                duplicate.OpenVrPathsFile,
                duplicate.OpenVrJson(
                    [duplicate.StagedDriverRoot, duplicate.StagedDriverRoot]));
            var originalOpenVr = duplicate.FileSystem.Read(duplicate.OpenVrPathsFile);

            var duplicateInspection =
                await duplicate.Lifecycle.InspectStartupAsync(duplicate.StagedDriverRoot);

            Assert.Equal(
                SteamVrDriverStartupState.DuplicateRegistrations,
                duplicateInspection.State);
            Assert.True(duplicateInspection.CanRemoveAutomatically);
            Assert.Equal(originalOpenVr, duplicate.FileSystem.Read(duplicate.OpenVrPathsFile));
            Assert.Empty(duplicate.ProcessRunner.Calls);
        }

        using var alias = new SteamVrLifecycleFixture();
        var nonCanonicalEquivalent = Path.Combine(
            alias.StagedDriverRoot,
            "..",
            Path.GetFileName(alias.StagedDriverRoot));
        alias.FileSystem.Write(
            alias.OpenVrPathsFile,
            alias.OpenVrJson([nonCanonicalEquivalent]));
        var originalAliasOpenVr = alias.FileSystem.Read(alias.OpenVrPathsFile);

        var aliasInspection =
            await alias.Lifecycle.InspectStartupAsync(alias.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.AmbiguousNonCanonicalRegistration,
            aliasInspection.State);
        Assert.False(aliasInspection.CanRemoveAutomatically);
        Assert.Equal(originalAliasOpenVr, alias.FileSystem.Read(alias.OpenVrPathsFile));
        Assert.Empty(alias.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupInspectionDetectsMultipleCanonicalLtbRoots()
    {
        using var fixture = new SteamVrLifecycleFixture();
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        fixture.AddCompleteLtbDriver(
            relocatedRoot,
            "driver_ltb-0.0.9-ipc-1.0");
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.StagedDriverRoot, relocatedRoot]));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.DuplicateRegistrations,
            inspection.State);
        Assert.Equal(
            [fixture.StagedDriverRoot, relocatedRoot],
            inspection.CanonicalLtbDriverRoots);
        Assert.True(inspection.CanRemoveAutomatically);
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task StartupInspectionRefusesDuplicateRootsWithConflictingSettingsReceipts()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        fixture.AddCompleteLtbDriver(relocatedRoot);
        var currentReceipt = Receipt(fixture.StagedDriverRoot);
        var relocatedReceipt = Receipt(relocatedRoot);
        store.Save(currentReceipt);
        store.Save(relocatedReceipt);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.StagedDriverRoot, relocatedRoot]));

        var inspection = await fixture.Lifecycle.InspectStartupAsync(
            fixture.StagedDriverRoot);

        Assert.Equal(
            SteamVrDriverStartupState.DuplicateRegistrations,
            inspection.State);
        Assert.False(inspection.CanRemoveAutomatically);
        Assert.Contains(
            "restoration authority is ambiguous",
            inspection.Diagnostic,
            StringComparison.Ordinal);
        Assert.Empty(fixture.ProcessRunner.Calls);
    }

    [Fact]
    public async Task BatchCleanupRemovesEveryAuthorizedRootAndPreservesUnrelatedOrder()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        fixture.AddCompleteLtbDriver(relocatedRoot);
        var currentReceipt = Receipt(fixture.StagedDriverRoot);
        var relocatedReceipt = new SteamVrDriverRegistrationReceipt(
            relocatedRoot,
            SteamVrActivateMultipleDriversState.Enabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.Save(currentReceipt);
        store.Save(relocatedReceipt);
        string[] unrelatedDrivers =
        [
            Path.Combine(fixture.Root, "drivers", "01spacecalibrator"),
            Path.Combine(fixture.Root, "drivers", "bigscreenbeyond"),
            Path.Combine(fixture.Root, "drivers", "vmt"),
            Path.Combine(fixture.Root, "drivers", "alvr_server"),
        ];
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(
            [
                unrelatedDrivers[0],
                fixture.StagedDriverRoot,
                unrelatedDrivers[1],
                relocatedRoot,
                unrelatedDrivers[2],
                unrelatedDrivers[3],
            ]));

        var result = await fixture.Lifecycle.RemoveOwnedAsync(
            [currentReceipt, relocatedReceipt]);

        Assert.True(result.Changed);
        Assert.True(result.RestartRequired);
        Assert.Equal(
            SteamVrDriverReadiness.RestartRequired,
            result.Readiness);
        Assert.Equal(unrelatedDrivers, fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Disabled,
            fixture.ActivateMultipleDrivers());
        Assert.Empty(store.LoadAll());
        Assert.Equal(
            [fixture.StagedDriverRoot, relocatedRoot],
            result.CanonicalDriverRoots);
        Assert.Contains(
            "already-loaded devices do not disappear live",
            result.Diagnostic,
            StringComparison.Ordinal);
        Assert.Collection(
            fixture.ProcessRunner.Calls,
            call =>
            {
                Assert.Equal("removedriver", call.Verb);
                Assert.Equal(fixture.StagedDriverRoot, call.DriverRoot);
            },
            call =>
            {
                Assert.Equal("removedriver", call.Verb);
                Assert.Equal(relocatedRoot, call.DriverRoot);
            });
    }

    [Fact]
    public async Task BatchCleanupFailureRestoresAllRootsAndRetainsReceipts()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        fixture.AddCompleteLtbDriver(relocatedRoot);
        var currentReceipt = Receipt(fixture.StagedDriverRoot);
        var relocatedReceipt = new SteamVrDriverRegistrationReceipt(
            relocatedRoot,
            SteamVrActivateMultipleDriversState.Enabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.Save(currentReceipt);
        store.Save(relocatedReceipt);
        var originalDrivers = new[]
        {
            fixture.OtherDriverRoot,
            fixture.StagedDriverRoot,
            relocatedRoot,
        };
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(originalDrivers));
        fixture.ProcessRunner.FailCallNumber = 2;
        fixture.ProcessRunner.MutateBeforeFailure = true;

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveOwnedAsync(
                [currentReceipt, relocatedReceipt]).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.ProcessFailed, failure.DiagnosticCode);
        Assert.Equal(originalDrivers, fixture.ExternalDrivers());
        Assert.Equal(
            SteamVrActivateMultipleDriversState.Enabled,
            fixture.ActivateMultipleDrivers());
        Assert.Equal(
            [currentReceipt, relocatedReceipt],
            store.LoadAll());
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
    public async Task RemovePreservesEveryNamedUnrelatedDriverInOriginalOrder()
    {
        using var fixture = new SteamVrLifecycleFixture();
        string[] unrelatedDrivers =
        [
            Path.Combine(fixture.Root, "drivers", "01spacecalibrator"),
            Path.Combine(fixture.Root, "drivers", "bigscreenbeyond"),
            Path.Combine(fixture.Root, "drivers", "vmt"),
            Path.Combine(fixture.Root, "drivers", "alvr_server"),
            Path.Combine(fixture.Root, "drivers", "arbitrary-third-party"),
        ];
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(unrelatedDrivers));
        var registration = await fixture.Lifecycle.RegisterAsync(
            fixture.StagedDriverRoot);

        var removal = await fixture.Lifecycle.RemoveAsync(registration.Receipt);

        Assert.True(removal.Changed);
        Assert.True(removal.RestartRequired);
        Assert.Contains("restart SteamVR", removal.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(
            "already-loaded devices do not disappear live",
            removal.Diagnostic,
            StringComparison.Ordinal);
        Assert.Equal(unrelatedDrivers, fixture.ExternalDrivers());
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
    public async Task PostRemovalArtifactDriftRollsBackAndRetainsReceipt()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(receiptStore: store);
        var registration = await fixture.Lifecycle.RegisterAsync(fixture.StagedDriverRoot);
        var registeredOpenVr = fixture.FileSystem.Read(fixture.OpenVrPathsFile);
        var registeredSettings = fixture.FileSystem.Read(fixture.SettingsFile);
        fixture.ProcessRunner.AfterMutation = (fileSystem, verb, _) =>
        {
            if (verb == "removedriver")
            {
                fileSystem.Write(fixture.BinaryFile, "drifted driver bytes");
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(registration.Receipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(registeredOpenVr, fixture.FileSystem.Read(fixture.OpenVrPathsFile));
        Assert.Equal(registeredSettings, fixture.FileSystem.Read(fixture.SettingsFile));
        Assert.Equal(
            registration.Receipt,
            store.TryLoad(registration.Receipt.CanonicalDriverRoot));
    }

    [Fact]
    public async Task LegacyReceiptComparesExactEphemeralProofAfterRemoval()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var legacyReceipt = new SteamVrDriverRegistrationReceipt(
            fixture.StagedDriverRoot,
            SteamVrActivateMultipleDriversState.Enabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.Save(legacyReceipt);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson([fixture.OtherDriverRoot, fixture.StagedDriverRoot]));
        fixture.ProcessRunner.AfterMutation = (fileSystem, verb, _) =>
        {
            if (verb == "removedriver")
            {
                fileSystem.Write(fixture.BinaryFile, "different valid binary bytes");
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveAsync(legacyReceipt).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot],
            fixture.ExternalDrivers());
        Assert.Equal(legacyReceipt, store.TryLoad(fixture.StagedDriverRoot));
        Assert.Null(legacyReceipt.ArtifactIdentity);
    }

    [Fact]
    public async Task LegacyBatchComparesEveryExactEphemeralProofAfterRemoval()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var relocatedRoot = Path.Combine(fixture.Root, "old-package", "driver_ltb");
        fixture.AddCompleteLtbDriver(relocatedRoot);
        var currentReceipt = Receipt(fixture.StagedDriverRoot);
        var relocatedReceipt = new SteamVrDriverRegistrationReceipt(
            relocatedRoot,
            SteamVrActivateMultipleDriversState.Enabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.Save(currentReceipt);
        store.Save(relocatedReceipt);
        var originalDrivers = new[]
        {
            fixture.OtherDriverRoot,
            fixture.StagedDriverRoot,
            relocatedRoot,
        };
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(originalDrivers));
        fixture.ProcessRunner.AfterMutation = (fileSystem, verb, driverRoot) =>
        {
            if (verb == "removedriver" &&
                string.Equals(
                    driverRoot,
                    fixture.StagedDriverRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                fileSystem.Write(fixture.BinaryFile, "different valid binary bytes");
            }
        };

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveOwnedAsync(
                [currentReceipt, relocatedReceipt]).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Equal(originalDrivers, fixture.ExternalDrivers());
        Assert.Equal([currentReceipt, relocatedReceipt], store.LoadAll());
        Assert.All(store.LoadAll(), receipt => Assert.Null(receipt.ArtifactIdentity));
    }

    [Fact]
    public async Task BatchValidatesEveryArtifactBeforeFirstExternalCall()
    {
        var store = new MemorySteamVrDriverReceiptStore();
        using var fixture = new SteamVrLifecycleFixture(
            SteamVrActivateMultipleDriversState.Enabled,
            receiptStore: store);
        var missingRoot = Path.Combine(fixture.Root, "missing-package", "driver_ltb");
        var first = Receipt(fixture.StagedDriverRoot);
        var missing = new SteamVrDriverRegistrationReceipt(
            missingRoot,
            SteamVrActivateMultipleDriversState.Enabled,
            ActivateMultipleDriversChanged: false,
            SteamVrSectionWasPresent: true,
            Guid.NewGuid());
        store.Save(first);
        store.Save(missing);
        fixture.FileSystem.Write(
            fixture.OpenVrPathsFile,
            fixture.OpenVrJson(
                [fixture.OtherDriverRoot, fixture.StagedDriverRoot, missingRoot]));

        var failure = await Assert.ThrowsAsync<SteamVrDriverLifecycleException>(
            () => fixture.Lifecycle.RemoveOwnedAsync([first, missing]).AsTask());

        Assert.Equal(SteamVrDriverDiagnosticCode.RemovalOwnershipLost, failure.DiagnosticCode);
        Assert.Empty(fixture.ProcessRunner.Calls);
        Assert.Equal(
            [fixture.OtherDriverRoot, fixture.StagedDriverRoot, missingRoot],
            fixture.ExternalDrivers());
        Assert.Equal([first, missing], store.LoadAll());
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

    private static SteamVrDriverRegistrationReceipt Receipt(string canonicalRoot) => new(
        canonicalRoot,
        SteamVrActivateMultipleDriversState.Disabled,
        ActivateMultipleDriversChanged: true,
        SteamVrSectionWasPresent: true,
        Guid.NewGuid());

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
