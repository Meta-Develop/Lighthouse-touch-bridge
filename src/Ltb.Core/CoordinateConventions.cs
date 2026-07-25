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

    /// <summary>
    /// Fixed tracker-to-controller mount transform: <c>X_mount = T_T_C</c>.
    /// Per the <c>T_parent_child</c> convention (parent = tracker frame
    /// <c>T</c>, child = controller frame <c>C</c>) it maps controller-frame
    /// coordinates into the tracker frame and composes at runtime as
    /// <c>T_L_tracker * T_T_C</c>.
    /// </summary>
    public const string MountTransformNotation = "X_mount = T_T_C";

    /// <summary>
    /// Tracker-side adjustment notation. It is pre-multiplied on the left of
    /// <c>X_mount</c>.
    /// </summary>
    public const string TrackerSideAdjustmentNotation = "A_tracker";

    /// <summary>Frame in which <c>A_tracker</c> is expressed.</summary>
    public const string TrackerSideAdjustmentFrame = TrackerFrame;

    /// <summary>
    /// Controller-side adjustment notation. It is post-multiplied on the right
    /// of <c>X_mount</c>.
    /// </summary>
    public const string ControllerSideAdjustmentNotation = "A_controller";

    /// <summary>Frame in which <c>A_controller</c> is expressed.</summary>
    public const string ControllerSideAdjustmentFrame = ControllerFrame;

    /// <summary>The required two-sided effective-mount composition equation.</summary>
    public const string EffectiveMountCompositionEquation =
        "X_eff = A_tracker * X_mount * A_controller";

    /// <summary>
    /// Stable public description of adjustment-side semantics. "Side" names
    /// multiplication order and does not identify a left or right controller
    /// hand.
    /// </summary>
    public const string AdjustmentSideSemantics =
        "tracker-side=left/pre-multiply; controller-side=right/post-multiply; side is not controller hand";

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
        ComposeRuntimeOutput(
            lighthouseFromTracker,
            trackerFromControllerMount,
            MountAdjustment.Identity);

    /// <summary>
    /// Produces the effective mount
    /// <c>X_eff = A_tracker * X_mount * A_controller</c>.
    /// Tracker-side and controller-side name left/pre- and right/post-
    /// multiplication, respectively; they do not name controller hands.
    /// </summary>
    /// <param name="trackerFromControllerMount">
    /// The calibrated <c>X_mount = T_T_C</c>.
    /// </param>
    /// <param name="adjustment">
    /// The portable two-sided adjustment. Use
    /// <see cref="MountAdjustment.Identity"/> for no change.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="adjustment"/> is <see langword="null"/>.
    /// </exception>
    public static RigidTransform ComposeEffectiveMount(
        RigidTransform trackerFromControllerMount,
        MountAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(adjustment);
        return adjustment.ApplyTo(trackerFromControllerMount);
    }

    /// <summary>
    /// Applies the adjusted runtime contract
    /// <c>T_L_output(t) = T_L_tracker(t) * X_eff</c>, where
    /// <c>X_eff = A_tracker * X_mount * A_controller</c>. Adjustment side
    /// names multiplication order, not a semantic left or right controller
    /// hand.
    /// </summary>
    /// <param name="lighthouseFromTracker">
    /// Raw/uncalibrated Lighthouse-world-from-tracker transform
    /// <c>T_L_T</c>.
    /// </param>
    /// <param name="trackerFromControllerMount">
    /// The calibrated tracker-from-controller mount
    /// <c>X_mount = T_T_C</c>.
    /// </param>
    /// <param name="adjustment">
    /// Two-sided tracker/controller-frame adjustment used to compute
    /// <c>X_eff</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="adjustment"/> is <see langword="null"/>.
    /// </exception>
    public static RigidTransform ComposeRuntimeOutput(
        RigidTransform lighthouseFromTracker,
        RigidTransform trackerFromControllerMount,
        MountAdjustment adjustment) =>
        lighthouseFromTracker *
        ComposeEffectiveMount(trackerFromControllerMount, adjustment);
}
