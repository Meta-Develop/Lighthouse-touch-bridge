using System.Numerics;
using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink.Tests;

public sealed class InputMappingTests
{
    [Fact]
    public void MapsFullPublicTouchStateForBothHands()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        var tracking = LiveTracking();
        var input = new OvrInputState
        {
            ControllerType = OvrConstants.ControllerTouch,
            Buttons = OvrConstants.ButtonA |
                      OvrConstants.ButtonB |
                      OvrConstants.ButtonRThumb |
                      OvrConstants.ButtonX |
                      OvrConstants.ButtonY |
                      OvrConstants.ButtonLThumb |
                      OvrConstants.ButtonEnter,
            Touches = OvrConstants.TouchA |
                      OvrConstants.TouchB |
                      OvrConstants.TouchRThumb |
                      OvrConstants.TouchRThumbRest |
                      OvrConstants.TouchRIndexTrigger |
                      OvrConstants.TouchRIndexPointing |
                      OvrConstants.TouchRThumbUp |
                      OvrConstants.TouchX |
                      OvrConstants.TouchY |
                      OvrConstants.TouchLThumb |
                      OvrConstants.TouchLThumbRest |
                      OvrConstants.TouchLIndexTrigger |
                      OvrConstants.TouchLIndexPointing |
                      OvrConstants.TouchLThumbUp,
            IndexTriggerLeft = 0.1f,
            IndexTriggerRight = 0.2f,
            HandTriggerLeft = 0.3f,
            HandTriggerRight = 0.4f,
            ThumbstickLeft = new OvrVector2f { X = -0.5f, Y = 0.6f },
            ThumbstickRight = new OvrVector2f { X = 0.7f, Y = -0.8f },
        };

        Assert.True(OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out var left,
            out var leftDiagnostic), leftDiagnostic);
        Assert.True(OvrInputMapper.TryMap(
            MetaLinkHand.Right,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out var right,
            out var rightDiagnostic), rightDiagnostic);

        Assert.NotNull(left);
        Assert.True(left.Buttons.X);
        Assert.True(left.Buttons.Y);
        Assert.True(left.Buttons.Thumbstick);
        Assert.True(left.Buttons.Menu);
        Assert.False(left.Buttons.A);
        Assert.True(left.Touches.X);
        Assert.True(left.Touches.Y);
        Assert.True(left.Touches.Thumbstick);
        Assert.True(left.Touches.ThumbRest);
        Assert.True(left.Touches.IndexTrigger);
        Assert.True(left.Touches.IndexPointing);
        Assert.True(left.Touches.ThumbUp);
        Assert.Equal(0.1f, left.Analog.IndexTrigger);
        Assert.Equal(0.3f, left.Analog.GripTrigger);
        Assert.Equal(new Vector2(-0.5f, 0.6f), left.Analog.Thumbstick);
        Assert.False(left.Battery.IsAvailable);
        Assert.Null(left.Battery.Fraction);
        Assert.Equal(100d, left.Pose.RawMetaTimeSeconds);
        Assert.Equal(10d, left.Pose.AppMonotonicTimeSeconds, 9);
        Assert.True(left.Pose.IsOrientationTracked);
        Assert.True(left.Pose.IsPositionTracked);
        Assert.True(left.Pose.HasValidPosition);

        Assert.NotNull(right);
        Assert.True(right.Buttons.A);
        Assert.True(right.Buttons.B);
        Assert.True(right.Buttons.Thumbstick);
        Assert.False(right.Buttons.Menu);
        Assert.True(right.Touches.A);
        Assert.True(right.Touches.B);
        Assert.True(right.Touches.Thumbstick);
        Assert.True(right.Touches.ThumbRest);
        Assert.True(right.Touches.IndexTrigger);
        Assert.True(right.Touches.IndexPointing);
        Assert.True(right.Touches.ThumbUp);
        Assert.Equal(0.2f, right.Analog.IndexTrigger);
        Assert.Equal(0.4f, right.Analog.GripTrigger);
        Assert.Equal(new Vector2(0.7f, -0.8f), right.Analog.Thumbstick);
        Assert.Equal(101d, right.Pose.RawMetaTimeSeconds);
        Assert.Equal(11d, right.Pose.AppMonotonicTimeSeconds, 9);
    }

    [Fact]
    public void RequiresBothConnectedAndInputControllerMasks()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        var input = new OvrInputState { ControllerType = OvrConstants.ControllerRTouch };

        var mapped = OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            LiveTracking(),
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out var snapshot,
            out var diagnostic);

        Assert.False(mapped);
        Assert.Null(snapshot);
        Assert.Contains("controller masks", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OrientationValidityIsRequiredButPositionCanBeUnavailable()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        var tracking = LiveTracking();
        tracking.LeftHandStatusFlags = OvrConstants.StatusOrientationTracked |
                                       OvrConstants.StatusOrientationValid;
        var input = new OvrInputState { ControllerType = OvrConstants.ControllerTouch };

        Assert.True(OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out var orientationOnly,
            out var diagnostic), diagnostic);
        Assert.NotNull(orientationOnly);
        Assert.True(orientationOnly.Pose.HasValidOrientation);
        Assert.False(orientationOnly.Pose.HasValidPosition);

        tracking.LeftHandStatusFlags = OvrConstants.StatusPositionTracked;
        Assert.False(OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out _,
            out diagnostic));
        Assert.Contains("orientation is not valid", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackedButInvalidIsRejectedAndValidEstimatedPositionIsPreserved()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        var tracking = LiveTracking();
        var input = new OvrInputState { ControllerType = OvrConstants.ControllerTouch };
        tracking.LeftHandStatusFlags = OvrConstants.StatusOrientationTracked |
                                       OvrConstants.StatusPositionTracked;

        Assert.False(OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out _,
            out var invalidDiagnostic));
        Assert.Contains("orientation is not valid", invalidDiagnostic, StringComparison.Ordinal);

        tracking.LeftHandStatusFlags = OvrConstants.StatusOrientationTracked |
                                       OvrConstants.StatusOrientationValid |
                                       OvrConstants.StatusPositionValid;
        Assert.True(OvrInputMapper.TryMap(
            MetaLinkHand.Left,
            tracking,
            input,
            OvrConstants.ControllerTouch,
            mapper,
            out var estimatedPosition,
            out var estimatedDiagnostic), estimatedDiagnostic);
        Assert.NotNull(estimatedPosition);
        Assert.True(estimatedPosition.Pose.HasValidPosition);
        Assert.False(estimatedPosition.Pose.IsPositionTracked);
        Assert.Contains("estimated", estimatedDiagnostic, StringComparison.Ordinal);
    }

    internal static OvrTrackingState LiveTracking() => new()
    {
        LeftHandStatusFlags = OvrConstants.StatusOrientationTracked |
                              OvrConstants.StatusPositionTracked |
                              OvrConstants.StatusOrientationValid |
                              OvrConstants.StatusPositionValid,
        RightHandStatusFlags = OvrConstants.StatusOrientationTracked |
                               OvrConstants.StatusPositionTracked |
                               OvrConstants.StatusOrientationValid |
                               OvrConstants.StatusPositionValid,
        LeftHandPose = Pose(100d, 1f),
        RightHandPose = Pose(101d, 2f),
    };

    private static OvrPoseStatef Pose(double time, float x) => new()
    {
        ThePose = new OvrPosef
        {
            Orientation = new OvrQuatf { W = 1f },
            Position = new OvrVector3f { X = x, Y = 2f, Z = -3f },
        },
        AngularVelocity = new OvrVector3f { X = 0.1f, Y = 0.2f, Z = 0.3f },
        LinearVelocity = new OvrVector3f { X = 0.4f, Y = 0.5f, Z = 0.6f },
        AngularAcceleration = new OvrVector3f { X = 0.7f, Y = 0.8f, Z = 0.9f },
        LinearAcceleration = new OvrVector3f { X = 1f, Y = 1.1f, Z = 1.2f },
        TimeInSeconds = time,
    };
}
