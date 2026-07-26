using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Driver;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverPreSessionTests
{
    [Fact]
    public async Task NoManualBindingPreservesAutomaticAssociationStartBehavior()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();

        var result = await control.PrepareStartAsync();

        Assert.True(result.CanStart);
        Assert.False(result.HasManualBinding);
        Assert.Contains("automatic", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Theory]
    [InlineData(true, false, "vrserver")]
    [InlineData(false, true, "vrmonitor")]
    [InlineData(true, true, "vrserver")]
    public async Task ManualBindingRequiresBothSteamVrProcessesStopped(
        bool vrServerRunning,
        bool vrMonitorRunning,
        string expectedProcess)
    {
        using var fixture = new Fixture();
        fixture.Processes.Snapshot = new InternalDriverSteamVrProcessSnapshot(
            vrServerRunning,
            vrMonitorRunning);
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        var before = File.ReadAllBytes(fixture.Paths.SettingsPath);

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.SteamVrMustBeStopped,
            result.State);
        Assert.Contains(expectedProcess, result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("No SteamVR settings were written", result.Diagnostic);
        Assert.Equal(before, File.ReadAllBytes(fixture.Paths.SettingsPath));
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Fact]
    public async Task StoppedManualBindingBlocksAtUnresolvedRegisteredPathWithoutGuessing()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        var steamVrSettings = Path.Combine(fixture.Root, "steamvr.vrsettings");
        File.WriteAllText(steamVrSettings, "{\"sentinel\":true}");
        var before = File.ReadAllBytes(steamVrSettings);

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.RegisteredDevicePathUnresolved,
            result.State);
        Assert.Contains("config serial/model", result.Remediation, StringComparison.Ordinal);
        Assert.Contains("No steamvr.vrsettings write", result.Diagnostic);
        Assert.Contains(
            "no /devices/lighthouse/<serial> path was synthesized",
            result.Diagnostic,
            StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(steamVrSettings));
    }

    [Fact]
    public async Task TypedPairingFailureReturnsSnapshotInsteadOfThrowing()
    {
        using var fixture = new Fixture
        {
            DiscoveryResult = new PairedLighthouseDeviceDiscoveryResult(
                PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMalformed,
                "openvrpaths is malformed",
                Array.Empty<PairedLighthouseDevice>(),
                "openvrpaths.vrpath"),
        };
        await using var control = fixture.CreateControl();

        var result = await control.RefreshAsync();

        Assert.Equal(
            InternalDriverPreSessionState.TrackerDiscoveryFailed,
            result.State);
        Assert.Empty(result.PairedTrackers);
        Assert.Contains("malformed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("No exception escaped", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoManualBindingDiscoveryFailureKeepsAutomaticStartReadyAndDiagnostic()
    {
        using var fixture = new Fixture
        {
            DiscoveryResult = PairingFailure(),
        };
        await using var control = fixture.CreateControl();

        var result = await control.PrepareStartAsync();

        Assert.Equal(InternalDriverPreSessionState.Ready, result.State);
        Assert.True(result.CanStart);
        Assert.False(result.HasManualBinding);
        Assert.Contains("automatic", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openvrpaths is malformed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("No exception escaped", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Fact]
    public async Task ManualBindingDiscoveryFailureStillBlocksStart()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        fixture.DiscoveryResult = PairingFailure();

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.TrackerDiscoveryFailed,
            result.State);
        Assert.False(result.CanStart);
        Assert.True(result.HasManualBinding);
        Assert.Contains("openvrpaths is malformed", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Fact]
    public async Task RegistrationSafetyFailureOverridesNoBindingDiscoveryCompatibility()
    {
        using var fixture = new Fixture
        {
            DiscoveryResult = PairingFailure(),
        };
        fixture.Maintenance.Inspection = fixture.Inspection(
            SteamVrDriverStartupState.StaleReceiptRegistrationMismatch,
            canRemoveAutomatically: false,
            "Receipt and registered root disagree.");
        await using var control = fixture.CreateControl();

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.RegistrationStateRequiresAction,
            result.State);
        Assert.False(result.CanStart);
        Assert.False(result.HasManualBinding);
        Assert.Equal(
            SteamVrDriverStartupState.StaleReceiptRegistrationMismatch,
            result.RegistrationState);
        Assert.Contains("Receipt and registered root disagree", result.Diagnostic);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Fact]
    public async Task SafeDuplicateRegistrationsAreRemovedBeforeStartAndRequireRestart()
    {
        using var fixture = new Fixture();
        fixture.Maintenance.Inspection = fixture.Inspection(
            SteamVrDriverStartupState.DuplicateRegistrations,
            canRemoveAutomatically: true,
            "Every duplicate root has independent LTB authority.");
        fixture.Maintenance.Removal = new InternalDriverRemovalResult(
            Changed: true,
            RestartRequired: true,
            "Removed two exact receipt-owned roots.");
        await using var control = fixture.CreateControl();

        var result = await control.PrepareStartAsync();

        Assert.Equal(InternalDriverPreSessionState.RestartRequired, result.State);
        Assert.False(result.CanStart);
        Assert.Equal(1, fixture.Maintenance.RemoveCalls);
        Assert.Contains("transactionally", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("no unrelated", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeDuplicateRegistrationsFailClosedWithoutRemoval()
    {
        using var fixture = new Fixture();
        fixture.Maintenance.Inspection = fixture.Inspection(
            SteamVrDriverStartupState.DuplicateRegistrations,
            canRemoveAutomatically: false,
            "One duplicate root lacks independent authority.");
        await using var control = fixture.CreateControl();

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.RegistrationStateRequiresAction,
            result.State);
        Assert.False(result.CanStart);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
        Assert.Contains("lacks independent authority", result.Diagnostic);
    }

    [Fact]
    public async Task ReceiptOnlyCrashStateIsSurfacedBeforeProceeding()
    {
        using var fixture = new Fixture();
        fixture.Maintenance.Inspection = fixture.Inspection(
            SteamVrDriverStartupState.ReceiptOnlyNoRegistration,
            canRemoveAutomatically: true,
            "Crash left one receipt without a registration.");
        await using var control = fixture.CreateControl();

        var result = await control.RefreshAsync();

        Assert.Equal(
            SteamVrDriverStartupState.ReceiptOnlyNoRegistration,
            result.RegistrationState);
        Assert.Contains("Crash left one receipt", result.Diagnostic);
    }

    [Fact]
    public async Task DefaultControlledStopRemovesOwnedRegistrationAndReportsRunningRestart()
    {
        using var fixture = new Fixture();
        fixture.Processes.Snapshot = new InternalDriverSteamVrProcessSnapshot(
            VrServerRunning: true,
            VrMonitorRunning: false);
        fixture.Maintenance.Inspection = fixture.Inspection(
            SteamVrDriverStartupState.ReceiptOwnedRegistration,
            canRemoveAutomatically: true,
            "Exact receipt-owned registration.");
        fixture.Maintenance.Removal = new InternalDriverRemovalResult(
            Changed: true,
            RestartRequired: true,
            "Removed exact driver_ltb registration.");
        await using var control = fixture.CreateControl();
        _ = await control.RefreshAsync();

        var result = await control.CompleteControlledStopAsync();

        Assert.Equal(InternalDriverPreSessionState.RestartRequired, result.State);
        Assert.True(result.UnregisterOnExit);
        Assert.Equal(1, fixture.Maintenance.RemoveCalls);
        Assert.Contains("only after SteamVR restarts", result.Diagnostic);
        Assert.Contains("does not remove already-published devices live", result.Diagnostic);
    }

    [Fact]
    public async Task UnregisterOptOutSkipsLifecycleMutation()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SetUnregisterOnExitAsync(enabled: false);

        var result = await control.CompleteControlledStopAsync();

        Assert.Equal(InternalDriverPreSessionState.CleanupSkipped, result.State);
        Assert.False(result.UnregisterOnExit);
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
        Assert.Contains("retained", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitCorrectionAndRetainDecisionsUseCoreSelectionAndPersistChoice()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        var verification = new InternalDriverManualBindingVerificationEvidence(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            "LHR-LEFT",
            "LHR-RIGHT",
            "Correlation suggests the reverse pair.",
            "LHR-RIGHT",
            "LHR-LEFT");

        _ = await control.ApplyManualBindingDecisionAsync(
            verification,
            InternalDriverManualBindingDecision.RetainManualBinding);
        var retained = InternalDriverSettingsFile.Load(fixture.Paths.SettingsPath);
        Assert.Equal("LHR-LEFT", retained.ManualTrackerBinding?.LeftTrackerSerial);
        Assert.Equal("LHR-RIGHT", retained.ManualTrackerBinding?.RightTrackerSerial);

        var accepted = await control.ApplyManualBindingDecisionAsync(
            verification,
            InternalDriverManualBindingDecision.AcceptCorrectionCandidate);
        var corrected = InternalDriverSettingsFile.Load(fixture.Paths.SettingsPath);
        Assert.Equal("LHR-RIGHT", corrected.ManualTrackerBinding?.LeftTrackerSerial);
        Assert.Equal("LHR-LEFT", corrected.ManualTrackerBinding?.RightTrackerSerial);
        Assert.Contains("Accepted", accepted.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualAssociationMismatchMapsToTypedSessionEvidenceWithoutReassignment()
    {
        var authoritative = new ManualTrackerBinding("lhr-left", "lhr-right");
        var correction = new ManualTrackerBinding("lhr-right", "lhr-left");
        var core = new ManualTrackerBindingVerificationResult(
            ManualTrackerBindingVerificationStatus.MismatchCorrectionCandidate,
            "Manual binding remains authoritative; correlation suggests a swap.",
            authoritative,
            correction,
            CorrelationResult: null);

        var evidence =
            ProductionInternalDriverSessionRuntime.ToManualBindingVerificationEvidence(core);

        Assert.Equal(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            evidence.State);
        Assert.Equal("LHR-LEFT", evidence.LeftTrackerSerial);
        Assert.Equal("LHR-RIGHT", evidence.RightTrackerSerial);
        Assert.Equal("LHR-RIGHT", evidence.CorrectionLeftTrackerSerial);
        Assert.Equal("LHR-LEFT", evidence.CorrectionRightTrackerSerial);
        Assert.True(evidence.RequiresDecision);
    }

    private static PairedLighthouseDeviceDiscoveryResult PairingFailure() =>
        new(
            PairedLighthouseDeviceDiagnosticCode.OpenVrPathsMalformed,
            "openvrpaths is malformed",
            Array.Empty<PairedLighthouseDevice>(),
            "openvrpaths.vrpath");

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"ltb-pre-session-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new InternalDriverResolvedPaths(
                Path.Combine(Root, "settings", "internal-driver.json"),
                Path.Combine(Root, "profiles", "calibration-profiles.json"),
                Path.Combine(Root, "package", "driver_ltb"),
                Path.Combine(Root, "logs", "internal-driver.jsonl"),
                Path.Combine(Root, "driver", "registration-receipts.json"));
            Maintenance.Inspection = Inspection(
                SteamVrDriverStartupState.NoLtbRegistration,
                canRemoveAutomatically: false,
                "No LTB registration or stale receipt.");
        }

        public string Root { get; }

        public InternalDriverResolvedPaths Paths { get; }

        public MutableProcessInspector Processes { get; } = new();

        public FakeMaintenance Maintenance { get; } = new();

        public PairedLighthouseDeviceDiscoveryResult DiscoveryResult { get; set; } =
            new(
                PairedLighthouseDeviceDiagnosticCode.None,
                "Discovered two paired generic Lighthouse trackers.",
                new[]
                {
                    new PairedLighthouseDevice("lhr-left", "Vive Tracker"),
                    new PairedLighthouseDevice("lhr-right", "Vive Tracker"),
                });

        public InternalDriverPreSessionControl CreateControl() => new(
            Paths,
            new FakeDiscovery(() => DiscoveryResult),
            Processes,
            () => Maintenance);

        public SteamVrDriverStartupInspection Inspection(
            SteamVrDriverStartupState state,
            bool canRemoveAutomatically,
            string diagnostic) => new(
            new SteamVrPaths(
                Path.Combine(Root, "openvrpaths.vrpath"),
                Path.Combine(Root, "runtime"),
                Path.Combine(Root, "config"),
                Path.Combine(Root, "runtime", "vrpathreg.exe"),
                Path.Combine(Root, "config", "steamvr.vrsettings")),
            Paths.StagedDriverRoot,
            "driver_ltb-0.1.0-ipc-1.0",
            state,
            SteamVrActivateMultipleDriversState.Enabled,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<SteamVrDriverRegistrationReceipt>(),
            MatchingReceipt: null,
            canRemoveAutomatically,
            diagnostic);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeDiscovery(
        Func<PairedLighthouseDeviceDiscoveryResult> result) :
        IInternalDriverPairedTrackerDiscovery
    {
        public ValueTask<PairedLighthouseDeviceDiscoveryResult> DiscoverAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(result());
    }

    private sealed class MutableProcessInspector :
        IInternalDriverSteamVrProcessInspector
    {
        public InternalDriverSteamVrProcessSnapshot Snapshot { get; set; } =
            new(VrServerRunning: false, VrMonitorRunning: false);

        public InternalDriverSteamVrProcessSnapshot Inspect() => Snapshot;
    }

    private sealed class FakeMaintenance : IInternalDriverRegistrationMaintenance
    {
        public SteamVrDriverStartupInspection Inspection { get; set; } = null!;

        public InternalDriverRemovalResult Removal { get; set; } = new(
            Changed: false,
            RestartRequired: false,
            "Nothing to remove.");

        public int RemoveCalls { get; private set; }

        public ValueTask<SteamVrDriverStartupInspection> InspectNextStartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Inspection);

        public ValueTask<InternalDriverRemovalResult> RemoveAsync(
            CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            return ValueTask.FromResult(Removal);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
