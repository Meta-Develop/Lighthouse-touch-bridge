using System.Numerics;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.Integration.Tests;

public sealed class TwoHandCalibrationWizardStateTests
{
    [Theory]
    [InlineData(HandCalibrationFailure.InvalidCapture, "Recording")]
    [InlineData(HandCalibrationFailure.TimeAlignmentRejected, "TimeAlignment")]
    [InlineData(HandCalibrationFailure.CalibrationRejected, "RotationSolve")]
    public void PipelineFailuresMapToActionableWizardStages(
        HandCalibrationFailure failure,
        string expectedState)
    {
        Assert.Equal(
            expectedState,
            FileCalibrationWizardBackend.FailureState(failure).ToString());
    }

    [Fact]
    public async Task FirstRunTraversesWizardAndReportsNormalRotationOnlyFallback()
    {
        var runtime = new ScriptedWizardRuntime();
        var backend = new InMemoryWizardBackend();
        var output = new RecordingWizardOutput();
        var wizard = new TwoHandCalibrationWizard(runtime, backend, output);

        var result = await wizard.RunAsync();

        Assert.True(result.Success);
        Assert.False(result.ReusedProfiles);
        Assert.Equal(CalibrationWizardState.Active, result.FinalState);
        Assert.Equal(
            [
                CalibrationWizardState.DependencyCheck,
                CalibrationWizardState.WaitingForSteamVR,
                CalibrationWizardState.WaitingForDevices,
                CalibrationWizardState.Ready,
                CalibrationWizardState.OverrideRelease,
                CalibrationWizardState.Recording,
                CalibrationWizardState.Association,
                CalibrationWizardState.TimeAlignment,
                CalibrationWizardState.RotationSolve,
                CalibrationWizardState.TranslationAttempt,
                CalibrationWizardState.Validation,
                CalibrationWizardState.ApplyProfile,
                CalibrationWizardState.Active,
            ],
            result.StateHistory);
        Assert.Equal(2, runtime.CapturedHands.Count);
        Assert.True(runtime.OverridesReleased);
        Assert.Equal(2, runtime.AppliedProfiles.Count);
        Assert.Equal(CalibrationModel.FullSixDof, result.Profiles.Single(
            profile => profile.Hand == CalibrationWizardHand.Left).SelectedModel);
        Assert.Equal(CalibrationModel.RotationOnly, result.Profiles.Single(
            profile => profile.Hand == CalibrationWizardHand.Right).SelectedModel);
        Assert.Contains(
            output.Lines,
            line => line.Contains(
                "mode=rotation_only_fallback reason=no position available; accepted rotation-only fallback",
                StringComparison.Ordinal));
        Assert.Contains(
            output.Lines,
            line => line == "association_tracker_enumeration_swapped: true");
        foreach (var hand in Enum.GetValues<CalibrationWizardHand>())
        {
            var snapshots = output.Progress
                .Where(progress => progress.Hand == hand)
                .ToArray();
            Assert.Equal(2, snapshots.Length);
            Assert.False(snapshots[0].RotationReady);
            Assert.True(snapshots[^1].RotationReady);
            Assert.True(snapshots[^1].RotationProgress > snapshots[0].RotationProgress);
            Assert.True(snapshots[^1].TotalRotationDegrees > snapshots[0].TotalRotationDegrees);
            Assert.Equal(hand == CalibrationWizardHand.Left, snapshots[^1].PositionReady);
        }

        Assert.Contains(
            output.Lines,
            line => line.StartsWith(
                "quality_rotation: hand=left rotation_rms_deg=0.8000 rotation_percentile_deg=1.1000 rotation_inlier_ratio=0.9700",
                StringComparison.Ordinal));
        Assert.Contains(
            output.Lines,
            line => line.StartsWith(
                "quality_position: hand=left position_rms_mm=7.200 position_percentile_mm=11.400 rotation_only_position_rms_mm=56.000",
                StringComparison.Ordinal));
        Assert.Contains(
            output.Lines,
            line => line.Contains(
                "quality_translation: hand=right translation_condition=unavailable translation_inlier_ratio=unavailable translation_magnitude_mm=0.000 translation_split_disagreement_mm=unavailable",
                StringComparison.Ordinal));
        Assert.Contains(
            output.Lines,
            line => line.Contains(
                "observability: hand=right rotation_observable=true rotation_degeneracy=None translation_observable=false translation_degeneracy=MissingPosition axis_coverage=0.4200",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterRunMatchesSerialAndHandProfilesWithoutCapture()
    {
        var backend = new InMemoryWizardBackend();
        var firstRuntime = new ScriptedWizardRuntime();
        var first = await new TwoHandCalibrationWizard(
            firstRuntime,
            backend,
            new RecordingWizardOutput()).RunAsync();
        Assert.True(first.Success);

        var laterRuntime = new ScriptedWizardRuntime();
        var output = new RecordingWizardOutput();
        var later = await new TwoHandCalibrationWizard(
            laterRuntime,
            backend,
            output).RunAsync();

        Assert.True(later.Success);
        Assert.True(later.ReusedProfiles);
        Assert.Empty(laterRuntime.CapturedHands);
        Assert.False(laterRuntime.OverridesReleased);
        Assert.Equal(2, laterRuntime.AppliedProfiles.Count);
        Assert.Equal(
            [
                CalibrationWizardState.DependencyCheck,
                CalibrationWizardState.WaitingForSteamVR,
                CalibrationWizardState.WaitingForDevices,
                CalibrationWizardState.Ready,
                CalibrationWizardState.ApplyProfile,
                CalibrationWizardState.Active,
            ],
            later.StateHistory);
        Assert.Contains(
            output.Lines,
            line => line.Contains(
                "matching serial-and-hand profiles found",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReusableProfileOutputFailureAfterApplySafeDisablesBothHands()
    {
        var backend = new InMemoryWizardBackend();
        var seed = await new TwoHandCalibrationWizard(
            new ScriptedWizardRuntime(),
            backend,
            new RecordingWizardOutput()).RunAsync();
        Assert.True(seed.Success);

        var runtime = new CleanupOwnedWizardRuntime();
        var result = await new TwoHandCalibrationWizard(
            runtime,
            backend,
            new ThrowOnAppliedProfileOutput()).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Equal(1, runtime.SafeDisableCalls);
        Assert.Empty(runtime.ActiveHands);
        Assert.Equal(
            [
                CalibrationWizardState.ApplyProfile,
                CalibrationWizardState.SafeDisable,
                CalibrationWizardState.Ready,
            ],
            result.StateHistory.TakeLast(3));
        Assert.Contains("unexpected production-wizard operation", result.Diagnostic);
    }

    [Fact]
    public async Task BadRotationReturnsReadyWithDiagnosticInsteadOfFallback()
    {
        var runtime = new ScriptedWizardRuntime();
        var backend = new InMemoryWizardBackend(failRightRotation: true);
        var output = new RecordingWizardOutput();

        var result = await new TwoHandCalibrationWizard(runtime, backend, output)
            .RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Contains("right rotation calibration failed", result.Diagnostic);
        Assert.DoesNotContain(CalibrationWizardState.ApplyProfile, result.StateHistory);
        Assert.Empty(runtime.AppliedProfiles);
    }

    [Fact]
    public async Task PipelineInvalidCaptureReturnsFromRecordingToReady()
    {
        var validity = PoseValidity.Orientation |
            PoseValidity.Position |
            PoseValidity.TrackingValid;
        var nonMonotonic = new[]
        {
            new TimestampedPoseSample(1d, RigidTransform.Identity, validity),
            new TimestampedPoseSample(0.5d, RigidTransform.Identity, validity),
        };
        var pipelineResult = PerHandCalibrationPipeline.Run(
            new HandCalibrationInput(
                CalibrationHand.Left,
                "LHR-TEST0001",
                nonMonotonic,
                nonMonotonic));
        Assert.Equal(HandCalibrationFailure.InvalidCapture, pipelineResult.Failure);

        var output = new RecordingWizardOutput();
        var result = await new TwoHandCalibrationWizard(
            new ScriptedWizardRuntime(),
            new PipelineFailureWizardBackend(pipelineResult),
            output).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Equal(
            [
                CalibrationWizardState.Association,
                CalibrationWizardState.Recording,
                CalibrationWizardState.Ready,
            ],
            result.StateHistory.TakeLast(3));
        Assert.Contains(nameof(HandCalibrationFailure.InvalidCapture), result.Diagnostic);
    }

    [Fact]
    public async Task ApplyExceptionReturnsToReadyWithoutClaimingActive()
    {
        var runtime = new ScriptedWizardRuntime(failApply: true);
        var result = await new TwoHandCalibrationWizard(
            runtime,
            new InMemoryWizardBackend(),
            new RecordingWizardOutput()).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Equal(
            [CalibrationWizardState.ApplyProfile, CalibrationWizardState.Ready],
            result.StateHistory.TakeLast(2));
        Assert.DoesNotContain(CalibrationWizardState.Active, result.StateHistory);
        Assert.Empty(runtime.AppliedProfiles);
        Assert.Contains("saved but could not be applied", result.Diagnostic);
    }

    [Fact]
    public async Task ActiveHmdFailureStopsAtDependencyCheckBeforeAnyRuntimeEffects()
    {
        var dependencyStatus = new CalibrationWizardDependencyStatus(
            AlvrAvailable: true,
            VmtAvailable: true,
            "Quest/ALVR is the active SteamVR display HMD. Configure ALVR in " +
            "tracking-reference-only mode and make the intended Lighthouse HMD active.",
            ActiveHmdReady: false);
        var runtime = new ScriptedWizardRuntime(dependencyStatus: dependencyStatus);

        var result = await new TwoHandCalibrationWizard(
            runtime,
            new InMemoryWizardBackend(),
            new RecordingWizardOutput()).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Equal(
            [CalibrationWizardState.DependencyCheck, CalibrationWizardState.Ready],
            result.StateHistory);
        Assert.Contains("tracking-reference-only", result.Diagnostic);
        Assert.False(runtime.OverridesReleased);
        Assert.Empty(runtime.CapturedHands);
        Assert.Empty(runtime.AppliedProfiles);
    }
}

internal sealed class ScriptedWizardRuntime : ICalibrationWizardRuntime
{
    private readonly bool _failApply;
    private readonly CalibrationWizardDependencyStatus _dependencyStatus;

    public ScriptedWizardRuntime(
        bool failApply = false,
        CalibrationWizardDependencyStatus? dependencyStatus = null)
    {
        _failApply = failApply;
        _dependencyStatus = dependencyStatus ?? new CalibrationWizardDependencyStatus(
            true,
            true,
            "scripted dependencies available");
    }

    public static CalibrationWizardDeviceSet Devices { get; } = new(
        "CTRL-TEST-L",
        "CTRL-TEST-R",
        // Intentionally reverse the physical hand assignment.
        ["LHR-TEST0002", "LHR-TEST0001"]);

    public List<CalibrationWizardHand> CapturedHands { get; } = [];

    public bool OverridesReleased { get; private set; }

    public IReadOnlyList<CalibrationWizardProfileView> AppliedProfiles { get; private set; } =
        Array.Empty<CalibrationWizardProfileView>();

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(_dependencyStatus);

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(Devices);

    public Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        OverridesReleased = true;
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        CapturedHands.Add(hand);
        progress.Report(new CalibrationWizardCaptureProgress(
            hand,
            30,
            1d,
            hand == CalibrationWizardHand.Left ? 1d : 0d,
            0.01d,
            60d,
            0.35d,
            hand == CalibrationWizardHand.Left ? 1d : 0d,
            false,
            hand == CalibrationWizardHand.Left,
            "continue multi-axis rotation"));
        progress.Report(new CalibrationWizardCaptureProgress(
            hand,
            180,
            1d,
            hand == CalibrationWizardHand.Left ? 1d : 0d,
            0.42d,
            420d,
            1d,
            hand == CalibrationWizardHand.Left ? 1d : 0d,
            true,
            hand == CalibrationWizardHand.Left,
            hand == CalibrationWizardHand.Left
                ? "multi-axis rotation and position ready"
                : "multi-axis rotation ready; position unavailable"));
        return Task.FromResult(new CalibrationWizardCapture(
            hand,
            new PoseRecording(Array.Empty<PoseStreamRecording>())));
    }

    public Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        if (_failApply)
        {
            throw new InvalidOperationException("scripted profile application failed");
        }

        AppliedProfiles = profiles.ToArray();
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryWizardBackend : ICalibrationWizardBackend
{
    private readonly bool _failRightRotation;
    private IReadOnlyList<CalibrationWizardProfileView> _saved =
        Array.Empty<CalibrationWizardProfileView>();

    public InMemoryWizardBackend(bool failRightRotation = false)
    {
        _failRightRotation = failRightRotation;
    }

    public CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices)
    {
        var matching = _saved
            .Where(profile => devices.TrackerSerials.Contains(
                profile.TrackerSerial,
                StringComparer.Ordinal))
            .ToArray();
        return new CalibrationWizardProfileLookup(
            matching,
            matching.Length == 2
                ? "matched both fake tracker serials and hands"
                : "no complete fake profile pair");
    }

    public CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture)
    {
        var left = HandAnalysis(
            CalibrationWizardHand.Left,
            ScriptedWizardRuntime.Devices.LeftControllerSerial,
            "LHR-TEST0001",
            CalibrationModel.FullSixDof,
            "translation observable; held-out position RMS improved");
        var right = HandAnalysis(
            CalibrationWizardHand.Right,
            ScriptedWizardRuntime.Devices.RightControllerSerial,
            "LHR-TEST0002",
            _failRightRotation ? CalibrationModel.Failed : CalibrationModel.RotationOnly,
            _failRightRotation
                ? "rotation held-out quality rejected"
                : "no position available; accepted rotation-only fallback");
        return new CalibrationWizardAnalysis(
            new CalibrationWizardAssociation(
                left.TrackerSerial,
                right.TrackerSerial,
                0.97d,
                0.96d,
                true,
                "motion correlation resolved reversed tracker enumeration"),
            left,
            right);
    }

    public IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis)
    {
        _saved = analysis.Hands.Select(hand => new CalibrationWizardProfileView(
            $"Scripted {hand.Hand} profile",
            hand.Hand,
            hand.ControllerSerial,
            hand.TrackerSerial,
            hand.Calibration.SelectedModel,
            hand.Calibration.SelectionReason,
            hand.Calibration.TrackerToController,
            hand.Lag.LagSeconds * 1000d,
            hand.Calibration.Quality,
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero))).ToArray();
        return _saved;
    }

    private static CalibrationWizardHandAnalysis HandAnalysis(
        CalibrationWizardHand hand,
        string controllerSerial,
        string trackerSerial,
        CalibrationModel model,
        string reason)
    {
        var success = model != CalibrationModel.Failed;
        var translation = model == CalibrationModel.FullSixDof
            ? new Vector3(0.014f, -0.052f, 0.031f)
            : Vector3.Zero;
        var result = new CalibrationResult(
            CalibrationPolicy.Auto,
            model,
            reason,
            new RigidTransform(Quaternion.Identity, translation),
            new CalibrationQualityMetrics(
                success ? 0.8d : 7d,
                success ? 1.1d : 9d,
                model == CalibrationModel.FullSixDof ? 7.2d : null,
                model == CalibrationModel.FullSixDof ? 11.4d : null,
                model == CalibrationModel.FullSixDof ? 56d : null,
                translation.Length())
            {
                RotationInlierRatio = success ? 0.97d : 0.2d,
                TranslationInlierRatio = model == CalibrationModel.FullSixDof ? 0.94d : double.NaN,
            },
            new MotionObservability(
                success,
                model == CalibrationModel.FullSixDof,
                success
                    ? CalibrationDegeneracy.None
                    : CalibrationDegeneracy.RotationQualityRejected,
                model == CalibrationModel.FullSixDof
                    ? CalibrationDegeneracy.None
                    : CalibrationDegeneracy.MissingPosition,
                0.42d,
                model == CalibrationModel.FullSixDof ? 14.7d : double.PositiveInfinity),
            180,
            180,
            model == CalibrationModel.FullSixDof ? 180 : 0,
            128,
            42);
        return new CalibrationWizardHandAnalysis(
            hand,
            controllerSerial,
            trackerSerial,
            new LagEstimate(0.012d, 0.98d, 0.9d, 178, 0.001d, 0.1d),
            result);
    }
}

internal sealed class PipelineFailureWizardBackend : ICalibrationWizardBackend
{
    private readonly HandCalibrationResult _failure;

    public PipelineFailureWizardBackend(HandCalibrationResult failure)
    {
        _failure = failure;
    }

    public CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices) =>
        new(Array.Empty<CalibrationWizardProfileView>(), "capture required for failure test");

    public CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture) =>
        throw new CalibrationWizardRunException(
            FileCalibrationWizardBackend.FailureState(_failure.Failure),
            $"Pipeline rejected capture ({_failure.Failure}): {_failure.Reason}");

    public IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis) =>
        throw new InvalidOperationException("A rejected capture cannot be saved.");
}

internal sealed class RecordingWizardOutput : ICalibrationWizardOutput
{
    public List<string> Lines { get; } = [];

    public List<CalibrationWizardCaptureProgress> Progress { get; } = [];

    public void OnStateChanged(CalibrationWizardState state, string diagnostic) =>
        Lines.Add($"state: {state} ({diagnostic})");

    public void OnCaptureProgress(CalibrationWizardCaptureProgress progress)
    {
        Progress.Add(progress);
        Lines.Add($"coverage: {progress.Hand} {progress.Diagnostic}");
    }

    public void WriteLine(string message) => Lines.Add(message);
}

internal sealed class CleanupOwnedWizardRuntime :
    ICalibrationWizardRuntime,
    ICalibrationWizardCleanupRuntime
{
    private readonly ScriptedWizardRuntime _inner = new();

    public HashSet<CalibrationWizardHand> ActiveHands { get; } = [];

    public int SafeDisableCalls { get; private set; }

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken) =>
        _inner.CheckDependenciesAsync(cancellationToken);

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken) =>
        _inner.WaitForSteamVrAsync(cancellationToken);

    public Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken) =>
        _inner.WaitForDevicesAsync(cancellationToken);

    public Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken) =>
        _inner.ReleaseOverridesAsync(devices, cancellationToken);

    public Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken) =>
        _inner.CaptureAsync(hand, devices, progress, cancellationToken);

    public async Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        await _inner.ApplyProfilesAsync(profiles, cancellationToken);
        ActiveHands.UnionWith(profiles.Select(profile => profile.Hand));
    }

    public Task<IReadOnlyList<Exception>> SafeDisableAsync()
    {
        SafeDisableCalls++;
        ActiveHands.Clear();
        return Task.FromResult<IReadOnlyList<Exception>>(Array.Empty<Exception>());
    }
}

internal sealed class ThrowOnAppliedProfileOutput : ICalibrationWizardOutput
{
    public void OnStateChanged(CalibrationWizardState state, string diagnostic)
    {
    }

    public void OnCaptureProgress(CalibrationWizardCaptureProgress progress)
    {
    }

    public void WriteLine(string message)
    {
        if (message.StartsWith("profile:", StringComparison.Ordinal))
        {
            throw new IOException("synthetic post-apply output failure");
        }
    }
}
