using System.Globalization;
using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.RecordingInspector.Tests;

public sealed class RecordingInspectionSummaryTests
{
    private const int SampleCount = 360;
    private const double SampleRateHz = 90d;
    private const double ControllerLagSeconds = 0.017d;

    [Fact]
    public void RenderReportsStableStreamValidityMotionAndLagSummary()
    {
        var summary = RecordingInspectionSummaryRenderer.Render(CreateRecording());
        var expected = Lines(
            "Lighthouse Touch Bridge - Recording Inspector",
            "format: ltb-pose-recording",
            "schema_version: 1",
            "stream_count: 2",
            string.Empty,
            "stream: tracker",
            "  source_kind: trackedPose",
            "  device_id: [redacted]",
            "  display_name: Synthetic tracker",
            "  samples: 360",
            "  duration_seconds: 3.988889",
            "  nominal_rate_hz: 90.000",
            "  orientation_channel_ratio: 100.00 %",
            "  position_channel_ratio: 100.00 %",
            "  orientation_valid_ratio: 90.00 %",
            "  position_valid_ratio: 90.00 %",
            "  tracking_validity_ratio: 100.00 %",
            "  connectivity_ratio: 90.00 %",
            "  orientation_motion_degrees: 333.109",
            "  rotation_axis_coverage: 1.000000",
            "  position_extent_meters: 0.688482",
            string.Empty,
            "stream: controller",
            "  source_kind: inputController",
            "  device_id: [redacted]",
            "  display_name: Synthetic controller",
            "  samples: 360",
            "  duration_seconds: 3.988889",
            "  nominal_rate_hz: 90.000",
            "  orientation_channel_ratio: 100.00 %",
            "  position_channel_ratio: 75.00 %",
            "  orientation_valid_ratio: 100.00 %",
            "  position_valid_ratio: 75.00 %",
            "  tracking_validity_ratio: 100.00 %",
            "  connectivity_ratio: 100.00 %",
            "  orientation_motion_degrees: 367.641",
            "  rotation_axis_coverage: 1.000000",
            "  position_extent_meters: 0.688429",
            string.Empty,
            "lag_pair: tracker=tracker, controller=controller",
            "controller_lag_ms: 17.000",
            "lag_correlation: 1.000000",
            "lag_confidence: 0.597",
            "lag_compared_samples: 716",
            "coarse_correlation_lag_ms: 17.000",
            "coarse_correlation_score: 1.000000",
            "runner_up_correlation_score: 0.99644",
            "correlation_peak_prominence: 0.00356",
            "correlation_interval_ms: [-16.000, 50.000]",
            "provisional_rotation_lag_ms: 17.000",
            "coarse_rotation_residual_deg: 0.005335",
            "refined_rotation_residual_deg: 0.005335",
            "evaluated_candidate_count: 201",
            string.Empty);

        Assert.Equal(expected, summary);
    }

    [Fact]
    public void LagConfidenceReportPrecisionIgnoresIncidentalPlatformDeltaButPreservesRegressionSignal()
    {
        var linuxLine = RenderedLagConfidenceLine(0.597004d);
        var windowsLine = RenderedLagConfidenceLine(0.597037d);
        var substantiveRegressionLine = RenderedLagConfidenceLine(0.596004d);

        Assert.Equal("lag_confidence: 0.597", linuxLine);
        Assert.Equal(linuxLine, windowsLine);
        Assert.Equal("lag_confidence: 0.596", substantiveRegressionLine);
        Assert.NotEqual(linuxLine, substantiveRegressionLine);
    }

    [Fact]
    public void CorrelationEvidenceReportPrecisionIgnoresPlatformDeltaButPreservesRegressionSignal()
    {
        var linuxEvidence = RenderedCorrelationEvidence(
            runnerUp: 0.99643586223984d,
            prominence: 0.00356413776016d);
        var windowsEvidence = RenderedCorrelationEvidence(
            runnerUp: 0.99643546820631d,
            prominence: 0.00356453179369d);
        var substantiveRegressionEvidence = RenderedCorrelationEvidence(
            runnerUp: 0.99653586223984d,
            prominence: 0.00346413776016d);

        Assert.Equal(
            ("runner_up_correlation_score: 0.99644", "correlation_peak_prominence: 0.00356"),
            linuxEvidence);
        Assert.Equal(linuxEvidence, windowsEvidence);
        Assert.Equal(
            ("runner_up_correlation_score: 0.99654", "correlation_peak_prominence: 0.00346"),
            substantiveRegressionEvidence);
        Assert.NotEqual(linuxEvidence, substantiveRegressionEvidence);
    }

    [Fact]
    public void ApplicationRejectsUnsupportedSchemaWithExistingDiagnostic()
    {
        var unsupportedJson = PoseRecordingJson.Serialize(CreateRecording())
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ltb-recording-inspector-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, unsupportedJson);
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);

            var exitCode = RecordingInspectorApplication.Run(
                ["inspect", path],
                output,
                error);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.Equal(
                $"Recording command failed: Recording schema version 2 is not supported; expected 1.{Environment.NewLine}",
                error.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static PoseRecording CreateRecording()
    {
        var trackerSamples = new RecordedPoseSample[SampleCount];
        var controllerSamples = new RecordedPoseSample[SampleCount];
        for (var index = 0; index < SampleCount; index++)
        {
            var time = 1d + (index / SampleRateHz);
            var phase = 2d * Math.PI * index / (SampleCount - 1d);
            var pose = new RigidTransform(EvaluateRotation(phase), EvaluatePosition(phase));
            var trackerSample = new TimestampedPoseSample(
                time,
                pose,
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);
            trackerSamples[index] = new RecordedPoseSample(
                trackerSample,
                isConnected: index >= SampleCount / 10,
                PoseTrackingResult.RunningOk);

            var controllerValidity = PoseValidity.Orientation | PoseValidity.TrackingValid;
            if (index % 4 != 0)
            {
                controllerValidity |= PoseValidity.Position;
            }

            var controllerSample = new TimestampedPoseSample(
                time + ControllerLagSeconds,
                pose,
                controllerValidity);
            controllerSamples[index] = new RecordedPoseSample(
                controllerSample,
                isConnected: true,
                PoseTrackingResult.RunningOk);
        }

        return new PoseRecording(
        [
            new PoseStreamRecording(
                new PoseStreamIdentity(
                    "tracker",
                    PoseSourceKind.TrackedPose,
                    "synthetic-tracker-device",
                    "Synthetic tracker"),
                trackerSamples),
            new PoseStreamRecording(
                new PoseStreamIdentity(
                    "controller",
                    PoseSourceKind.InputController,
                    "synthetic-controller-device",
                    "Synthetic controller"),
                controllerSamples),
        ]);
    }

    private static Quaternion EvaluateRotation(double phase)
    {
        var yaw = 0.82 * Math.Sin(0.91 * phase) + 0.16 * Math.Sin((2.7 * phase) + 0.2);
        var pitch = 0.64 * Math.Sin((1.31 * phase) + 0.45) + 0.12 * Math.Cos(2.1 * phase);
        var roll = 0.73 * Math.Sin((1.73 * phase) - 0.25) + 0.14 * Math.Cos(2.9 * phase);
        return Quaternion.Normalize(
            Quaternion.CreateFromYawPitchRoll((float)yaw, (float)pitch, (float)roll));
    }

    private static Vector3 EvaluatePosition(double phase) => new(
        (float)(0.18 + (0.23 * Math.Sin(0.83 * phase)) + (0.04 * Math.Cos(2.2 * phase))),
        (float)(1.08 + (0.17 * Math.Sin((1.37 * phase) + 0.4))),
        (float)(-0.28 + (0.21 * Math.Cos((1.11 * phase) - 0.2))));

    private static string RenderedLagConfidenceLine(double confidence) =>
        RecordingInspectionSummaryRenderer.RenderLag(
                new LagEstimate(
                    LagSeconds: 0.017d,
                    CorrelationScore: 1d,
                    Confidence: confidence,
                    ComparedSampleCount: 716,
                    SearchStepSeconds: 0.001d,
                    MaximumAbsoluteLagSeconds: 0.1d))
            .Split(Environment.NewLine)
            .Single(line => line.StartsWith("lag_confidence:", StringComparison.Ordinal));

    private static (string RunnerUp, string Prominence) RenderedCorrelationEvidence(
        double runnerUp,
        double prominence)
    {
        var lines = RecordingInspectionSummaryRenderer.RenderLag(
                new LagEstimate(
                    LagSeconds: 0.017d,
                    CorrelationScore: 1d,
                    Confidence: 0.597004d,
                    ComparedSampleCount: 716,
                    SearchStepSeconds: 0.001d,
                    MaximumAbsoluteLagSeconds: 0.1d)
                {
                    RunnerUpCorrelationScore = runnerUp,
                    PeakProminence = prominence,
                })
            .Split(Environment.NewLine);
        return (
            lines.Single(line => line.StartsWith("runner_up_correlation_score:", StringComparison.Ordinal)),
            lines.Single(line => line.StartsWith("correlation_peak_prominence:", StringComparison.Ordinal)));
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines);
}
