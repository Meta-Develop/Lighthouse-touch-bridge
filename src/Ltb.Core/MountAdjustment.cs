namespace Ltb.Core;

/// <summary>
/// Immutable two-sided adjustment for a calibrated tracker-to-controller
/// mount. The effective mount is
/// <c>X_eff = A_tracker * X_mount * A_controller</c>.
/// </summary>
/// <remarks>
/// "Tracker side" means the left/pre-multiplication side of
/// <c>X_mount = T_T_C</c>, and "controller side" means its
/// right/post-multiplication side. These names describe transform composition
/// sides, not semantic left- and right-hand controllers. Construct one
/// instance per hand when the hands need different adjustments.
/// </remarks>
public sealed record MountAdjustment
{
    public const float MaximumTranslationMeters = 0.5f;

    /// <summary>
    /// Creates an identity adjustment. Applying it leaves
    /// <c>X_mount</c> unchanged.
    /// </summary>
    public MountAdjustment()
        : this(RigidTransform.Identity, RigidTransform.Identity)
    {
    }

    /// <summary>
    /// Creates a two-sided mount adjustment.
    /// </summary>
    /// <param name="trackerSideAdjustment">
    /// <c>A_tracker</c>, an active adjustment expressed in tracker frame
    /// <c>T</c> and pre-multiplied on the left of <c>X_mount</c>.
    /// </param>
    /// <param name="controllerSideAdjustment">
    /// <c>A_controller</c>, an active adjustment expressed in controller frame
    /// <c>C</c> and post-multiplied on the right of <c>X_mount</c>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when either adjustment is an invalid transform, including
    /// <c>default(RigidTransform)</c>.
    /// </exception>
    public MountAdjustment(
        RigidTransform trackerSideAdjustment,
        RigidTransform controllerSideAdjustment)
    {
        if (!trackerSideAdjustment.IsValid)
        {
            throw new ArgumentException(
                "Tracker-side adjustment must be a valid RigidTransform.",
                nameof(trackerSideAdjustment));
        }

        if (!controllerSideAdjustment.IsValid)
        {
            throw new ArgumentException(
                "Controller-side adjustment must be a valid RigidTransform.",
                nameof(controllerSideAdjustment));
        }

        RequireBoundedTranslation(
            trackerSideAdjustment,
            nameof(trackerSideAdjustment));
        RequireBoundedTranslation(
            controllerSideAdjustment,
            nameof(controllerSideAdjustment));

        TrackerSideAdjustment = trackerSideAdjustment;
        ControllerSideAdjustment = controllerSideAdjustment;
    }

    /// <summary>
    /// Identity adjustments on both composition sides.
    /// </summary>
    public static MountAdjustment Identity { get; } = new();

    /// <summary>
    /// <c>A_tracker</c>, the active tracker-frame <c>T</c> adjustment
    /// pre-multiplied on the left of <c>X_mount</c>.
    /// </summary>
    public RigidTransform TrackerSideAdjustment { get; }

    /// <summary>
    /// <c>A_controller</c>, the active controller-frame <c>C</c> adjustment
    /// post-multiplied on the right of <c>X_mount</c>.
    /// </summary>
    public RigidTransform ControllerSideAdjustment { get; }

    /// <summary>
    /// Applies this adjustment to <paramref name="trackerFromControllerMount"/>
    /// using the exact noncommutative order
    /// <c>A_tracker * X_mount * A_controller</c>.
    /// </summary>
    /// <param name="trackerFromControllerMount">
    /// The calibrated <c>X_mount = T_T_C</c>.
    /// </param>
    /// <returns>The effective tracker-to-controller mount <c>X_eff</c>.</returns>
    public RigidTransform ApplyTo(RigidTransform trackerFromControllerMount) =>
        TrackerSideAdjustment *
        trackerFromControllerMount *
        ControllerSideAdjustment;

    private static void RequireBoundedTranslation(
        RigidTransform adjustment,
        string parameterName)
    {
        var magnitude = adjustment.TranslationMeters.Length();
        if (!float.IsFinite(magnitude) || magnitude > MaximumTranslationMeters)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Mount-adjustment translation magnitude must be at most " +
                $"{MaximumTranslationMeters} meters per slot.");
        }
    }
}
