using System.Numerics;
using Ltb.Core;

namespace Ltb.Calibration.Tests;

public sealed class CoordinateConventionTests
{
    private const float FloatTolerance = 1e-5f;

    [Fact]
    public void IdentityAndInverseRoundTripPoints()
    {
        var transform = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.4f, -0.7f, 0.2f),
            new Vector3(0.12f, -0.34f, 0.56f));
        var point = new Vector3(0.3f, -0.8f, 1.1f);

        AssertVectorNear(point, RigidTransform.Identity.TransformPoint(point));
        AssertVectorNear(point, transform.Inverse().TransformPoint(transform.TransformPoint(point)));
        AssertTransformNear(RigidTransform.Identity, transform * transform.Inverse());
    }

    [Fact]
    public void CompositionMatchesSequentialApplication()
    {
        var aFromB = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            new Vector3(1f, 2f, 3f));
        var bFromC = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3f),
            new Vector3(-0.2f, 0.4f, 0.8f));
        var pointInC = new Vector3(0.5f, -0.1f, 0.9f);

        var aFromC = aFromB.Compose(bFromC);

        AssertVectorNear(
            aFromB.TransformPoint(bFromC.TransformPoint(pointInC)),
            aFromC.TransformPoint(pointInC));
    }

    [Fact]
    public void RuntimeCompositionRotatesMountLeverArm()
    {
        var lighthouseFromTracker = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            new Vector3(1f, 2f, 3f));
        var mount = new RigidTransform(Quaternion.Identity, new Vector3(0.1f, 0f, 0f));

        var lighthouseFromOutput = CoordinateConventions.ComposeRuntimeOutput(
            lighthouseFromTracker,
            mount);

        AssertVectorNear(new Vector3(1f, 2.1f, 3f), lighthouseFromOutput.TranslationMeters);
        AssertQuaternionEquivalent(lighthouseFromTracker.Rotation, lighthouseFromOutput.Rotation);
    }

    [Fact]
    public void PublicConventionNamesFramesOrderHandednessAndUnits()
    {
        Assert.Equal("Q", CoordinateConventions.QuestWorldFrame);
        Assert.Equal("L", CoordinateConventions.LighthouseWorldFrame);
        Assert.Equal("C", CoordinateConventions.ControllerFrame);
        Assert.Equal("T", CoordinateConventions.TrackerFrame);
        Assert.Equal("T_parent_child", CoordinateConventions.TransformNotation);
        Assert.Equal("Y = T_Q_L", CoordinateConventions.CalibrationWorldTransformNotation);
        Assert.Equal("X_mount = T_T_C", CoordinateConventions.MountTransformNotation);
        Assert.Equal(
            "T_Q_C(i) = T_Q_L * T_L_T(i) * T_T_C",
            CoordinateConventions.SynchronizedCalibrationEquation);
        Assert.Equal("right-handed", CoordinateConventions.Handedness);
        Assert.Equal("XYZW", CoordinateConventions.QuaternionComponentOrder);
        Assert.Equal("meters", CoordinateConventions.LengthUnit);
        Assert.Equal("seconds", CoordinateConventions.TimeUnit);
        Assert.Equal(0, (int)PoseValidity.None);
        Assert.Equal(1, (int)PoseValidity.Orientation);
        Assert.Equal(2, (int)PoseValidity.Position);
        Assert.Equal(4, (int)PoseValidity.TrackingValid);
        Assert.Equal(
            "T_L_output(t) = T_L_tracker(t) * X_mount",
            CoordinateConventions.RuntimeCompositionEquation);
    }

    [Fact]
    public void ConstructionNormalizesEquivalentQuaternionSignsWithoutChangingTheTransform()
    {
        var positive = new RigidTransform(new Quaternion(0.2f, -0.4f, 0.1f, 0.8f), Vector3.Zero);
        var negative = new RigidTransform(new Quaternion(-0.4f, 0.8f, -0.2f, -1.6f), Vector3.Zero);
        var point = new Vector3(0.3f, -0.7f, 0.9f);

        AssertQuaternionEquivalent(positive.Rotation, negative.Rotation);
        AssertVectorNear(positive.TransformPoint(point), negative.TransformPoint(point));
        Assert.InRange(MathF.Abs(positive.Rotation.Length() - 1f), 0f, FloatTolerance);
        Assert.InRange(MathF.Abs(negative.Rotation.Length() - 1f), 0f, FloatTolerance);
        Assert.Throws<ArgumentException>(() => new RigidTransform(default, Vector3.Zero));
    }

    [Fact]
    public void PoseValidityRequiresTrackingAndChannelFlags()
    {
        var orientationOnly = CreateSample(
            10d,
            PoseValidity.Orientation | PoseValidity.TrackingValid);
        var untrackedFullChannels = CreateSample(
            10d,
            PoseValidity.Orientation | PoseValidity.Position);
        var trackedFullPose = CreateSample(
            10d,
            PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid);

        Assert.True(orientationOnly.HasValidOrientation);
        Assert.False(orientationOnly.HasValidPosition);
        Assert.False(untrackedFullChannels.HasValidOrientation);
        Assert.False(untrackedFullChannels.HasValidPosition);
        Assert.True(trackedFullPose.HasFullValidPose);
    }

    [Fact]
    public void SynchronizedPairAcceptsMatchedTimestampsWithinExplicitTolerance()
    {
        var tracker = CreateSample(42d, PoseValidity.Orientation | PoseValidity.TrackingValid);
        var controller = CreateSample(
            42d + SynchronizedPosePair.TimestampMatchToleranceSeconds * 0.5d,
            PoseValidity.Orientation | PoseValidity.TrackingValid);

        var pair = new SynchronizedPosePair(tracker, controller);

        Assert.Equal(tracker, pair.Tracker);
        Assert.Equal(controller, pair.Controller);
        Assert.InRange(
            Math.Abs(pair.TimeDeltaSeconds),
            0d,
            SynchronizedPosePair.TimestampMatchToleranceSeconds);
    }

    [Fact]
    public void SynchronizedPairRejectsUnmatchedTimestamps()
    {
        var tracker = CreateSample(42d, PoseValidity.Orientation | PoseValidity.TrackingValid);
        var controller = CreateSample(
            42d + SynchronizedPosePair.TimestampMatchToleranceSeconds * 2d,
            PoseValidity.Orientation | PoseValidity.TrackingValid);

        var exception = Assert.Throws<ArgumentException>(
            () => new SynchronizedPosePair(tracker, controller));

        Assert.Contains("timestamp-matched", exception.Message);
    }

    private static TimestampedPoseSample CreateSample(double time, PoseValidity validity) =>
        new(time, RigidTransform.Identity, validity);

    private static void AssertTransformNear(RigidTransform expected, RigidTransform actual)
    {
        AssertQuaternionEquivalent(expected.Rotation, actual.Rotation);
        AssertVectorNear(expected.TranslationMeters, actual.TranslationMeters);
    }

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(expected, actual));
        Assert.InRange(dot, 1f - FloatTolerance, 1f + FloatTolerance);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, FloatTolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, FloatTolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, FloatTolerance);
    }
}
