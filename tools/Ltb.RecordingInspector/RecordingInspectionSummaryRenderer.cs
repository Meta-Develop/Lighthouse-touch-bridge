using System.Globalization;
using System.Numerics;
using System.Text;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.RecordingInspector;

/// <summary>Selection and redaction options for a recording inspection summary.</summary>
internal sealed record RecordingInspectionSummaryOptions(
    string? TrackerStreamId = null,
    string? ControllerStreamId = null,
    bool RevealDeviceIds = false);

/// <summary>Renders the human-readable inspector summary from parsed recording data.</summary>
internal static class RecordingInspectionSummaryRenderer
{
    private const int MaximumAxisSamples = 256;

    public static string Render(
        PoseRecording recording,
        RecordingInspectionSummaryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        options ??= new RecordingInspectionSummaryOptions();

        var output = new StringBuilder();
        output.AppendLine("Lighthouse Touch Bridge - Recording Inspector");
        AppendLine(output, $"format: {PoseRecording.FormatIdentifier}");
        AppendLine(output, $"schema_version: {recording.SchemaVersion}");
        AppendLine(output, $"stream_count: {recording.Streams.Count}");

        foreach (var stream in recording.Streams)
        {
            AppendStreamSummary(output, stream, options.RevealDeviceIds);
        }

        if (!TrySelectPair(
                recording,
                options.TrackerStreamId,
                options.ControllerStreamId,
                out var tracker,
                out var controller,
                out var diagnostic))
        {
            output.AppendLine();
            AppendLine(output, $"lag_estimate: unavailable ({diagnostic})");
            return output.ToString();
        }

        output.AppendLine();
        AppendLine(
            output,
            $"lag_pair: tracker={tracker!.Identity.StreamId}, controller={controller!.Identity.StreamId}");
        try
        {
            var lag = StreamLagEstimator.EstimateControllerLag(
                ToReplaySamples(tracker),
                ToReplaySamples(controller));
            output.Append(RenderLag(lag));
        }
        catch (LagEstimationException exception)
        {
            AppendLine(
                output,
                $"lag_estimate: rejected ({exception.Reason}: {exception.Message})");
        }
        catch (InvalidOperationException exception)
        {
            AppendLine(output, $"lag_estimate: unavailable ({exception.Message})");
        }

        return output.ToString();
    }

    internal static string RenderLag(LagEstimate lag)
    {
        ArgumentNullException.ThrowIfNull(lag);

        var output = new StringBuilder();
        AppendLine(output, $"controller_lag_ms: {lag.LagSeconds * 1_000d:F3}");
        AppendLine(output, $"lag_correlation: {lag.CorrelationScore:F6}");
        AppendLine(output, $"lag_confidence: {lag.Confidence:F6}");
        AppendLine(output, $"lag_compared_samples: {lag.ComparedSampleCount}");
        AppendLine(output, $"coarse_correlation_lag_ms: {FormatMilliseconds(lag.CoarseLagSeconds)}");
        AppendLine(output, $"coarse_correlation_score: {FormatFinite(lag.CoarseCorrelationScore)}");
        AppendLine(output, $"runner_up_correlation_score: {FormatFinite(lag.RunnerUpCorrelationScore)}");
        AppendLine(output, $"correlation_peak_prominence: {FormatFinite(lag.PeakProminence)}");
        AppendLine(
            output,
            $"correlation_interval_ms: [{FormatMilliseconds(lag.CorrelationIntervalMinimumSeconds)}, {FormatMilliseconds(lag.CorrelationIntervalMaximumSeconds)}]");
        AppendLine(output, $"provisional_rotation_lag_ms: {FormatMilliseconds(lag.ProvisionalRotationLagSeconds)}");
        AppendLine(output, $"coarse_rotation_residual_deg: {FormatFinite(lag.CoarseRotationResidualDegrees)}");
        AppendLine(output, $"refined_rotation_residual_deg: {FormatFinite(lag.RefinedRotationResidualDegrees)}");
        AppendLine(output, $"evaluated_candidate_count: {lag.EvaluatedCandidateLagsSeconds.Count}");
        return output.ToString();
    }

    internal static bool TrySelectPair(
        PoseRecording recording,
        string? trackerStreamId,
        string? controllerStreamId,
        out PoseStreamRecording? tracker,
        out PoseStreamRecording? controller,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(recording);

        tracker = ResolveExplicitStream(
            recording,
            trackerStreamId,
            PoseSourceKind.TrackedPose,
            "tracker");
        controller = ResolveExplicitStream(
            recording,
            controllerStreamId,
            PoseSourceKind.InputController,
            "controller");

        if (tracker is null)
        {
            var candidates = recording.Streams
                .Where(stream => stream.Identity.SourceKind == PoseSourceKind.TrackedPose)
                .ToArray();
            if (candidates.Length != 1)
            {
                diagnostic = candidates.Length == 0
                    ? "recording contains no tracked-pose stream"
                    : $"recording contains {candidates.Length} tracked-pose streams; select one with --tracker";
                return false;
            }

            tracker = candidates[0];
        }

        if (controller is null)
        {
            var candidates = recording.Streams
                .Where(stream => stream.Identity.SourceKind == PoseSourceKind.InputController)
                .ToArray();
            if (candidates.Length != 1)
            {
                diagnostic = candidates.Length == 0
                    ? "recording contains no input-controller stream"
                    : $"recording contains {candidates.Length} input-controller streams; select one with --controller";
                return false;
            }

            controller = candidates[0];
        }

        diagnostic = string.Empty;
        return true;
    }

    private static void AppendStreamSummary(
        StringBuilder output,
        PoseStreamRecording stream,
        bool revealDeviceIds)
    {
        var samples = stream.Samples;
        var duration = samples.Count < 2
            ? 0d
            : samples[^1].MonotonicHostTimeSeconds - samples[0].MonotonicHostTimeSeconds;
        var rate = duration > 0d ? (samples.Count - 1d) / duration : 0d;
        var orientationChannelRatio = Ratio(
            samples,
            sample => sample.Validity.HasFlag(PoseValidity.Orientation));
        var positionChannelRatio = Ratio(
            samples,
            sample => sample.Validity.HasFlag(PoseValidity.Position));
        var orientationValidRatio = Ratio(samples, HasUsableOrientation);
        var positionValidRatio = Ratio(samples, HasUsablePosition);
        var trackingRatio = Ratio(
            samples,
            sample => sample.Validity.HasFlag(PoseValidity.TrackingValid));
        var connectivityRatio = Ratio(samples, sample => sample.IsConnected);
        var motion = ComputeMotionCoverage(samples);

        output.AppendLine();
        AppendLine(output, $"stream: {stream.Identity.StreamId}");
        AppendLine(output, $"  source_kind: {FormatSourceKind(stream.Identity.SourceKind)}");
        AppendLine(
            output,
            $"  device_id: {(revealDeviceIds ? stream.Identity.DeviceId : "[redacted]")}");
        AppendLine(output, $"  display_name: {stream.Identity.DisplayName ?? "n/a"}");
        AppendLine(output, $"  samples: {samples.Count}");
        AppendLine(output, $"  duration_seconds: {duration:F6}");
        AppendLine(output, $"  nominal_rate_hz: {rate:F3}");
        AppendLine(output, $"  orientation_channel_ratio: {orientationChannelRatio:P2}");
        AppendLine(output, $"  position_channel_ratio: {positionChannelRatio:P2}");
        AppendLine(output, $"  orientation_valid_ratio: {orientationValidRatio:P2}");
        AppendLine(output, $"  position_valid_ratio: {positionValidRatio:P2}");
        AppendLine(output, $"  tracking_validity_ratio: {trackingRatio:P2}");
        AppendLine(output, $"  connectivity_ratio: {connectivityRatio:P2}");
        AppendLine(output, $"  orientation_motion_degrees: {motion.TotalAngularMotionDegrees:F3}");
        AppendLine(output, $"  rotation_axis_coverage: {motion.RotationAxisCoverage:F6}");
        AppendLine(output, $"  position_extent_meters: {motion.PositionExtentMeters:F6}");
    }

    private static MotionCoverage ComputeMotionCoverage(IReadOnlyList<RecordedPoseSample> samples)
    {
        var axes = new List<Vector3>();
        var totalAngularMotionRadians = 0d;
        for (var index = 1; index < samples.Count; index++)
        {
            var previous = samples[index - 1];
            var current = samples[index];
            if (!HasUsableOrientation(previous) || !HasUsableOrientation(current))
            {
                continue;
            }

            var delta = Quaternion.Normalize(Quaternion.Multiply(
                Quaternion.Inverse(previous.Pose.Rotation),
                current.Pose.Rotation));
            if (delta.W < 0f)
            {
                delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
            }

            var vectorLength = Math.Sqrt(
                ((double)delta.X * delta.X) +
                ((double)delta.Y * delta.Y) +
                ((double)delta.Z * delta.Z));
            var angle = 2d * Math.Atan2(vectorLength, Math.Clamp((double)delta.W, 0d, 1d));
            if (!double.IsFinite(angle) || angle <= 1e-7d || vectorLength <= 1e-12d)
            {
                continue;
            }

            totalAngularMotionRadians += angle;
            axes.Add(new Vector3(
                (float)(delta.X / vectorLength),
                (float)(delta.Y / vectorLength),
                (float)(delta.Z / vectorLength)));
        }

        var sampledAxes = EvenlySample(axes, MaximumAxisSamples);
        var axisCoverage = 0d;
        for (var first = 0; first < sampledAxes.Count; first++)
        {
            for (var second = first + 1; second < sampledAxes.Count; second++)
            {
                axisCoverage = Math.Max(
                    axisCoverage,
                    Vector3.Cross(sampledAxes[first], sampledAxes[second]).Length());
            }
        }

        var validPositions = samples
            .Where(HasUsablePosition)
            .Select(sample => sample.Pose.TranslationMeters)
            .ToArray();
        var positionExtent = 0d;
        if (validPositions.Length > 0)
        {
            var minimum = validPositions[0];
            var maximum = validPositions[0];
            foreach (var position in validPositions.AsSpan(1))
            {
                minimum = Vector3.Min(minimum, position);
                maximum = Vector3.Max(maximum, position);
            }

            positionExtent = Vector3.Distance(minimum, maximum);
        }

        return new MotionCoverage(
            totalAngularMotionRadians * 180d / Math.PI,
            axisCoverage,
            positionExtent);
    }

    private static IReadOnlyList<Vector3> EvenlySample(IReadOnlyList<Vector3> values, int limit)
    {
        if (values.Count <= limit)
        {
            return values;
        }

        var sampled = new Vector3[limit];
        for (var index = 0; index < limit; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (values.Count - 1d) / (limit - 1d),
                MidpointRounding.AwayFromZero);
            sampled[index] = values[sourceIndex];
        }

        return sampled;
    }

    private static PoseStreamRecording? ResolveExplicitStream(
        PoseRecording recording,
        string? streamId,
        PoseSourceKind expectedKind,
        string optionName)
    {
        if (streamId is null)
        {
            return null;
        }

        var stream = recording.Streams.SingleOrDefault(candidate =>
            string.Equals(candidate.Identity.StreamId, streamId, StringComparison.Ordinal));
        if (stream is null)
        {
            throw new ArgumentException(
                $"Selected {optionName} stream '{streamId}' does not exist.");
        }

        if (stream.Identity.SourceKind != expectedKind)
        {
            throw new ArgumentException(
                $"Selected {optionName} stream '{streamId}' has source kind {stream.Identity.SourceKind}, expected {expectedKind}.");
        }

        return stream;
    }

    private static IReadOnlyList<TimestampedPoseSample> ToReplaySamples(
        PoseStreamRecording stream) =>
        stream.Samples.Select(sample =>
        {
            var validity = sample.IsConnected
                ? sample.Validity
                : sample.Validity & ~PoseValidity.TrackingValid;
            return new TimestampedPoseSample(
                sample.MonotonicHostTimeSeconds,
                sample.Pose,
                validity);
        }).ToArray();

    private static string FormatMilliseconds(double seconds) =>
        double.IsFinite(seconds)
            ? (seconds * 1_000d).ToString("F3", CultureInfo.InvariantCulture)
            : "unavailable";

    private static string FormatFinite(double value) =>
        double.IsFinite(value)
            ? value.ToString("F6", CultureInfo.InvariantCulture)
            : "unavailable";

    private static double Ratio(
        IReadOnlyList<RecordedPoseSample> samples,
        Func<RecordedPoseSample, bool> predicate) =>
        samples.Count == 0 ? 0d : samples.Count(predicate) / (double)samples.Count;

    private static bool HasUsableOrientation(RecordedPoseSample sample) =>
        sample.IsConnected &&
        sample.Validity.HasFlag(PoseValidity.Orientation | PoseValidity.TrackingValid);

    private static bool HasUsablePosition(RecordedPoseSample sample) =>
        sample.IsConnected &&
        sample.Validity.HasFlag(PoseValidity.Position | PoseValidity.TrackingValid);

    private static string FormatSourceKind(PoseSourceKind kind) => kind switch
    {
        PoseSourceKind.InputController => "inputController",
        PoseSourceKind.TrackedPose => "trackedPose",
        _ => kind.ToString(),
    };

    private static void AppendLine(StringBuilder output, FormattableString line) =>
        output.AppendLine(line.ToString(CultureInfo.InvariantCulture));

    private readonly record struct MotionCoverage(
        double TotalAngularMotionDegrees,
        double RotationAxisCoverage,
        double PositionExtentMeters);
}
