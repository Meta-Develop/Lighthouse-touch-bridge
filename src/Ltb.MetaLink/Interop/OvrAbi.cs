using System.Runtime.InteropServices;
using System.Text;

namespace Ltb.MetaLink.Interop;

/// <summary>
/// Clean-room declarations of only the Oculus PC SDK 32.0.0 public C ABI used
/// by LTB. No Meta header or binary is redistributed.
/// </summary>
internal static class OvrConstants
{
    internal const uint InitInvisible = 0x00000004;
    internal const uint InitRequestVersion = 0x00000010;
    internal const uint RequestedMinorVersion = 64;
    internal const uint InitializationFlags = InitInvisible | InitRequestVersion;

    internal const uint ControllerLTouch = 0x00000001;
    internal const uint ControllerRTouch = 0x00000002;
    internal const uint ControllerTouch = ControllerLTouch | ControllerRTouch;

    internal const uint StatusOrientationTracked = 0x00000001;
    internal const uint StatusPositionTracked = 0x00000002;
    internal const uint StatusOrientationValid = 0x00000004;
    internal const uint StatusPositionValid = 0x00000008;

    internal const uint ButtonA = 0x00000001;
    internal const uint ButtonB = 0x00000002;
    internal const uint ButtonRThumb = 0x00000004;
    internal const uint ButtonX = 0x00000100;
    internal const uint ButtonY = 0x00000200;
    internal const uint ButtonLThumb = 0x00000400;
    internal const uint ButtonEnter = 0x00100000;

    internal const uint TouchA = 0x00000001;
    internal const uint TouchB = 0x00000002;
    internal const uint TouchRThumb = 0x00000004;
    internal const uint TouchRThumbRest = 0x00000008;
    internal const uint TouchRIndexTrigger = 0x00000010;
    internal const uint TouchRIndexPointing = 0x00000020;
    internal const uint TouchRThumbUp = 0x00000040;
    internal const uint TouchX = 0x00000100;
    internal const uint TouchY = 0x00000200;
    internal const uint TouchLThumb = 0x00000400;
    internal const uint TouchLThumbRest = 0x00000800;
    internal const uint TouchLIndexTrigger = 0x00001000;
    internal const uint TouchLIndexPointing = 0x00002000;
    internal const uint TouchLThumbUp = 0x00004000;

    internal const int ErrorUnsupported = -1009;
    internal const int ErrorLibVersion = -3002;
    internal const int ErrorServiceVersion = -3004;

    internal static bool Succeeded(int result) => result >= 0;

    internal static bool IsVersionFailure(int result) =>
        result is ErrorUnsupported or ErrorLibVersion or ErrorServiceVersion;
}

[StructLayout(LayoutKind.Explicit, Size = 1)]
internal struct OvrBool
{
    [FieldOffset(0)]
    internal byte Value;

    internal static OvrBool False => default;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct OvrGraphicsLuid
{
    [FieldOffset(0)]
    internal long Reserved;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct OvrQuatf
{
    [FieldOffset(0)] internal float X;
    [FieldOffset(4)] internal float Y;
    [FieldOffset(8)] internal float Z;
    [FieldOffset(12)] internal float W;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct OvrVector2f
{
    [FieldOffset(0)] internal float X;
    [FieldOffset(4)] internal float Y;
}

[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct OvrVector3f
{
    [FieldOffset(0)] internal float X;
    [FieldOffset(4)] internal float Y;
    [FieldOffset(8)] internal float Z;
}

[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct OvrPosef
{
    [FieldOffset(0)] internal OvrQuatf Orientation;
    [FieldOffset(16)] internal OvrVector3f Position;
}

[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct OvrPoseStatef
{
    [FieldOffset(0)] internal OvrPosef ThePose;
    [FieldOffset(28)] internal OvrVector3f AngularVelocity;
    [FieldOffset(40)] internal OvrVector3f LinearVelocity;
    [FieldOffset(52)] internal OvrVector3f AngularAcceleration;
    [FieldOffset(64)] internal OvrVector3f LinearAcceleration;
    [FieldOffset(80)] internal double TimeInSeconds;
}

[StructLayout(LayoutKind.Explicit, Size = 312)]
internal struct OvrTrackingState
{
    [FieldOffset(0)] internal OvrPoseStatef HeadPose;
    [FieldOffset(88)] internal uint StatusFlags;
    [FieldOffset(96)] internal OvrPoseStatef LeftHandPose;
    [FieldOffset(184)] internal OvrPoseStatef RightHandPose;
    [FieldOffset(272)] internal uint LeftHandStatusFlags;
    [FieldOffset(276)] internal uint RightHandStatusFlags;
    [FieldOffset(280)] internal OvrPosef CalibratedOrigin;
}

[StructLayout(LayoutKind.Explicit, Size = 120)]
internal struct OvrInputState
{
    [FieldOffset(0)] internal double TimeInSeconds;
    [FieldOffset(8)] internal uint Buttons;
    [FieldOffset(12)] internal uint Touches;
    [FieldOffset(16)] internal float IndexTriggerLeft;
    [FieldOffset(20)] internal float IndexTriggerRight;
    [FieldOffset(24)] internal float HandTriggerLeft;
    [FieldOffset(28)] internal float HandTriggerRight;
    [FieldOffset(32)] internal OvrVector2f ThumbstickLeft;
    [FieldOffset(40)] internal OvrVector2f ThumbstickRight;
    [FieldOffset(48)] internal uint ControllerType;
    [FieldOffset(52)] internal float IndexTriggerNoDeadzoneLeft;
    [FieldOffset(56)] internal float IndexTriggerNoDeadzoneRight;
    [FieldOffset(60)] internal float HandTriggerNoDeadzoneLeft;
    [FieldOffset(64)] internal float HandTriggerNoDeadzoneRight;
    [FieldOffset(68)] internal OvrVector2f ThumbstickNoDeadzoneLeft;
    [FieldOffset(76)] internal OvrVector2f ThumbstickNoDeadzoneRight;
    [FieldOffset(84)] internal float IndexTriggerRawLeft;
    [FieldOffset(88)] internal float IndexTriggerRawRight;
    [FieldOffset(92)] internal float HandTriggerRawLeft;
    [FieldOffset(96)] internal float HandTriggerRawRight;
    [FieldOffset(100)] internal OvrVector2f ThumbstickRawLeft;
    [FieldOffset(108)] internal OvrVector2f ThumbstickRawRight;
}

[StructLayout(LayoutKind.Explicit, Size = 9)]
internal struct OvrSessionStatus
{
    [FieldOffset(0)] internal byte IsVisible;
    [FieldOffset(1)] internal byte HmdPresent;
    [FieldOffset(2)] internal byte HmdMounted;
    [FieldOffset(3)] internal byte DisplayLost;
    [FieldOffset(4)] internal byte ShouldQuit;
    [FieldOffset(5)] internal byte ShouldRecenter;
    [FieldOffset(6)] internal byte HasInputFocus;
    [FieldOffset(7)] internal byte OverlayPresent;
    [FieldOffset(8)] internal byte DepthRequested;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct OvrInitParams
{
    [FieldOffset(0)] internal uint Flags;
    [FieldOffset(4)] internal uint RequestedMinorVersion;
    [FieldOffset(8)] internal IntPtr LogCallback;
    [FieldOffset(16)] internal IntPtr UserData;
    [FieldOffset(24)] internal uint ConnectionTimeoutMilliseconds;

    internal static OvrInitParams InvisibleSession => new()
    {
        Flags = OvrConstants.InitializationFlags,
        RequestedMinorVersion = OvrConstants.RequestedMinorVersion,
    };
}

[StructLayout(LayoutKind.Explicit, Size = 516)]
internal unsafe struct OvrErrorInfo
{
    [FieldOffset(0)] internal int Result;
    [FieldOffset(4)] internal fixed byte ErrorString[512];

    internal string GetMessage()
    {
        fixed (byte* start = ErrorString)
        {
            var length = 0;
            while (length < 512 && start[length] != 0)
            {
                length++;
            }

            return Encoding.UTF8.GetString(start, length);
        }
    }
}

/// <summary>Runtime guards for the exact x64 layouts targeted by this adapter.</summary>
internal static class OvrAbiLayout
{
    internal static void Verify()
    {
        Size<OvrBool>(1);
        Size<OvrGraphicsLuid>(8);
        Size<OvrQuatf>(16);
        Size<OvrVector2f>(8);
        Size<OvrVector3f>(12);
        Size<OvrPosef>(28);
        Size<OvrPoseStatef>(88);
        Size<OvrTrackingState>(312);
        Size<OvrInputState>(120);
        Size<OvrSessionStatus>(9);
        Size<OvrInitParams>(32);
        Size<OvrErrorInfo>(516);

        Offset<OvrPoseStatef>(nameof(OvrPoseStatef.TimeInSeconds), 80);
        Offset<OvrTrackingState>(nameof(OvrTrackingState.LeftHandPose), 96);
        Offset<OvrTrackingState>(nameof(OvrTrackingState.RightHandPose), 184);
        Offset<OvrTrackingState>(nameof(OvrTrackingState.LeftHandStatusFlags), 272);
        Offset<OvrTrackingState>(nameof(OvrTrackingState.CalibratedOrigin), 280);
        Offset<OvrInputState>(nameof(OvrInputState.ControllerType), 48);
        Offset<OvrInputState>(nameof(OvrInputState.ThumbstickRawRight), 108);
        Offset<OvrInitParams>(nameof(OvrInitParams.LogCallback), 8);
        Offset<OvrInitParams>(nameof(OvrInitParams.ConnectionTimeoutMilliseconds), 24);
    }

    private static void Size<T>(int expected)
        where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new PlatformNotSupportedException(
                $"LibOVR ABI layout mismatch for {typeof(T).Name}: expected {expected}, observed {actual}.");
        }
    }

    private static void Offset<T>(string fieldName, int expected)
        where T : struct
    {
        var actual = checked((int)Marshal.OffsetOf<T>(fieldName));
        if (actual != expected)
        {
            throw new PlatformNotSupportedException(
                $"LibOVR ABI offset mismatch for {typeof(T).Name}.{fieldName}: expected {expected}, observed {actual}.");
        }
    }
}
