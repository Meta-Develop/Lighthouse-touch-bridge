using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class LiveRecorderCaptureTests
{
    [Fact]
    public void RepeatableSelectorsPreserveAllDistinctStableSerials()
    {
        var parsed = AppCommandLineOptions.TryParse(
            [
                "record",
                "--tracker", "tracker-b",
                "--controller", "controller-left",
                "--tracker", "tracker-a",
                "--controller", "controller-right",
                "--output", "synthetic.json",
                "--override-released",
            ],
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal(["tracker-b", "tracker-a"], options.TrackerSerials);
        Assert.Equal(["controller-left", "controller-right"], options.ControllerSerials);
    }

    [Theory]
    [InlineData("tracker-a", "tracker-a")]
    [InlineData("shared-serial", "shared-serial")]
    public void DuplicateSelectorsAreRejectedClearly(string firstTracker, string repeatedSerial)
    {
        var parsed = AppCommandLineOptions.TryParse(
            [
                "record",
                "--tracker", firstTracker,
                "--tracker", repeatedSerial,
                "--controller", "shared-serial",
                "--output", "synthetic.json",
                "--override-released",
            ],
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Contains("selected more than once", error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidStableSerialAndPoseSourceCapabilitySelectionsAreRejectedClearly()
    {
        var tracker = Tracker("tracker-a", 1);
        var controller = Controller("controller-left", 2, SteamVrControllerRole.LeftHand);
        var devices = new[] { tracker, controller };

        var missing = Assert.Throws<ArgumentException>(() => Program.SelectPhysicalPoseSources(
            devices,
            ["missing"],
            "tracker"));
        var incompatible = Assert.Throws<ArgumentException>(() =>
            Program.SelectPhysicalPoseSources(
                devices,
                ["controller-left"],
                "tracker"));

        Assert.Contains("No SteamVR device matches --tracker serial 'missing'", missing.Message);
        Assert.Contains(
            "not a connected, position-capable physical Lighthouse pose source",
            incompatible.Message);
    }

    [Fact]
    public void PhysicalPoseSourceSelectionUsesCapabilitiesInsteadOfDeviceClassOrModel()
    {
        var futureGenericDevice = new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(
                "generic-pose-source",
                "/devices/lighthouse/generic-pose-source"),
            7,
            SteamVrDeviceCategory.Unknown,
            SteamVrControllerRole.None,
            true,
            metadata: null,
            capabilities: new SteamVrDeviceCapabilities(
                hasPosition: true,
                isPhysicalPoseSourceEligible: true,
                isVirtualPoseSource: false));
        var vmtOutput = new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity("VMT-1", "/devices/vmt/VMT_1"),
            8,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);

        var selected = Program.SelectPhysicalPoseSources(
            [futureGenericDevice, vmtOutput],
            [futureGenericDevice.StableDeviceId],
            "tracker");

        Assert.Equal(futureGenericDevice, Assert.Single(selected));
        var virtualFailure = Assert.Throws<ArgumentException>(() =>
            Program.SelectPhysicalPoseSources(
                [futureGenericDevice, vmtOutput],
                [vmtOutput.StableDeviceId],
                "tracker"));
        Assert.Contains("physical Lighthouse pose source", virtualFailure.Message);
    }

    [Fact]
    public void MultiSourceCaptureExportsEveryStreamWithStableIdentityAndMetadata()
    {
        var trackerA = Samples(10d, new Vector3(1f, 0f, 0f), PoseTrackingResult.RunningOk);
        var trackerB = Samples(20d, new Vector3(2f, 0f, 0f), PoseTrackingResult.RunningOutOfRange);
        var controllerLeft = Samples(30d, new Vector3(3f, 0f, 0f), PoseTrackingResult.CalibratingInProgress);
        var controllerRight = Samples(40d, new Vector3(4f, 0f, 0f), PoseTrackingResult.FallbackRotationOnly);
        var clock = new FakeRecordingCaptureClock();

        var capture = PoseRecordingCapture.Capture(
            [
                new SimulatedTrackedPoseSource(Tracker("tracker-b", 8), trackerB),
                new SimulatedTrackedPoseSource(Tracker("tracker-a", 4), trackerA),
            ],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-right", 12, SteamVrControllerRole.RightHand),
                    controllerRight),
                new SimulatedInputControllerPoseSource(
                    Controller("controller-left", 6, SteamVrControllerRole.LeftHand),
                    controllerLeft),
            ],
            durationSeconds: 1d,
            sampleRateHz: 3d,
            clock);

        Assert.Equal(3, capture.SamplingTicks);
        Assert.Equal(1d, capture.CaptureElapsedSeconds);
        Assert.Equal([0d, 1d / 3d, 2d / 3d, 1d], clock.WaitTargets);
        Assert.Equal(
            [
                "tracker:tracker-b",
                "tracker:tracker-a",
                "controller:controller-right",
                "controller:controller-left",
            ],
            capture.Recording.Streams.Select(stream => stream.Identity.StreamId));
        Assert.Equal(
            ["tracker-b", "tracker-a", "controller-right", "controller-left"],
            capture.Recording.Streams.Select(stream => stream.Identity.DeviceId));

        AssertStream(capture.Recording.GetStream("tracker:tracker-b"), PoseSourceKind.TrackedPose, trackerB);
        AssertStream(capture.Recording.GetStream("tracker:tracker-a"), PoseSourceKind.TrackedPose, trackerA);
        AssertStream(
            capture.Recording.GetStream("controller:controller-right"),
            PoseSourceKind.InputController,
            controllerRight);
        AssertStream(
            capture.Recording.GetStream("controller:controller-left"),
            PoseSourceKind.InputController,
            controllerLeft);

        var roundTripped = PoseRecordingJson.Deserialize(
            PoseRecordingJson.Serialize(capture.Recording));
        Assert.Equal(
            capture.Recording.Streams.Select(stream => stream.Identity),
            roundTripped.Streams.Select(stream => stream.Identity));
        for (var streamIndex = 0; streamIndex < capture.Recording.Streams.Count; streamIndex++)
        {
            AssertRoundTrippedStream(
                capture.Recording.Streams[streamIndex],
                roundTripped.Streams[streamIndex]);
        }
    }

    [Fact]
    public void OnePairCaptureKeepsLegacyStreamIds()
    {
        var clock = new FakeRecordingCaptureClock();
        var capture = PoseRecordingCapture.Capture(
            [new SimulatedTrackedPoseSource(Tracker("tracker-one", 1), Samples(1d))],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-one", 2, SteamVrControllerRole.LeftHand),
                    Samples(2d)),
            ],
            durationSeconds: 1d,
            sampleRateHz: 3d,
            clock);

        Assert.Equal(["tracker", "controller"],
            capture.Recording.Streams.Select(stream => stream.Identity.StreamId));
        Assert.Equal("tracker-one", capture.Recording.GetStream("tracker").Identity.DeviceId);
        Assert.Equal("controller-one", capture.Recording.GetStream("controller").Identity.DeviceId);
    }

    [Fact]
    public void LowRateCaptureRemainsActiveUntilDeadlineWithoutRealSleeping()
    {
        var clock = new FakeRecordingCaptureClock();
        var capture = PoseRecordingCapture.Capture(
            [new SimulatedTrackedPoseSource(Tracker("tracker-one", 1), [Sample(1d)])],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-one", 2, SteamVrControllerRole.LeftHand),
                    [Sample(2d)]),
            ],
            durationSeconds: 0.25d,
            sampleRateHz: 0.5d,
            clock);

        Assert.Equal(1, capture.SamplingTicks);
        Assert.All(capture.Recording.Streams, stream => Assert.Single(stream.Samples));
        Assert.Equal([0d, 0.25d], clock.WaitTargets);
        Assert.Equal(0.25d, capture.CaptureElapsedSeconds);
    }

    [Theory]
    [InlineData(0.25d)]
    [InlineData(0.3d)]
    public void InitialWaitAtOrBeyondDeadlineProducesNoSamples(double elapsedAfterInitialWait)
    {
        var clock = new FakeRecordingCaptureClock(elapsedAfterInitialWait);
        var capture = PoseRecordingCapture.Capture(
            [new SimulatedTrackedPoseSource(Tracker("tracker-one", 1), [Sample(1d)])],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-one", 2, SteamVrControllerRole.LeftHand),
                    [Sample(2d)]),
            ],
            durationSeconds: 0.25d,
            sampleRateHz: 10d,
            clock);

        Assert.Equal(0, capture.SamplingTicks);
        Assert.All(capture.Recording.Streams, stream => Assert.Empty(stream.Samples));
        Assert.Equal([0d, 0.25d], clock.WaitTargets);
        Assert.Equal(elapsedAfterInitialWait, capture.CaptureElapsedSeconds);
    }

    [Fact]
    public void IntermediateOvershootSkipsMissedTicksWithoutBurstCatchUp()
    {
        var clock = new FakeRecordingCaptureClock(0d, 0.35d);
        var capture = PoseRecordingCapture.Capture(
            [new SimulatedTrackedPoseSource(Tracker("tracker-one", 1), Samples(1d, count: 4))],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-one", 2, SteamVrControllerRole.LeftHand),
                    Samples(10d, count: 4)),
            ],
            durationSeconds: 0.55d,
            sampleRateHz: 10d,
            clock);

        Assert.Equal(4, capture.SamplingTicks);
        Assert.All(capture.Recording.Streams, stream => Assert.Equal(4, stream.Samples.Count));
        Assert.Equal([0d, 0.1d, 0.4d, 0.5d, 0.55d], clock.WaitTargets);
        Assert.Equal(0.55d, capture.CaptureElapsedSeconds);
    }

    [Fact]
    public void CaptureRejectsADeviceSelectedForMoreThanOneStream()
    {
        var descriptor = Tracker("tracker-duplicate", 1);
        var source = new SimulatedTrackedPoseSource(descriptor, [Sample(1d)]);

        var exception = Assert.Throws<ArgumentException>(() => PoseRecordingCapture.Capture(
            [source, source],
            [
                new SimulatedInputControllerPoseSource(
                    Controller("controller-one", 2, SteamVrControllerRole.LeftHand),
                    [Sample(2d)]),
            ],
            durationSeconds: 0.25d,
            sampleRateHz: 1d,
            new FakeRecordingCaptureClock()));

        Assert.Contains("tracker-duplicate", exception.Message);
        Assert.Contains("selected more than once", exception.Message);
    }

    private static PoseSourceSample[] Samples(
        double firstTimestamp,
        Vector3? firstPosition = null,
        PoseTrackingResult trackingResult = PoseTrackingResult.RunningOk,
        int count = 3) =>
        Enumerable.Range(0, count)
            .Select(index => Sample(
                firstTimestamp + index,
                (firstPosition ?? Vector3.Zero) + new Vector3(0f, index, index % 2),
                trackingResult,
                isConnected: index % 2 == 0,
                sampleAgeSeconds: 0.001d * (index + 1)))
            .ToArray();

    private static PoseSourceSample Sample(
        double timestamp,
        Vector3? position = null,
        PoseTrackingResult trackingResult = PoseTrackingResult.RunningOk,
        bool isConnected = true,
        double sampleAgeSeconds = 0.001d) =>
        new(
            new TimestampedPoseSample(
                timestamp,
                new RigidTransform(
                    Quaternion.CreateFromYawPitchRoll(0.2f, -0.1f, 0.3f),
                    position ?? Vector3.Zero),
                PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid),
            isConnected,
            trackingResult,
            runtimeTimeSeconds: timestamp - sampleAgeSeconds,
            predictionOffsetSeconds: 0.01d,
            sampleAgeSeconds);

    private static SteamVrDeviceDescriptor Tracker(string serial, uint index) =>
        new(
            new SteamVrDeviceIdentity(serial, $"lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            true);

    private static SteamVrDeviceDescriptor Controller(
        string serial,
        uint index,
        SteamVrControllerRole role) =>
        new(
            new SteamVrDeviceIdentity(serial, $"controller/{serial}"),
            index,
            SteamVrDeviceCategory.InputController,
            role,
            true);

    private static void AssertStream(
        PoseStreamRecording stream,
        PoseSourceKind expectedKind,
        IReadOnlyList<PoseSourceSample> expected)
    {
        Assert.Equal(expectedKind, stream.Identity.SourceKind);
        Assert.Equal(expected.Count, stream.Samples.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].ToRecordedPoseSample(), stream.Samples[index]);
        }
    }

    private static void AssertRoundTrippedStream(
        PoseStreamRecording expected,
        PoseStreamRecording actual)
    {
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        for (var sampleIndex = 0; sampleIndex < expected.Samples.Count; sampleIndex++)
        {
            var expectedSample = expected.Samples[sampleIndex];
            var actualSample = actual.Samples[sampleIndex];
            Assert.Equal(expectedSample.MonotonicHostTimeSeconds, actualSample.MonotonicHostTimeSeconds);
            Assert.Equal(expectedSample.Validity, actualSample.Validity);
            Assert.Equal(expectedSample.IsConnected, actualSample.IsConnected);
            Assert.Equal(expectedSample.TrackingResult, actualSample.TrackingResult);
            Assert.Equal(expectedSample.RuntimeTimeSeconds, actualSample.RuntimeTimeSeconds);
            Assert.Equal(expectedSample.PredictionOffsetSeconds, actualSample.PredictionOffsetSeconds);
            Assert.Equal(expectedSample.SampleAgeSeconds, actualSample.SampleAgeSeconds);
            Assert.InRange(
                Vector3.Distance(
                    expectedSample.Pose.TranslationMeters,
                    actualSample.Pose.TranslationMeters),
                0f,
                1e-6f);
            Assert.InRange(
                QuaternionAngularDistance(
                    expectedSample.Pose.Rotation,
                    actualSample.Pose.Rotation),
                0d,
                1e-5d);
        }
    }

    private static double QuaternionAngularDistance(Quaternion first, Quaternion second)
    {
        var dot = Math.Clamp(Math.Abs(Quaternion.Dot(first, second)), 0f, 1f);
        return 2d * Math.Acos(dot);
    }
}

internal sealed class FakeRecordingCaptureClock : RecordingCaptureClock
{
    private readonly List<double> _waitTargets = [];
    private readonly double[] _elapsedAfterWaits;
    private int _nextElapsedOverride;

    public FakeRecordingCaptureClock(params double[] elapsedAfterWaits)
    {
        _elapsedAfterWaits = elapsedAfterWaits;
    }

    public double ElapsedSeconds { get; private set; }

    public IReadOnlyList<double> WaitTargets => _waitTargets;

    public void Restart()
    {
        ElapsedSeconds = 0d;
        _waitTargets.Clear();
        _nextElapsedOverride = 0;
    }

    public void WaitUntil(double targetElapsedSeconds)
    {
        _waitTargets.Add(targetElapsedSeconds);
        var elapsedAfterWait = _nextElapsedOverride < _elapsedAfterWaits.Length
            ? _elapsedAfterWaits[_nextElapsedOverride++]
            : targetElapsedSeconds;
        ElapsedSeconds = Math.Max(
            ElapsedSeconds,
            Math.Max(targetElapsedSeconds, elapsedAfterWait));
    }
}
