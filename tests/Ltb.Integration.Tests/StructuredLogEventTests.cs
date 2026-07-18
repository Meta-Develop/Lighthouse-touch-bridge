using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Ltb.App;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.Integration.Tests;

public sealed class StructuredLogEventTests
{
    [Theory]
    [InlineData("OverrideRelease")]
    [InlineData("Recording")]
    [InlineData("Association")]
    [InlineData("TimeAlignment")]
    [InlineData("RotationSolve")]
    [InlineData("TranslationAttempt")]
    [InlineData("Validation")]
    [InlineData("ApplyProfile")]
    public async Task EveryCalibrationFailureStageReturnsReadyWithDiagnostic(
        string stateName)
    {
        var failedState = Enum.Parse<CalibrationWizardState>(stateName);
        var sink = new InMemoryLtbLogSink();
        var result = await new TwoHandCalibrationWizard(
            new ReliableWizardRuntime(failedState),
            new ReliableWizardBackend(failedState),
            new RecordingWizardOutput(),
            sink,
            new ReliableManualTimeProvider()).RunAsync();

        Assert.False(result.Success);
        Assert.Equal(CalibrationWizardState.Ready, result.FinalState);
        Assert.Equal(
            [failedState, CalibrationWizardState.Ready],
            result.StateHistory.TakeLast(2));
        Assert.Contains(stateName, result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(CalibrationWizardState.Active, result.StateHistory);
        if (failedState == CalibrationWizardState.RotationSolve)
        {
            Assert.Contains(
                sink.Events,
                entry => entry.Code == LtbDiagnosticCode.BadRotationCalibration &&
                         entry.State == RuntimeApplicationState.RotationSolve);
        }
    }

    [Fact]
    public async Task CalibrationDistinctionsUseDifferentTypedCodes()
    {
        var noPosition = await RunWizardAsync(CalibrationDegeneracy.MissingPosition);
        var poorObservability = await RunWizardAsync(
            CalibrationDegeneracy.TranslationUnobservable);
        var badRotationSink = new InMemoryLtbLogSink();
        var badRotation = await new TwoHandCalibrationWizard(
            new ReliableWizardRuntime(CalibrationWizardState.RotationSolve),
            new ReliableWizardBackend(CalibrationWizardState.RotationSolve),
            new RecordingWizardOutput(),
            badRotationSink,
            new ReliableManualTimeProvider()).RunAsync();

        Assert.True(noPosition.Result.Success);
        Assert.Contains(
            noPosition.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.NoPositionAvailable &&
                     entry.Properties["translationDegeneracy"] == "MissingPosition");
        Assert.DoesNotContain(
            noPosition.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.PoorTranslationObservability);

        Assert.True(poorObservability.Result.Success);
        Assert.Contains(
            poorObservability.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.PoorTranslationObservability &&
                     entry.Properties["translationDegeneracy"] ==
                     "TranslationUnobservable");
        Assert.DoesNotContain(
            poorObservability.Log.Events,
            entry => entry.Code == LtbDiagnosticCode.NoPositionAvailable);

        Assert.False(badRotation.Success);
        Assert.Contains(
            badRotationSink.Events,
            entry => entry.Code == LtbDiagnosticCode.BadRotationCalibration);
    }

    [Fact]
    public void JsonLinesSchemaHasRequiredFieldsAndStableSafetyCodeNames()
    {
        var requiredCodes = new[]
        {
            LtbDiagnosticCode.NoPositionAvailable,
            LtbDiagnosticCode.PoorTranslationObservability,
            LtbDiagnosticCode.BadRotationCalibration,
            LtbDiagnosticCode.TrackerLost,
            LtbDiagnosticCode.TouchInputLost,
            LtbDiagnosticCode.VmtUnavailable,
            LtbDiagnosticCode.SteamVrStopped,
            LtbDiagnosticCode.ProfileApplyFailed,
            LtbDiagnosticCode.SafeDisableFailed,
            LtbDiagnosticCode.RollbackFailed,
        };
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using (var sink = new JsonLinesLtbLogSink(writer, leaveOpen: true))
        {
            foreach (var code in requiredCodes)
            {
                sink.Write(new LtbLogEvent(
                    new DateTimeOffset(2026, 7, 18, 1, 2, 3, TimeSpan.Zero),
                    LtbLogLevel.Error,
                    code,
                    RuntimeApplicationState.SafeDisable,
                    $"synthetic {code} event",
                    new Dictionary<string, string>
                    {
                        ["hand"] = "left",
                        ["deviceIdentity"] = "TRACKER-LEFT",
                    }));
            }
        }

        var lines = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(requiredCodes.Length, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Assert.Equal(
                "2026-07-18T01:02:03+00:00",
                root.GetProperty("timestampUtc").GetString());
            Assert.Equal("Error", root.GetProperty("level").GetString());
            Assert.Equal(requiredCodes[index].ToString(), root.GetProperty("code").GetString());
            Assert.Equal("SafeDisable", root.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
            Assert.Equal(
                "left",
                root.GetProperty("properties").GetProperty("hand").GetString());
            Assert.Equal(
                "TRACKER-LEFT",
                root.GetProperty("properties").GetProperty("deviceIdentity").GetString());
        }
    }

    private static async Task<WizardRun> RunWizardAsync(
        CalibrationDegeneracy translationDegeneracy)
    {
        var log = new InMemoryLtbLogSink();
        var result = await new TwoHandCalibrationWizard(
            new ReliableWizardRuntime(),
            new ReliableWizardBackend(
                failureState: null,
                translationDegeneracy),
            new RecordingWizardOutput(),
            log,
            new ReliableManualTimeProvider()).RunAsync();
        return new WizardRun(result, log);
    }

    private sealed record WizardRun(
        CalibrationWizardResult Result,
        InMemoryLtbLogSink Log);
}

internal sealed class ReliableWizardRuntime : ICalibrationWizardRuntime
{
    private readonly CalibrationWizardState? _failureState;

    public ReliableWizardRuntime(CalibrationWizardState? failureState = null)
    {
        _failureState = failureState;
    }

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new CalibrationWizardDependencyStatus(
            true,
            true,
            "synthetic dependencies ready"));

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(ReliableDailyUseFakeRuntime.MatchingDevices);

    public Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        ThrowIf(CalibrationWizardState.OverrideRelease);
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        ThrowIf(CalibrationWizardState.Recording);
        return Task.FromResult(new CalibrationWizardCapture(
            hand,
            new PoseRecording(Array.Empty<PoseStreamRecording>())));
    }

    public Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        ThrowIf(CalibrationWizardState.ApplyProfile);
        return Task.CompletedTask;
    }

    private void ThrowIf(CalibrationWizardState state)
    {
        if (_failureState == state)
        {
            throw new InvalidOperationException($"synthetic {state} failure");
        }
    }
}

internal sealed class ReliableWizardBackend : ICalibrationWizardBackend
{
    private readonly CalibrationWizardState? _failureState;
    private readonly CalibrationDegeneracy _translationDegeneracy;

    public ReliableWizardBackend(
        CalibrationWizardState? failureState = null,
        CalibrationDegeneracy translationDegeneracy = CalibrationDegeneracy.MissingPosition)
    {
        _failureState = failureState;
        _translationDegeneracy = translationDegeneracy;
    }

    public CalibrationWizardProfileLookup FindReusableProfiles(
        CalibrationWizardDeviceSet devices) =>
        new(Array.Empty<CalibrationWizardProfileView>(), "synthetic capture required");

    public CalibrationWizardAnalysis AnalyzeFirstRun(
        CalibrationWizardDeviceSet devices,
        CalibrationWizardCapture leftCapture,
        CalibrationWizardCapture rightCapture)
    {
        if (_failureState is CalibrationWizardState.Association or
            CalibrationWizardState.TimeAlignment or
            CalibrationWizardState.TranslationAttempt)
        {
            throw new CalibrationWizardRunException(
                _failureState.Value,
                $"synthetic {_failureState.Value} failure");
        }

        var left = Analysis(
            CalibrationWizardHand.Left,
            "TOUCH-LEFT",
            "TRACKER-LEFT",
            CalibrationModel.FullSixDof,
            CalibrationDegeneracy.None);
        var right = Analysis(
            CalibrationWizardHand.Right,
            "TOUCH-RIGHT",
            "TRACKER-RIGHT",
            _failureState == CalibrationWizardState.RotationSolve
                ? CalibrationModel.Failed
                : CalibrationModel.RotationOnly,
            _translationDegeneracy);
        return new CalibrationWizardAnalysis(
            new CalibrationWizardAssociation(
                left.TrackerSerial,
                right.TrackerSerial,
                0.99d,
                0.98d,
                false,
                "synthetic stable-serial association"),
            left,
            right);
    }

    public IReadOnlyList<CalibrationWizardProfileView> SaveProfiles(
        CalibrationWizardAnalysis analysis)
    {
        if (_failureState == CalibrationWizardState.Validation)
        {
            throw new InvalidOperationException("synthetic Validation failure");
        }

        return analysis.Hands.Select(hand => new CalibrationWizardProfileView(
            $"Synthetic {hand.Hand} profile",
            hand.Hand,
            hand.ControllerSerial,
            hand.TrackerSerial,
            hand.Calibration.SelectedModel,
            hand.Calibration.SelectionReason,
            hand.Calibration.TrackerToController,
            hand.Lag.LagSeconds * 1000d,
            hand.Calibration.Quality,
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero))).ToArray();
    }

    private static CalibrationWizardHandAnalysis Analysis(
        CalibrationWizardHand hand,
        string controllerSerial,
        string trackerSerial,
        CalibrationModel model,
        CalibrationDegeneracy translationDegeneracy)
    {
        var failed = model == CalibrationModel.Failed;
        var full = model == CalibrationModel.FullSixDof;
        var translation = full ? new Vector3(0.01f, -0.04f, 0.02f) : Vector3.Zero;
        var calibration = new CalibrationResult(
            CalibrationPolicy.Auto,
            model,
            failed
                ? "synthetic RotationSolve failure"
                : full
                    ? "translation observable; held-out error improved"
                    : $"synthetic {translationDegeneracy} rotation-only fallback",
            new RigidTransform(Quaternion.Identity, translation),
            new CalibrationQualityMetrics(
                failed ? 8d : 0.6d,
                failed ? 10d : 0.9d,
                full ? 5d : null,
                full ? 7d : null,
                full ? 30d : null,
                translation.Length())
            {
                RotationInlierRatio = failed ? 0.2d : 0.98d,
                TranslationInlierRatio = full ? 0.97d : double.NaN,
                TranslationSplitDisagreementMillimeters = full ? 0.5d : double.NaN,
            },
            new MotionObservability(
                !failed,
                full,
                failed ? CalibrationDegeneracy.RotationQualityRejected : CalibrationDegeneracy.None,
                full ? CalibrationDegeneracy.None : translationDegeneracy,
                0.5d,
                full ? 10d : double.PositiveInfinity),
            180,
            180,
            full ? 180 : 0,
            100,
            20);
        return new CalibrationWizardHandAnalysis(
            hand,
            controllerSerial,
            trackerSerial,
            new LagEstimate(0.01d, 0.99d, 0.9d, 170, 0.001d, 0.1d),
            calibration);
    }
}
