using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.Driver;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverHandStateComposerTests
{
    private static readonly PoseValidity FullPoseValidity =
        PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;

    [Fact]
    public void ComposesTrackerThenMountInExactContractOrder()
    {
        var driverFromTracker = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            new Vector3(1f, 2f, 3f));
        var trackerFromController = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 3f),
            new Vector3(2f, 0f, 0f));

        var state = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(2d, driverFromTracker),
            trackerFromController,
            ProtocolInputState.Neutral,
            inputsValid: true);

        var expected = driverFromTracker * trackerFromController;
        var reversed = trackerFromController * driverFromTracker;
        AssertVector(expected.TranslationMeters, state.DriverSpacePose.PositionMeters);
        AssertQuaternion(expected.Rotation, state.DriverSpacePose.OrientationXyzw);
        Assert.NotEqual(reversed.TranslationMeters.X, state.DriverSpacePose.PositionMeters.X);
        Assert.True(state.Presence.HasFlag(ProtocolPresence.Tracked));
    }

    [Fact]
    public void ComputesLeverArmLinearVelocityAndPreservesTrackerAngularVelocity()
    {
        var driverFromTracker = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            Vector3.Zero);
        var trackerFromController = new RigidTransform(
            Quaternion.Identity,
            new Vector3(2f, 0f, 0f));
        var trackerLinear = new Vector3(10f, 1f, 0f);
        var trackerAngular = new Vector3(0f, 0f, 3f);

        var state = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Right,
            TrackerSample(
                3d,
                driverFromTracker,
                linearVelocity: trackerLinear,
                angularVelocity: trackerAngular),
            trackerFromController,
            ProtocolInputState.Neutral,
            inputsValid: true);

        // R_driver_tracker * (2, 0, 0) = (0, 2, 0), then
        // omega x lever = (0, 0, 3) x (0, 2, 0) = (-6, 0, 0).
        AssertVector(new Vector3(4f, 1f, 0f), state.Motion.LinearVelocityMetersPerSecond);
        AssertVector(trackerAngular, state.Motion.AngularVelocityRadiansPerSecond);
        Assert.True(state.Presence.HasFlag(ProtocolPresence.LinearVelocityValid));
        Assert.True(state.Presence.HasFlag(ProtocolPresence.AngularVelocityValid));
    }

    [Fact]
    public void UsesTrackerSampleMonotonicTimeAsProtocolSourceTimestamp()
    {
        const double trackerSampleTime = 12.345678901d;

        var state = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(trackerSampleTime, RigidTransform.Identity),
            RigidTransform.Identity,
            ProtocolInputState.Neutral,
            inputsValid: true);

        Assert.Equal(12_345_678_901UL, state.SampleMonotonicNanoseconds);
    }

    [Fact]
    public void PublishesVelocityValidityOnlyForObservedAndComputableFiniteChannels()
    {
        var nonzeroMount = new RigidTransform(Quaternion.Identity, Vector3.UnitX);
        var noAngular = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(
                4d,
                RigidTransform.Identity,
                linearVelocity: Vector3.One,
                angularVelocity: null),
            nonzeroMount,
            ProtocolInputState.Neutral,
            inputsValid: true);
        Assert.False(noAngular.Presence.HasFlag(ProtocolPresence.LinearVelocityValid));
        Assert.False(noAngular.Presence.HasFlag(ProtocolPresence.AngularVelocityValid));
        Assert.Equal(ProtocolVector3.Zero, noAngular.Motion.LinearVelocityMetersPerSecond);
        Assert.Equal(ProtocolVector3.Zero, noAngular.Motion.AngularVelocityRadiansPerSecond);

        var noLinear = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(
                5d,
                RigidTransform.Identity,
                linearVelocity: null,
                angularVelocity: Vector3.UnitY),
            nonzeroMount,
            ProtocolInputState.Neutral,
            inputsValid: true);
        Assert.False(noLinear.Presence.HasFlag(ProtocolPresence.LinearVelocityValid));
        Assert.True(noLinear.Presence.HasFlag(ProtocolPresence.AngularVelocityValid));

        var zeroLeverArm = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(
                6d,
                RigidTransform.Identity,
                linearVelocity: Vector3.One,
                angularVelocity: null),
            RigidTransform.Identity,
            ProtocolInputState.Neutral,
            inputsValid: true);
        Assert.True(zeroLeverArm.Presence.HasFlag(ProtocolPresence.LinearVelocityValid));
        AssertVector(Vector3.One, zeroLeverArm.Motion.LinearVelocityMetersPerSecond);

        var overflow = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(
                7d,
                RigidTransform.Identity,
                linearVelocity: Vector3.Zero,
                angularVelocity: new Vector3(float.MaxValue, float.MaxValue, 0f)),
            new RigidTransform(
                Quaternion.Identity,
                new Vector3(float.MaxValue, 0f, float.MaxValue)),
            ProtocolInputState.Neutral,
            inputsValid: true);
        Assert.False(overflow.Presence.HasFlag(ProtocolPresence.LinearVelocityValid));
        Assert.True(overflow.Presence.HasFlag(ProtocolPresence.AngularVelocityValid));
        Assert.Equal(ProtocolVector3.Zero, overflow.Motion.LinearVelocityMetersPerSecond);
    }

    [Fact]
    public void InvalidTrackerPoseNeutralizesOnlyTheRequestedHandState()
    {
        var staleInput = new ProtocolInputState(
            ProtocolButtons.Primary,
            ProtocolTouches.Primary,
            1f,
            1f,
            1f,
            1f);
        var sample = TrackerSample(
            8d,
            new RigidTransform(Quaternion.Identity, new Vector3(9f, 8f, 7f)),
            validity: PoseValidity.Orientation | PoseValidity.Position,
            linearVelocity: Vector3.One,
            angularVelocity: Vector3.One);

        var state = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Right,
            sample,
            RigidTransform.Identity,
            staleInput,
            inputsValid: true);

        Assert.Equal(ProtocolHand.Right, state.Hand);
        Assert.Equal(ProtocolPresence.Connected, state.Presence);
        Assert.Equal(ProtocolInputState.Neutral, state.Input);
        Assert.Equal(ProtocolVector3.Zero, state.DriverSpacePose.PositionMeters);
        Assert.Equal(ProtocolQuaternion.Identity, state.DriverSpacePose.OrientationXyzw);
        Assert.Equal(ProtocolVector3.Zero, state.Motion.LinearVelocityMetersPerSecond);
        Assert.Equal(ProtocolVector3.Zero, state.Motion.AngularVelocityRadiansPerSecond);
        Assert.Equal(0f, state.BatteryLevel);
    }

    [Fact]
    public void InputValidityMatchesItsActualSourceWithoutInvalidatingHealthyPose()
    {
        var input = new ProtocolInputState(
            ProtocolButtons.Primary,
            ProtocolTouches.Trigger,
            0.7f,
            0.2f,
            0.1f,
            -0.1f);

        var invalidInput = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(9d, RigidTransform.Identity),
            RigidTransform.Identity,
            input,
            inputsValid: false);
        Assert.True(invalidInput.Presence.HasFlag(ProtocolPresence.Tracked));
        Assert.False(invalidInput.Presence.HasFlag(ProtocolPresence.InputsValid));
        Assert.Equal(ProtocolInputState.Neutral, invalidInput.Input);

        var validInput = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(10d, RigidTransform.Identity),
            RigidTransform.Identity,
            input,
            inputsValid: true);
        Assert.True(validInput.Presence.HasFlag(ProtocolPresence.InputsValid));
        Assert.Equal(input, validInput.Input);
    }

    [Fact]
    public void HealthyStateNeverAdvertisesBatteryOrHaptics()
    {
        var state = InternalDriverHandStateComposer.Compose(
            ProtocolHand.Left,
            TrackerSample(11d, RigidTransform.Identity),
            RigidTransform.Identity,
            ProtocolInputState.Neutral,
            inputsValid: true);

        Assert.False(state.Presence.HasFlag(ProtocolPresence.BatteryPresent));
        Assert.Equal(0f, state.BatteryLevel);
        Assert.DoesNotContain(
            typeof(DriverHandState).GetProperties(),
            property => property.Name.Contains("Haptic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsZeroTrackerTimeInsteadOfSubstitutingSendOrHeartbeatTime()
    {
        var sample = TrackerSample(0d, RigidTransform.Identity);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            InternalDriverHandStateComposer.Compose(
                ProtocolHand.Left,
                sample,
                RigidTransform.Identity,
                ProtocolInputState.Neutral,
                inputsValid: true));

        Assert.Equal("monotonicTimeSeconds", exception.ParamName);
    }

    private static PoseSourceSample TrackerSample(
        double monotonicTimeSeconds,
        RigidTransform pose,
        PoseValidity? validity = null,
        Vector3? linearVelocity = null,
        Vector3? angularVelocity = null,
        bool connected = true) =>
        new(
            new TimestampedPoseSample(
                monotonicTimeSeconds,
                pose,
                validity ?? FullPoseValidity),
            connected,
            PoseTrackingResult.RunningOk,
            runtimeTimeSeconds: 1234d,
            predictionOffsetSeconds: 0d,
            sampleAgeSeconds: 0.001d,
            linearVelocityMetersPerSecond: linearVelocity,
            angularVelocityRadiansPerSecond: angularVelocity);

    private static void AssertVector(Vector3 expected, ProtocolVector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }

    private static void AssertQuaternion(Quaternion expected, ProtocolQuaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(
            Quaternion.Normalize(expected),
            new Quaternion(actual.X, actual.Y, actual.Z, actual.W)));
        Assert.InRange(dot, 0.99999f, 1.00001f);
    }
}
