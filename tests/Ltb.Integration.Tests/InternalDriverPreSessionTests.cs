using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Driver;
using Ltb.OpenVr;

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
        Assert.Contains("one normal live LTB session", result.Remediation, StringComparison.Ordinal);
        Assert.Contains("No steamvr.vrsettings write", result.Diagnostic);
        Assert.Contains(
            "no tracker path was synthesized",
            result.Diagnostic,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllBytes(steamVrSettings));
    }

    [Fact]
    public async Task StoppedManualBindingUsesOneExactNormalizedCurrentEvidencePair()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync(" lhr-left ", "lhr-right");
        const string leftPath = "/devices/lighthouse/live-left";
        const string rightPath = "/devices/custom.driver/live-right";
        fixture.RecordObservations(
            (" lhr-left ", leftPath),
            ("LHR-RIGHT", rightPath));
        var settingsBefore = File.ReadAllBytes(fixture.Paths.SettingsPath);

        var result = await control.PrepareStartAsync();

        Assert.Equal(InternalDriverPreSessionState.Ready, result.State);
        Assert.True(result.CanStart);
        Assert.Contains("two distinct exact", result.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("redacted", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(leftPath, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(rightPath, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(leftPath, result.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain(rightPath, result.Remediation, StringComparison.Ordinal);
        Assert.Equal(settingsBefore, File.ReadAllBytes(fixture.Paths.SettingsPath));
    }

    [Fact]
    public async Task SimilarSerialAndPriorPathHistoryCannotSubstituteForExactCurrentEvidence()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        var store = new TrackerPathObservationStore(
            fixture.Paths.EffectiveTrackerPathObservationStorePath);
        store.RecordObservations(
        [
            new TrackerPathObservationCandidate(
                "LHR-LEFT-SIMILAR",
                "/devices/lighthouse/old-left",
                Fixture.ObservedAt(1)),
            new TrackerPathObservationCandidate(
                "LHR-RIGHT",
                "/devices/lighthouse/right",
                Fixture.ObservedAt(1)),
        ]);
        store.RecordObservation(new TrackerPathObservationCandidate(
            "LHR-LEFT-SIMILAR",
            "/devices/lighthouse/current-left",
            Fixture.ObservedAt(2)));

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.RegisteredDevicePathUnresolved,
            result.State);
        Assert.Contains("one normal live LTB session", result.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("old-left", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("current-left", result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("invalid_path")]
    [InlineData("pending")]
    public async Task MalformedAmbiguousInvalidOrPendingStoreFailsClosedWithoutWrites(
        string storeCase)
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        fixture.WriteInvalidStore(storeCase);
        var settingsBefore = File.ReadAllBytes(fixture.Paths.SettingsPath);
        var steamVrSettings = Path.Combine(fixture.Root, "steamvr.vrsettings");
        File.WriteAllText(steamVrSettings, "{\"sentinel\":true}");
        var steamVrBefore = File.ReadAllBytes(steamVrSettings);

        var result = await control.PrepareStartAsync();

        Assert.Equal(
            InternalDriverPreSessionState.RegisteredDevicePathUnresolved,
            result.State);
        Assert.Contains("failed closed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one normal live LTB session", result.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-left", result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-right", result.Diagnostic, StringComparison.Ordinal);
        Assert.Equal(settingsBefore, File.ReadAllBytes(fixture.Paths.SettingsPath));
        Assert.Equal(steamVrBefore, File.ReadAllBytes(steamVrSettings));
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
    public async Task StaleVerificationDecisionReloadsNewBindingWithoutOverwritingIt()
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
        var newer = InternalDriverSettingsFile.Load(fixture.Paths.SettingsPath)
            .WithManualTrackerBinding(
                new InternalDriverTrackerBinding("LHR-RIGHT", "LHR-LEFT"));
        InternalDriverSettingsFile.Save(fixture.Paths.SettingsPath, newer);

        var result = await control.ApplyManualBindingDecisionAsync(
            verification,
            InternalDriverManualBindingDecision.RetainManualBinding);

        Assert.Equal(
            InternalDriverPreSessionState.ManualBindingDecisionStale,
            result.State);
        Assert.Equal("LHR-RIGHT", result.LeftTrackerSerial);
        Assert.Equal("LHR-LEFT", result.RightTrackerSerial);
        Assert.Contains("no settings were overwritten", result.Diagnostic);
        Assert.Contains("rerun", result.Remediation, StringComparison.OrdinalIgnoreCase);
        var retained = InternalDriverSettingsFile.Load(fixture.Paths.SettingsPath);
        Assert.Equal("LHR-RIGHT", retained.ManualTrackerBinding?.LeftTrackerSerial);
        Assert.Equal("LHR-LEFT", retained.ManualTrackerBinding?.RightTrackerSerial);
    }

    [Fact]
    public async Task SamePairGenerationDriftMakesVerificationDecisionStaleWithoutOverwrite()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        var generation =
            InternalDriverSettingsFile.ComputeGeneration(
                fixture.Paths.SettingsPath);
        var verification = new InternalDriverManualBindingVerificationEvidence(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            "LHR-LEFT",
            "LHR-RIGHT",
            "Correlation suggests the reverse pair.",
            "LHR-RIGHT",
            "LHR-LEFT",
            generation);
        var changed = InternalDriverSettingsFile.Load(fixture.Paths.SettingsPath)
            .WithUnregisterOnExit(false);
        InternalDriverSettingsFile.Save(fixture.Paths.SettingsPath, changed);
        var changedBytes = File.ReadAllBytes(fixture.Paths.SettingsPath);

        var result = await control.ApplyManualBindingDecisionAsync(
            verification,
            InternalDriverManualBindingDecision.AcceptCorrectionCandidate);

        Assert.Equal(
            InternalDriverPreSessionState.ManualBindingDecisionStale,
            result.State);
        Assert.Equal("LHR-LEFT", result.LeftTrackerSerial);
        Assert.Equal("LHR-RIGHT", result.RightTrackerSerial);
        Assert.Contains("generation changed", result.Diagnostic);
        Assert.Contains("no settings were overwritten", result.Diagnostic);
        Assert.Equal(changedBytes, File.ReadAllBytes(fixture.Paths.SettingsPath));
    }

    [Fact]
    public async Task RefreshCarriesWarningsRecoveryPathHistoryAndReadOnlyRoleDrift()
    {
        using var fixture = new Fixture();
        var settingsPath = fixture.Maintenance.Inspection.Paths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            """
            {
              "trackers": {
                "/devices/lighthouse/live-left": "TrackerRole_None",
                "/devices/lighthouse/live-right": "TrackerRole_Handed"
              }
            }
            """);
        var backupPath = settingsPath + ".ltb-backup";
        File.WriteAllText(backupPath, "metadata-only candidate bytes");
        fixture.RecordObservations(
            ("LHR-LEFT", "/devices/lighthouse/prior-left"),
            ("LHR-RIGHT", "/devices/lighthouse/live-right"));
        _ = new TrackerPathObservationStore(
                fixture.Paths.EffectiveTrackerPathObservationStorePath)
            .RecordObservation(new TrackerPathObservationCandidate(
                "LHR-LEFT",
                "/devices/lighthouse/live-left",
                Fixture.ObservedAt(3)));
        fixture.Maintenance.Inspection = fixture.Maintenance.Inspection with
        {
            ExternalIntegrationWarnings =
                ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots(
                [Path.Combine(fixture.Root, "external", "vmt")]),
        };
        fixture.Maintenance.RoleDrift = new TrackerRoleDrift(
            new PhysicalTrackerRoleTargets(
                "/devices/lighthouse/live-left",
                "/devices/lighthouse/live-right"),
            new TrackerRoleDriftEntry(
                "/devices/lighthouse/live-left",
                TrackerRoleDriftStatus.UnchangedNeutral,
                "TrackerRole_None"),
            new TrackerRoleDriftEntry(
                "/devices/lighthouse/live-right",
                TrackerRoleDriftStatus.Changed,
                "TrackerRole_Handed"));
        var settingsBefore = File.ReadAllBytes(settingsPath);
        var backupBefore = File.ReadAllBytes(backupPath);
        await using var control = fixture.CreateControl();

        var result = await control.RefreshAsync();

        var warning = Assert.Single(result.ExternalRegistrationWarnings);
        Assert.Equal(ExternalSteamVrIntegrationIdentity.VirtualMotionTracker, warning.Identity);
        var recovery = Assert.IsType<SteamVrSettingsRecoveryDiscovery>(
            result.RecoveryDiscovery);
        var candidate = Assert.Single(recovery.Candidates);
        Assert.Equal(Path.GetFileName(backupPath), candidate.FileName);
        Assert.Equal(backupBefore.LongLength, candidate.LengthBytes);
        Assert.True(result.TrackerRoleDrift?.HasDrift);
        var left = Assert.Single(
            result.TrackerPathObservations,
            observation => observation.TrackerSerial == "LHR-LEFT");
        Assert.Equal("/devices/lighthouse/live-left", left.RegisteredDevicePath);
        Assert.Single(left.PathChangeHistory);
        Assert.False(result.TrackerPathReconciliationPending);
        Assert.Equal(settingsBefore, File.ReadAllBytes(settingsPath));
        Assert.Equal(backupBefore, File.ReadAllBytes(backupPath));
        Assert.Equal(0, fixture.Maintenance.RemoveCalls);
    }

    [Fact]
    public async Task RefreshAssessesExactReusableStoredPairAtTwentyMillimeterBoundaries()
    {
        using var fixture = new Fixture();
        await using var control = fixture.CreateControl();
        _ = await control.SaveManualBindingAsync("lhr-left", "lhr-right");
        CalibrationProfileFile.SaveStore(
            fixture.Paths.CalibrationProfileStorePath,
            new CalibrationProfileStore(
            [
                StoredProfile(
                    ControllerHand.Left,
                    "LHR-LEFT",
                    positionRmsMillimeters: 20d,
                    leverArmMillimeters: 10d),
                StoredProfile(
                    ControllerHand.Right,
                    "LHR-RIGHT",
                    positionRmsMillimeters: 19.999d,
                    leverArmMillimeters: 30d),
            ]));

        var result = await control.RefreshAsync();

        var quality = Assert.IsType<StoredCalibrationProfilePairAssessment>(
            result.StoredProfileQuality);
        Assert.Equal(
            StoredCalibrationPositionGuidance.RecaptureRecommended,
            quality.Left.PositionGuidance);
        Assert.Equal(
            StoredCalibrationPositionGuidance.WithinOperationalGuidance,
            quality.Right.PositionGuidance);
        Assert.Equal(
            StoredCalibrationLeverArmGuidance.MaterialMagnitudeDisagreement,
            quality.LeverArmGuidance);
        Assert.Equal(
            20d,
            quality.LeverArmMagnitudeDifferenceMillimeters!.Value,
            3);
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
            ProductionInternalDriverSessionRuntime.ToManualBindingVerificationEvidence(
                core,
                new string('a', 64));

        Assert.Equal(
            InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
            evidence.State);
        Assert.Equal("LHR-LEFT", evidence.LeftTrackerSerial);
        Assert.Equal("LHR-RIGHT", evidence.RightTrackerSerial);
        Assert.Equal("LHR-RIGHT", evidence.CorrectionLeftTrackerSerial);
        Assert.Equal("LHR-LEFT", evidence.CorrectionRightTrackerSerial);
        Assert.Equal(new string('A', 64), evidence.AuthorityGeneration);
        Assert.True(evidence.RequiresDecision);
    }

    private static CalibrationProfile StoredProfile(
        ControllerHand hand,
        string trackerSerial,
        double positionRmsMillimeters,
        double leverArmMillimeters) => new(
        CalibrationProfileSchema.CurrentVersion,
        $"{hand} stored profile",
        hand,
        ControllerRuntimeIdentities.MetaLinkLibOvr,
        "Quest 2 Touch",
        controllerIdentity: null,
        trackerSerial,
        CalibrationDriverProfiles.LtbTouch,
        ProfileCalibrationPolicy.Auto,
        ProfileCalibrationMode.FullSixDof,
        "validated full 6DoF",
        new TrackerToControllerTransform(
            new Vector3((float)(leverArmMillimeters / 1_000d), 0f, 0f),
            Quaternion.Identity),
        estimatedLagMilliseconds: 1d,
        new CalibrationProfileQuality(
            rotationRmsDegrees: 1d,
            positionRmsMillimeters,
            translationCondition: 10d,
            inlierRatio: 0.95d),
        Fixture.ObservedAt(1));

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

        public static DateTimeOffset ObservedAt(int second) => new(
            2026,
            7,
            28,
            0,
            0,
            second,
            TimeSpan.Zero);

        public void RecordObservations(
            params (string Serial, string DevicePath)[] observations)
        {
            var candidates = observations
                .Select((observation, index) =>
                    new TrackerPathObservationCandidate(
                        observation.Serial,
                        observation.DevicePath,
                        ObservedAt(index + 1)))
                .ToArray();
            _ = new TrackerPathObservationStore(
                    Paths.EffectiveTrackerPathObservationStorePath)
                .RecordObservations(candidates);
        }

        public void WriteInvalidStore(string storeCase)
        {
            var path = Paths.EffectiveTrackerPathObservationStorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (storeCase == "pending")
            {
                RecordObservations(
                    ("LHR-LEFT", "/devices/lighthouse/secret-left"),
                    ("LHR-RIGHT", "/devices/lighthouse/secret-right"));
                File.WriteAllText(
                    path + ".path-change-pending",
                    """
                    {
                      "schema_version": 1,
                      "affected_tracker_serials": [
                        "LHR-LEFT"
                      ]
                    }

                    """);
                return;
            }

            File.WriteAllText(
                path,
                storeCase switch
                {
                    "malformed" => "{",
                    "duplicate" =>
                        PersistedStore(
                            "/devices/lighthouse/secret-left",
                            "/devices/lighthouse/secret-left"),
                    "invalid_path" =>
                        PersistedStore(
                            "openvr://device/secret-left",
                            "/devices/lighthouse/secret-right"),
                    _ => throw new ArgumentOutOfRangeException(nameof(storeCase)),
                });
        }

        private static string PersistedStore(string leftPath, string rightPath) =>
            $$"""
            {
              "schema_version": 1,
              "observations": [
                {
                  "tracker_serial": "LHR-LEFT",
                  "registered_device_path": "{{leftPath}}",
                  "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
                  "path_change_history": []
                },
                {
                  "tracker_serial": "LHR-RIGHT",
                  "registered_device_path": "{{rightPath}}",
                  "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
                  "path_change_history": []
                }
              ]
            }

            """;

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

        public TrackerRoleDrift? RoleDrift { get; set; }

        public InternalDriverRemovalResult Removal { get; set; } = new(
            Changed: false,
            RestartRequired: false,
            "Nothing to remove.");

        public int RemoveCalls { get; private set; }

        public ValueTask<SteamVrDriverStartupInspection> InspectNextStartAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Inspection);

        public TrackerRoleDrift? InspectTrackerRoleDrift(
            SteamVrDriverStartupInspection inspection) => RoleDrift;

        public ValueTask<InternalDriverRemovalResult> RemoveAsync(
            CancellationToken cancellationToken = default)
        {
            RemoveCalls++;
            return ValueTask.FromResult(Removal);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
