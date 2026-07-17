using System.Numerics;
using Ltb.Core;

namespace Ltb.Vmt;

/// <summary>
/// The local transform fields accepted by VMT's <c>/VMT/Joint/Driver</c>
/// command: meters in OpenVR's right-handed driver space and a quaternion in
/// XYZW component order.
/// </summary>
public readonly record struct VmtDriverLocalTransform(
    Vector3 PositionMeters,
    Quaternion RotationXyzw);

/// <summary>
/// The single boundary between LTB's transform convention and VMT's wire
/// convention. Both conventions currently use the same right-handed axes,
/// meters, transform direction (<c>T_T_C</c>), and XYZW quaternion order, so
/// conversion is intentionally identity. Keeping it here prevents a future
/// VMT protocol or command change from leaking into calibration code.
/// </summary>
public static class VmtTransformConvention
{
    public const string OscAddress = "/VMT/Joint/Driver";

    public static VmtDriverLocalTransform ToVmtDriverLocal(
        RigidTransform trackerFromVirtualDevice)
    {
        if (!trackerFromVirtualDevice.IsValid)
        {
            throw new ArgumentException(
                "The LTB tracker-to-virtual-device transform must be valid.",
                nameof(trackerFromVirtualDevice));
        }

        return new VmtDriverLocalTransform(
            trackerFromVirtualDevice.TranslationMeters,
            trackerFromVirtualDevice.Rotation);
    }

    public static RigidTransform FromVmtDriverLocal(VmtDriverLocalTransform value) =>
        new(value.RotationXyzw, value.PositionMeters);
}
