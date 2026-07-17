using System.Globalization;
using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.RecordingInspector;

internal static class Program
{
    private const int MaximumAxisSamples = 256;

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (!InspectorCommandLineOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            InspectorCommandLineOptions.PrintUsage(Console.Error);
            return 1;
        }

        if (options.ShowHelp)
        {
            InspectorCommandLineOptions.PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            using var source = new FileStream(
                options.RecordingPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var recording = PoseRecordingJson.Read(source);
            return options.Command switch
            {
                InspectorCommand.Inspect => Inspect(recording, options),
                InspectorCommand.Replay => Replay(recording, options),
                _ => throw new InvalidOperationException("Unsupported inspector command."),
            };
        }
        catch (LagEstimationException exception)
        {
            Console.Error.WriteLine(
                $"Lag estimation rejected ({exception.Reason}): {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            PoseRecordingFormatException or
            NotSupportedException or
            ArgumentException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            Console.Error.WriteLine($"Recording command failed: {exception.Message}");
            return 2;
        }
    }

    private static int Inspect(PoseRecording recording, InspectorCommandLineOptions options)
    {
        Console.WriteLine("Lighthouse Touch Bridge - Recording Inspector");
        Console.WriteLine($"format: {PoseRecording.FormatIdentifier}");
        Console.WriteLine($"schema_version: {recording.SchemaVersion}");
        Console.WriteLine($"stream_count: {recording.Streams.Count}");

        foreach (var stream in recording.Streams)
        {
            PrintStreamSummary(stream, options.RevealDeviceIds);
        }

        if (!TrySelectPair(recording, options, out var tracker, out var controller, out var diagnostic))
        {
            Console.WriteLine();
            Console.WriteLine($"lag_estimate: unavailable ({diagnostic})");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"lag_pair: tracker={tracker!.Identity.StreamId}, controller={controller!.Identity.StreamId}");
        try
        {
            var lag = StreamLagEstimator.EstimateControllerLag(
                ToReplaySamples(tracker),
                ToReplaySamples(controller));
            PrintLag(lag);
        }
        catch (LagEstimationException exception)
        {
            Console.WriteLine($"lag_estimate: rejected ({exception.Reason}: {exception.Message})");
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine($"lag_estimate: unavailable ({exception.Message})");
        }

        return 0;
    }

    private static int Replay(PoseRecording recording, InspectorCommandLineOptions options)
    {
        if (!TrySelectPair(recording, options, out var tracker, out var controller, out var diagnostic))
        {
            throw new ArgumentException(diagnostic);
        }

        var replay = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions(
                tracker!.Identity.StreamId,
                controller!.Identity.StreamId,
                options.Policy));

        Console.WriteLine("Lighthouse Touch Bridge - Recording Replay");
        Console.WriteLine($"format: {PoseRecording.FormatIdentifier}");
        Console.WriteLine($"schema_version: {recording.SchemaVersion}");
        Console.WriteLine($"tracker_stream: {tracker.Identity.StreamId}");
        Console.WriteLine($"controller_stream: {controller.Identity.StreamId}");
        PrintLag(replay.Lag);
        Console.WriteLine($"aligned_pairs: {replay.AlignedPairs.Count}");
        Console.WriteLine();
        Console.WriteLine(new CalibrationReport(replay.Calibration));
        return replay.Calibration.Success ? 0 : 2;
    }

    private static void PrintStreamSummary(PoseStreamRecording stream, bool revealDeviceIds)
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
        var trackingRatio = Ratio(samples, sample => sample.Validity.HasFlag(PoseValidity.TrackingValid));
        var connectivityRatio = Ratio(samples, sample => sample.IsConnected);
        var motion = ComputeMotionCoverage(samples);

        Console.WriteLine();
        Console.WriteLine($"stream: {stream.Identity.StreamId}");
        Console.WriteLine($"  source_kind: {FormatSourceKind(stream.Identity.SourceKind)}");
        Console.WriteLine($"  device_id: {(revealDeviceIds ? stream.Identity.DeviceId : "[redacted]")}");
        Console.WriteLine($"  display_name: {stream.Identity.DisplayName ?? "n/a"}");
        Console.WriteLine($"  samples: {samples.Count}");
        Console.WriteLine($"  duration_seconds: {duration:F6}");
        Console.WriteLine($"  nominal_rate_hz: {rate:F3}");
        Console.WriteLine($"  orientation_channel_ratio: {orientationChannelRatio:P2}");
        Console.WriteLine($"  position_channel_ratio: {positionChannelRatio:P2}");
        Console.WriteLine($"  orientation_valid_ratio: {orientationValidRatio:P2}");
        Console.WriteLine($"  position_valid_ratio: {positionValidRatio:P2}");
        Console.WriteLine($"  tracking_validity_ratio: {trackingRatio:P2}");
        Console.WriteLine($"  connectivity_ratio: {connectivityRatio:P2}");
        Console.WriteLine($"  orientation_motion_degrees: {motion.TotalAngularMotionDegrees:F3}");
        Console.WriteLine($"  rotation_axis_coverage: {motion.RotationAxisCoverage:F6}");
        Console.WriteLine($"  position_extent_meters: {motion.PositionExtentMeters:F6}");
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

    private static bool TrySelectPair(
        PoseRecording recording,
        InspectorCommandLineOptions options,
        out PoseStreamRecording? tracker,
        out PoseStreamRecording? controller,
        out string diagnostic)
    {
        tracker = ResolveExplicitStream(
            recording,
            options.TrackerStreamId,
            PoseSourceKind.TrackedPose,
            "tracker");
        controller = ResolveExplicitStream(
            recording,
            options.ControllerStreamId,
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

    private static IReadOnlyList<TimestampedPoseSample> ToReplaySamples(PoseStreamRecording stream) =>
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

    private static void PrintLag(LagEstimate lag)
    {
        Console.WriteLine($"controller_lag_ms: {lag.LagSeconds * 1_000d:F3}");
        Console.WriteLine($"lag_correlation: {lag.CorrelationScore:F6}");
        Console.WriteLine($"lag_confidence: {lag.Confidence:F6}");
        Console.WriteLine($"lag_compared_samples: {lag.ComparedSampleCount}");
        Console.WriteLine($"coarse_correlation_lag_ms: {FormatMilliseconds(lag.CoarseLagSeconds)}");
        Console.WriteLine($"coarse_correlation_score: {FormatFinite(lag.CoarseCorrelationScore)}");
        Console.WriteLine($"runner_up_correlation_score: {FormatFinite(lag.RunnerUpCorrelationScore)}");
        Console.WriteLine($"correlation_peak_prominence: {FormatFinite(lag.PeakProminence)}");
        Console.WriteLine(
            $"correlation_interval_ms: [{FormatMilliseconds(lag.CorrelationIntervalMinimumSeconds)}, {FormatMilliseconds(lag.CorrelationIntervalMaximumSeconds)}]");
        Console.WriteLine($"provisional_rotation_lag_ms: {FormatMilliseconds(lag.ProvisionalRotationLagSeconds)}");
        Console.WriteLine($"coarse_rotation_residual_deg: {FormatFinite(lag.CoarseRotationResidualDegrees)}");
        Console.WriteLine($"refined_rotation_residual_deg: {FormatFinite(lag.RefinedRotationResidualDegrees)}");
        Console.WriteLine($"evaluated_candidate_count: {lag.EvaluatedCandidateLagsSeconds.Count}");
    }

    private static string FormatMilliseconds(double seconds) =>
        double.IsFinite(seconds) ? (seconds * 1_000d).ToString("F3", CultureInfo.InvariantCulture) : "unavailable";

    private static string FormatFinite(double value) =>
        double.IsFinite(value) ? value.ToString("F6", CultureInfo.InvariantCulture) : "unavailable";

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

    private readonly record struct MotionCoverage(
        double TotalAngularMotionDegrees,
        double RotationAxisCoverage,
        double PositionExtentMeters);
}

internal sealed record InspectorCommandLineOptions(
    InspectorCommand Command,
    string? RecordingPath,
    string? TrackerStreamId,
    string? ControllerStreamId,
    CalibrationPolicy Policy,
    bool RevealDeviceIds,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out InspectorCommandLineOptions options,
        out string? error)
    {
        options = Empty;
        error = null;
        if (arguments.Count == 1 && arguments[0] is "-h" or "--help")
        {
            options = Empty with { ShowHelp = true };
            return true;
        }

        if (arguments.Count == 0 || !TryParseCommand(arguments[0], out var command))
        {
            error = arguments.Count == 0
                ? "An inspect or replay command is required."
                : $"Unknown command '{arguments[0]}'.";
            return false;
        }

        if (arguments.Count == 2 && arguments[1] is "-h" or "--help")
        {
            options = Empty with { Command = command, ShowHelp = true };
            return true;
        }

        if (arguments.Count < 2 || arguments[1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"The {arguments[0]} command requires a recording path.";
            return false;
        }

        var recordingPath = arguments[1];
        string? trackerStreamId = null;
        string? controllerStreamId = null;
        var policy = CalibrationPolicy.Auto;
        var policySpecified = false;
        var revealDeviceIds = false;
        var showHelp = false;
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--tracker":
                    if (!TryReadValue(arguments, ref index, argument, out trackerStreamId, out error))
                    {
                        return false;
                    }

                    break;
                case "--controller":
                    if (!TryReadValue(arguments, ref index, argument, out controllerStreamId, out error))
                    {
                        return false;
                    }

                    break;
                case "--policy":
                    if (!TryReadValue(arguments, ref index, argument, out var policyValue, out error) ||
                        !TryParsePolicy(policyValue!, out policy))
                    {
                        error ??= $"Unknown policy '{policyValue}'.";
                        return false;
                    }

                    policySpecified = true;
                    break;
                case "--reveal-device-ids":
                    revealDeviceIds = true;
                    break;
                default:
                    error = $"Unknown option '{argument}'.";
                    return false;
            }
        }

        if (command == InspectorCommand.Inspect && policySpecified)
        {
            error = "--policy is only valid with replay.";
            return false;
        }

        options = new InspectorCommandLineOptions(
            command,
            recordingPath,
            trackerStreamId,
            controllerStreamId,
            policy,
            revealDeviceIds,
            showHelp);
        return true;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Lighthouse Touch Bridge recording inspector and replay utility");
        writer.WriteLine();
        writer.WriteLine("Repository-root usage:");
        writer.WriteLine("  dotnet run --project tools/Ltb.RecordingInspector -- inspect <recording.json> [--tracker <stream-id>] [--controller <stream-id>] [--reveal-device-ids]");
        writer.WriteLine("  dotnet run --project tools/Ltb.RecordingInspector -- replay <recording.json> [--tracker <stream-id>] [--controller <stream-id>] [--policy auto|rotation|full]");
        writer.WriteLine();
        writer.WriteLine("Device IDs are redacted by default from inspect output.");
    }

    private static InspectorCommandLineOptions Empty { get; } = new(
        InspectorCommand.Inspect,
        null,
        null,
        null,
        CalibrationPolicy.Auto,
        false,
        false);

    private static bool TryParseCommand(string value, out InspectorCommand command)
    {
        command = value switch
        {
            "inspect" => InspectorCommand.Inspect,
            "replay" => InspectorCommand.Replay,
            _ => (InspectorCommand)(-1),
        };
        return Enum.IsDefined(command);
    }

    private static bool TryParsePolicy(string value, out CalibrationPolicy policy)
    {
        policy = value.ToLowerInvariant() switch
        {
            "auto" => CalibrationPolicy.Auto,
            "rotation" or "rotation-only" => CalibrationPolicy.RotationOnly,
            "full" or "full-6dof" or "full-six-dof" => CalibrationPolicy.FullSixDof,
            _ => (CalibrationPolicy)(-1),
        };
        return Enum.IsDefined(policy);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= arguments.Count)
        {
            value = null;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = arguments[++index];
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Option '{option}' requires a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }
}

internal enum InspectorCommand
{
    Inspect,
    Replay,
}
