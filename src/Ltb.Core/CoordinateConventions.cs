namespace Ltb.Core;

/// <summary>
/// Public contract for all runtime-neutral LTB coordinate and unit conventions.
/// Transforms use the notation <c>T_parent_child</c> and map coordinates from
/// the child frame into the parent frame.
/// </summary>
public static class CoordinateConventions
{
    /// <summary>Quest tracking world during calibration.</summary>
    public const string QuestWorldFrame = "Q";

    /// <summary>Lighthouse tracking world.</summary>
    public const string LighthouseWorldFrame = "L";

    /// <summary>Controller pose frame exposed during calibration.</summary>
    public const string ControllerFrame = "C";

    /// <summary>Physical Lighthouse tracker frame.</summary>
    public const string TrackerFrame = "T";

    /// <summary>Internal transform notation: T_parent_child.</summary>
    public const string TransformNotation = "T_parent_child";

    /// <summary>Unknown calibration-world transform: Y = T_Q_L.</summary>
    public const string CalibrationWorldTransformNotation = "Y = T_Q_L";

    /// <summary>Fixed tracker-to-controller mount transform: X_mount = T_T_C.</summary>
    public const string MountTransformNotation = "X_mount = T_T_C";

    /// <summary>The synchronized calibration-pose equation.</summary>
    public const string SynchronizedCalibrationEquation =
        "T_Q_C(i) = T_Q_L * T_L_T(i) * T_T_C";

    /// <summary>All internal Cartesian frames are right-handed.</summary>
    public const string Handedness = "right-handed";

    /// <summary>System.Numerics quaternion components are represented as XYZW.</summary>
    public const string QuaternionComponentOrder = "XYZW";

    /// <summary>Internal linear unit.</summary>
    public const string LengthUnit = "meters";

    /// <summary>Internal monotonic-time unit.</summary>
    public const string TimeUnit = "seconds";

    /// <summary>The required runtime composition equation.</summary>
    public const string RuntimeCompositionEquation =
        "T_L_output(t) = T_L_tracker(t) * X_mount";

    /// <summary>
    /// Applies the runtime contract
    /// <c>T_L_output(t) = T_L_tracker(t) * X_mount</c>, where
    /// <paramref name="lighthouseFromTracker"/> is <c>T_L_T</c> and
    /// <paramref name="trackerFromControllerMount"/> is <c>X_mount = T_T_C</c>.
    /// </summary>
    public static RigidTransform ComposeRuntimeOutput(
        RigidTransform lighthouseFromTracker,
        RigidTransform trackerFromControllerMount) =>
        lighthouseFromTracker * trackerFromControllerMount;
}
