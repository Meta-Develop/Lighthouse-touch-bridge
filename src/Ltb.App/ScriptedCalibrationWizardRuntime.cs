using System.Numerics;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.App;

/// <summary>
/// Deterministic Linux-safe fake device session used by the documented wizard
/// demo and integration tests. It makes no OpenVR, VMT, or settings calls.
/// </summary>
public sealed class ScriptedCalibrationWizardRuntime : ICalibrationWizardRuntime
{
    public const string LeftControllerSerial = "CTRL-TEST-L";
    public const string RightControllerSerial = "CTRL-TEST-R";
    public const string LeftTrackerSerial = "LHR-TEST0001";
    public const string RightTrackerSerial = "LHR-TEST0002";

    private readonly ICalibrationWizardOutput _output;
    private readonly CalibrationWizardDeviceSet _devices;

    public ScriptedCalibrationWizardRuntime(
        ICalibrationWizardOutput output,
        CalibrationWizardRecalibrationObservations? recalibration = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _devices = Devices with
        {
            Recalibration = recalibration ?? Devices.Recalibration,
        };
    }

    public static CalibrationWizardDeviceSet Devices { get; } = new(
        LeftControllerSerial,
        RightControllerSerial,
        // Deliberately reversed: association must follow motion, not this order.
        [RightTrackerSerial, LeftTrackerSerial])
    {
        Recalibration = new CalibrationWizardRecalibrationObservations
        {
            ObservedLeftTrackerSerial = LeftTrackerSerial,
            ObservedRightTrackerSerial = RightTrackerSerial,
        },
    };

    public List<CalibrationWizardHand> CapturedHands { get; } = [];

    public IReadOnlyList<CalibrationWizardProfileView> AppliedProfiles { get; private set; } =
        Array.Empty<CalibrationWizardProfileView>();

    public Task<CalibrationWizardDependencyStatus> CheckDependenciesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CalibrationWizardDependencyStatus(
            true,
            true,
            "scripted ALVR and VMT dependency checks passed"));
    }

    public Task WaitForSteamVrAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine("scripted_runtime: SteamVR ready (no native runtime opened)");
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardDeviceSet> WaitForDevicesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine(
            $"scripted_devices: controllers={LeftControllerSerial},{RightControllerSerial} " +
            $"trackers={RightTrackerSerial},{LeftTrackerSerial}");
        return Task.FromResult(_devices);
    }

    public Task ReleaseOverridesAsync(
        CalibrationWizardDeviceSet devices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.WriteLine("override_release: scripted hand overrides released");
        return Task.CompletedTask;
    }

    public Task<CalibrationWizardCapture> CaptureAsync(
        CalibrationWizardHand hand,
        CalibrationWizardDeviceSet devices,
        IProgress<CalibrationWizardCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CapturedHands.Add(hand);
        var capture = ScriptedWizardCaptureFactory.Create(hand);
        var controller = capture.Recording.Streams.Single(
            stream => stream.Identity.SourceKind == PoseSourceKind.InputController);
        var controllerSamples = controller.Samples
            .Select(sample => sample.PoseSample)
            .ToArray();
        foreach (var prefixLength in new[] { 30, 120, controllerSamples.Length }
                     .Distinct())
        {
            var coverage = MotionCoverageAnalyzer.Evaluate(
                controllerSamples.Take(prefixLength).ToArray());
            progress.Report(new CalibrationWizardCaptureProgress(
                hand,
                coverage.SampleCount,
                coverage.OrientationValidityFraction,
                coverage.PositionValidityFraction,
                coverage.RotationAxisCoverage,
                coverage.TotalRotationDegrees,
                coverage.RotationProgress,
                coverage.PositionProgress,
                coverage.IsRotationSufficient,
                coverage.IsPositionSufficient,
                CoverageDiagnostic(coverage)));
        }

        return Task.FromResult(capture);
    }

    public Task ApplyProfilesAsync(
        IReadOnlyList<CalibrationWizardProfileView> profiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profiles.Count != 2 ||
            profiles.Any(profile => !_devices.TrackerSerials.Contains(
                profile.TrackerSerial,
                StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "The scripted runtime received profiles that do not match its two trackers.");
        }

        AppliedProfiles = profiles.ToArray();
        _output.WriteLine("apply_result: scripted two-hand transforms and overrides applied");
        return Task.CompletedTask;
    }

    private static string CoverageDiagnostic(MotionCoverageResult coverage)
    {
        if (!coverage.IsRotationSufficient)
        {
            return "continue multi-axis rotation for capture readiness";
        }

        return coverage.IsPositionSufficient
            ? "rotation and position capture gates passed"
            : "rotation capture gates passed; position unavailable, so Auto may fall back normally";
    }
}

internal static class ScriptedWizardCaptureFactory
{
    private const int SampleCount = 360;
    private const double SampleRateHz = 90d;
    private const double ControllerLagSeconds = 0.012d;

    private static readonly RigidTransform QuestFromLighthouse = new(
        Quaternion.CreateFromYawPitchRoll(0.42f, -0.31f, 0.19f),
        new Vector3(0.7f, -0.2f, 1.1f));

    private static readonly RigidTransform LeftMount = new(
        Quaternion.CreateFromYawPitchRoll(0.25f, -0.18f, 0.12f),
        new Vector3(0.014f, -0.052f, 0.031f));

    private static readonly RigidTransform RightMount = new(
        Quaternion.CreateFromYawPitchRoll(-0.35f, 0.22f, -0.14f),
        new Vector3(-0.018f, -0.047f, 0.026f));

    public static CalibrationWizardCapture Create(CalibrationWizardHand hand)
    {
        var controllerSerial = hand == CalibrationWizardHand.Left
            ? ScriptedCalibrationWizardRuntime.LeftControllerSerial
            : ScriptedCalibrationWizardRuntime.RightControllerSerial;
        var activeTrackerSerial = hand == CalibrationWizardHand.Left
            ? ScriptedCalibrationWizardRuntime.LeftTrackerSerial
            : ScriptedCalibrationWizardRuntime.RightTrackerSerial;
        var inactiveTrackerSerial = hand == CalibrationWizardHand.Left
            ? ScriptedCalibrationWizardRuntime.RightTrackerSerial
            : ScriptedCalibrationWizardRuntime.LeftTrackerSerial;
        var mount = hand == CalibrationWizardHand.Left ? LeftMount : RightMount;
        var controllerPositionAvailable = hand == CalibrationWizardHand.Left;
        var phase = hand == CalibrationWizardHand.Left ? 0f : 0.63f;

        var controllerSamples = new RecordedPoseSample[SampleCount];
        var activeTrackerSamples = new RecordedPoseSample[SampleCount];
        var inactiveTrackerSamples = new RecordedPoseSample[SampleCount];
        for (var index = 0; index < SampleCount; index++)
        {
            var relativeTime = index / SampleRateHz;
            var trackerTime = 1d + relativeTime;
            var trackerPose = TrackerPose(relativeTime, phase);
            var controllerPose = QuestFromLighthouse * trackerPose * mount;
            activeTrackerSamples[index] = Recorded(
                trackerTime,
                trackerPose,
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);
            controllerSamples[index] = Recorded(
                trackerTime + ControllerLagSeconds,
                controllerPose,
                PoseValidity.Orientation |
                PoseValidity.TrackingValid |
                (controllerPositionAvailable ? PoseValidity.Position : PoseValidity.None));
            inactiveTrackerSamples[index] = Recorded(
                trackerTime,
                RigidTransform.Identity,
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);
        }

        // Preserve reversed candidate enumeration in both guided captures.
        var trackerStreams = activeTrackerSerial == ScriptedCalibrationWizardRuntime.RightTrackerSerial
            ? new[]
            {
                TrackerStream(activeTrackerSerial, activeTrackerSamples),
                TrackerStream(inactiveTrackerSerial, inactiveTrackerSamples),
            }
            : new[]
            {
                TrackerStream(inactiveTrackerSerial, inactiveTrackerSamples),
                TrackerStream(activeTrackerSerial, activeTrackerSamples),
            };
        return new CalibrationWizardCapture(
            hand,
            new PoseRecording(
            [
                ControllerStream(hand, controllerSerial, controllerSamples),
                .. trackerStreams,
            ]));
    }

    private static PoseStreamRecording ControllerStream(
        CalibrationWizardHand hand,
        string serial,
        IReadOnlyList<RecordedPoseSample> samples) =>
        new(
            new PoseStreamIdentity(
                $"controller-{hand.ToString().ToLowerInvariant()}",
                PoseSourceKind.InputController,
                serial,
                $"Scripted {hand} Meta Touch"),
            samples);

    private static PoseStreamRecording TrackerStream(
        string serial,
        IReadOnlyList<RecordedPoseSample> samples) =>
        new(
            new PoseStreamIdentity(
                $"tracker-{serial}",
                PoseSourceKind.TrackedPose,
                serial,
                "Scripted Lighthouse tracker"),
            samples);

    private static RecordedPoseSample Recorded(
        double timestamp,
        RigidTransform pose,
        PoseValidity validity) =>
        new(
            new TimestampedPoseSample(timestamp, pose, validity),
            true,
            PoseTrackingResult.RunningOk);

    private static RigidTransform TrackerPose(double time, float phase)
    {
        var seconds = (float)time;
        var yaw = 0.85f * MathF.Sin((2f * MathF.PI * 0.43f * seconds) + phase);
        var pitch = 0.68f * MathF.Sin((2f * MathF.PI * 0.71f * seconds) + 0.31f + phase);
        var roll = 0.78f * MathF.Sin((2f * MathF.PI * 0.89f * seconds) + 0.83f + phase);
        var position = new Vector3(
            0.23f * MathF.Sin((2f * MathF.PI * 0.29f * seconds) + phase),
            0.18f * MathF.Cos((2f * MathF.PI * 0.37f * seconds) + phase),
            1.15f + (0.13f * MathF.Sin((2f * MathF.PI * 0.23f * seconds) + phase)));
        return new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll),
            position);
    }
}
