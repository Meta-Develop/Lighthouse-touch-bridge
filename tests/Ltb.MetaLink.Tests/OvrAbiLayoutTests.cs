using System.Runtime.InteropServices;
using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink.Tests;

public sealed class OvrAbiLayoutTests
{
    [Fact]
    public void SdkThirtyTwoX64LayoutsMatchTargetedPublicAbi()
    {
        OvrAbiLayout.Verify();

        Assert.Equal(1, Marshal.SizeOf<OvrBool>());
        Assert.Equal(8, Marshal.SizeOf<OvrGraphicsLuid>());
        Assert.Equal(16, Marshal.SizeOf<OvrQuatf>());
        Assert.Equal(8, Marshal.SizeOf<OvrVector2f>());
        Assert.Equal(12, Marshal.SizeOf<OvrVector3f>());
        Assert.Equal(28, Marshal.SizeOf<OvrPosef>());
        Assert.Equal(88, Marshal.SizeOf<OvrPoseStatef>());
        Assert.Equal(312, Marshal.SizeOf<OvrTrackingState>());
        Assert.Equal(120, Marshal.SizeOf<OvrInputState>());
        Assert.Equal(9, Marshal.SizeOf<OvrSessionStatus>());
        Assert.Equal(32, Marshal.SizeOf<OvrInitParams>());
        Assert.Equal(516, Marshal.SizeOf<OvrErrorInfo>());
    }

    [Theory]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.ThePose), 0)]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.AngularVelocity), 28)]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.LinearVelocity), 40)]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.AngularAcceleration), 52)]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.LinearAcceleration), 64)]
    [InlineData(typeof(OvrPoseStatef), nameof(OvrPoseStatef.TimeInSeconds), 80)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.StatusFlags), 88)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.LeftHandPose), 96)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.RightHandPose), 184)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.LeftHandStatusFlags), 272)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.RightHandStatusFlags), 276)]
    [InlineData(typeof(OvrTrackingState), nameof(OvrTrackingState.CalibratedOrigin), 280)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.TimeInSeconds), 0)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.Buttons), 8)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.Touches), 12)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.ThumbstickLeft), 32)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.ControllerType), 48)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.IndexTriggerNoDeadzoneLeft), 52)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.ThumbstickNoDeadzoneRight), 76)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.IndexTriggerRawLeft), 84)]
    [InlineData(typeof(OvrInputState), nameof(OvrInputState.ThumbstickRawRight), 108)]
    [InlineData(typeof(OvrInitParams), nameof(OvrInitParams.Flags), 0)]
    [InlineData(typeof(OvrInitParams), nameof(OvrInitParams.RequestedMinorVersion), 4)]
    [InlineData(typeof(OvrInitParams), nameof(OvrInitParams.LogCallback), 8)]
    [InlineData(typeof(OvrInitParams), nameof(OvrInitParams.UserData), 16)]
    [InlineData(typeof(OvrInitParams), nameof(OvrInitParams.ConnectionTimeoutMilliseconds), 24)]
    [InlineData(typeof(OvrErrorInfo), nameof(OvrErrorInfo.Result), 0)]
    public void ContractBearingOffsetsAreExact(Type type, string field, int expected)
    {
        Assert.Equal(expected, Marshal.OffsetOf(type, field).ToInt32());
    }

    [Fact]
    public void InitializationFlagConstantsMatchPublicAbi()
    {
        // OVR_CAPI.h: ovrInit_RequestVersion = 0x00000004
        Assert.Equal(0x00000004u, OvrConstants.InitRequestVersion);

        // OVR_CAPI.h: ovrInit_Invisible = 0x00000010
        Assert.Equal(0x00000010u, OvrConstants.InitInvisible);
    }

    [Fact]
    public void InvisibleInitializationRequestsAbiMinorSixtyFour()
    {
        var parameters = OvrInitParams.InvisibleSession;

        Assert.Equal(0x14u, parameters.Flags);
        Assert.Equal(64u, parameters.RequestedMinorVersion);
        Assert.Equal(IntPtr.Zero, parameters.LogCallback);
        Assert.Equal(IntPtr.Zero, parameters.UserData);
        Assert.Equal(0u, parameters.ConnectionTimeoutMilliseconds);
    }

    [Fact]
    public void TouchControllerAndTrackingConstantsMatchPublicAbi()
    {
        Assert.Equal(0x00000001u, OvrConstants.ControllerLTouch);
        Assert.Equal(0x00000002u, OvrConstants.ControllerRTouch);
        Assert.Equal(0x00000003u, OvrConstants.ControllerTouch);
        Assert.Equal(0x00000001u, OvrConstants.StatusOrientationTracked);
        Assert.Equal(0x00000002u, OvrConstants.StatusPositionTracked);
        Assert.Equal(0x00000004u, OvrConstants.StatusOrientationValid);
        Assert.Equal(0x00000008u, OvrConstants.StatusPositionValid);
    }
}
