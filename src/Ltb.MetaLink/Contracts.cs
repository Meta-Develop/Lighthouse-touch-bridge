using System.Numerics;
using Ltb.Core;

namespace Ltb.MetaLink;

/// <summary>The semantic Touch controller side.</summary>
public enum MetaLinkHand
{
    Left,
    Right,
}

/// <summary>
/// Readiness of one hand at the Meta Quest Link ingestion boundary. Runtime-wide
/// failures are repeated for both hands so consumers never infer readiness from
/// the other controller.
/// </summary>
public enum MetaLinkReadiness
{
    NotInstalled,
    AbiUnavailable,
    RuntimeStopped,
    HeadsetDisconnected,
    ControllersUnavailable,
    Ready,
    Faulted,
}

/// <summary>Buttons publicly reported for a Touch controller hand.</summary>
public readonly record struct MetaLinkButtons(
    bool A,
    bool B,
    bool X,
    bool Y,
    bool Thumbstick,
    bool Menu,
    uint RawMask);

/// <summary>Capacitive and gesture-like touch states reported by LibOVR.</summary>
public readonly record struct MetaLinkTouches(
    bool A,
    bool B,
    bool X,
    bool Y,
    bool Thumbstick,
    bool ThumbRest,
    bool IndexTrigger,
    bool IndexPointing,
    bool ThumbUp,
    uint RawMask);

/// <summary>Analog Touch state after the public LibOVR dead-zone processing.</summary>
public readonly record struct MetaLinkAnalogState(
    float IndexTrigger,
    float GripTrigger,
    Vector2 Thumbstick)
{
    /// <summary>Whether every component is finite and within its public range.</summary>
    public bool IsValid =>
        float.IsFinite(IndexTrigger) && IndexTrigger is >= 0f and <= 1f &&
        float.IsFinite(GripTrigger) && GripTrigger is >= 0f and <= 1f &&
        float.IsFinite(Thumbstick.X) && Thumbstick.X is >= -1f and <= 1f &&
        float.IsFinite(Thumbstick.Y) && Thumbstick.Y is >= -1f and <= 1f;
}

/// <summary>
/// Battery is intentionally unavailable: the required public LibOVR calls do
/// not provide per-controller battery state.
/// </summary>
public readonly record struct MetaLinkBatteryState
{
    private MetaLinkBatteryState(bool isAvailable, float? fraction, string diagnostic)
    {
        IsAvailable = isAvailable;
        Fraction = fraction;
        Diagnostic = diagnostic;
    }

    public bool IsAvailable { get; }

    public float? Fraction { get; }

    public string Diagnostic { get; }

    public static MetaLinkBatteryState Unavailable { get; } = new(
        false,
        null,
        "Per-controller battery state is unavailable from the targeted public LibOVR ABI.");
}

/// <summary>
/// One Touch pose in the right-handed Meta tracking-origin frame. Translation
/// and linear velocity/acceleration are in meters and seconds; angular values
/// are radians and seconds. The quaternion is XYZW through
/// <see cref="System.Numerics.Quaternion"/>.
/// </summary>
public sealed record MetaLinkPoseSnapshot
{
    public MetaLinkPoseSnapshot(
        RigidTransform trackingOriginFromController,
        Vector3 angularVelocityRadiansPerSecond,
        Vector3 linearVelocityMetersPerSecond,
        Vector3 angularAccelerationRadiansPerSecondSquared,
        Vector3 linearAccelerationMetersPerSecondSquared,
        bool isOrientationTracked,
        bool isPositionTracked,
        bool hasValidOrientation,
        bool hasValidPosition,
        double rawMetaTimeSeconds,
        double appMonotonicTimeSeconds,
        long appMonotonicTimeNanoseconds,
        double clockUncertaintySeconds)
    {
        if (!trackingOriginFromController.IsValid)
        {
            throw new ArgumentException("Pose transform must be valid.", nameof(trackingOriginFromController));
        }

        ValidateFinite(angularVelocityRadiansPerSecond, nameof(angularVelocityRadiansPerSecond));
        ValidateFinite(linearVelocityMetersPerSecond, nameof(linearVelocityMetersPerSecond));
        ValidateFinite(angularAccelerationRadiansPerSecondSquared, nameof(angularAccelerationRadiansPerSecondSquared));
        ValidateFinite(linearAccelerationMetersPerSecondSquared, nameof(linearAccelerationMetersPerSecondSquared));

        if (!hasValidOrientation)
        {
            throw new ArgumentException("A published Touch snapshot requires valid orientation.", nameof(hasValidOrientation));
        }

        if (!double.IsFinite(rawMetaTimeSeconds) || rawMetaTimeSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(rawMetaTimeSeconds));
        }

        if (!double.IsFinite(appMonotonicTimeSeconds) || appMonotonicTimeSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(appMonotonicTimeSeconds));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(appMonotonicTimeNanoseconds);

        if (!double.IsFinite(clockUncertaintySeconds) || clockUncertaintySeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(clockUncertaintySeconds));
        }

        TrackingOriginFromController = trackingOriginFromController;
        AngularVelocityRadiansPerSecond = angularVelocityRadiansPerSecond;
        LinearVelocityMetersPerSecond = linearVelocityMetersPerSecond;
        AngularAccelerationRadiansPerSecondSquared = angularAccelerationRadiansPerSecondSquared;
        LinearAccelerationMetersPerSecondSquared = linearAccelerationMetersPerSecondSquared;
        IsOrientationTracked = isOrientationTracked;
        IsPositionTracked = isPositionTracked;
        HasValidOrientation = hasValidOrientation;
        HasValidPosition = hasValidPosition;
        RawMetaTimeSeconds = rawMetaTimeSeconds;
        AppMonotonicTimeSeconds = appMonotonicTimeSeconds;
        AppMonotonicTimeNanoseconds = appMonotonicTimeNanoseconds;
        ClockUncertaintySeconds = clockUncertaintySeconds;
    }

    public RigidTransform TrackingOriginFromController { get; }

    public Vector3 AngularVelocityRadiansPerSecond { get; }

    public Vector3 LinearVelocityMetersPerSecond { get; }

    public Vector3 AngularAccelerationRadiansPerSecondSquared { get; }

    public Vector3 LinearAccelerationMetersPerSecondSquared { get; }

    /// <summary>Whether LibOVR reports active orientation tracking, distinct from validity.</summary>
    public bool IsOrientationTracked { get; }

    /// <summary>Whether LibOVR reports active position tracking, distinct from validity.</summary>
    public bool IsPositionTracked { get; }

    public bool HasValidOrientation { get; }

    public bool HasValidPosition { get; }

    /// <summary>The per-hand <c>ovrPoseStatef.TimeInSeconds</c> value.</summary>
    public double RawMetaTimeSeconds { get; }

    /// <summary>The raw Meta time mapped into the application's monotonic clock.</summary>
    public double AppMonotonicTimeSeconds { get; }

    public long AppMonotonicTimeNanoseconds { get; }

    public double ClockUncertaintySeconds { get; }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentException("Vector components must be finite.", parameterName);
        }
    }
}

/// <summary>Immutable pose and complete public Touch input state for one hand.</summary>
public sealed record MetaLinkControllerSnapshot(
    MetaLinkHand Hand,
    MetaLinkPoseSnapshot Pose,
    MetaLinkButtons Buttons,
    MetaLinkTouches Touches,
    MetaLinkAnalogState Analog,
    MetaLinkBatteryState Battery);

/// <summary>Per-hand readiness and optional current controller sample.</summary>
public sealed record MetaLinkHandSnapshot
{
    public MetaLinkHandSnapshot(
        MetaLinkHand hand,
        MetaLinkReadiness readiness,
        string diagnostic,
        MetaLinkControllerSnapshot? controller = null)
    {
        if (!Enum.IsDefined(hand))
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        if (!Enum.IsDefined(readiness))
        {
            throw new ArgumentOutOfRangeException(nameof(readiness));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        if (readiness == MetaLinkReadiness.Ready && controller is null)
        {
            throw new ArgumentException("Ready requires a controller sample.", nameof(controller));
        }

        if (readiness != MetaLinkReadiness.Ready && controller is not null)
        {
            throw new ArgumentException("A non-ready hand cannot carry a controller sample.", nameof(controller));
        }

        if (controller is not null && controller.Hand != hand)
        {
            throw new ArgumentException("Controller hand does not match readiness hand.", nameof(controller));
        }

        Hand = hand;
        Readiness = readiness;
        Diagnostic = diagnostic;
        Controller = controller;
    }

    public MetaLinkHand Hand { get; }

    public MetaLinkReadiness Readiness { get; }

    public string Diagnostic { get; }

    public MetaLinkControllerSnapshot? Controller { get; }

    public bool InputsLive => Readiness == MetaLinkReadiness.Ready;
}

/// <summary>One atomic two-hand observation from the Meta runtime.</summary>
public sealed record MetaLinkRuntimeSnapshot
{
    public MetaLinkRuntimeSnapshot(
        long sequence,
        double observedAtMonotonicSeconds,
        MetaLinkHandSnapshot left,
        MetaLinkHandSnapshot right)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        if (!double.IsFinite(observedAtMonotonicSeconds) || observedAtMonotonicSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtMonotonicSeconds));
        }

        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Hand != MetaLinkHand.Left || right.Hand != MetaLinkHand.Right)
        {
            throw new ArgumentException("Runtime snapshot hands must be left then right.");
        }

        Sequence = sequence;
        ObservedAtMonotonicSeconds = observedAtMonotonicSeconds;
        Left = left;
        Right = right;
    }

    public long Sequence { get; }

    public double ObservedAtMonotonicSeconds { get; }

    public MetaLinkHandSnapshot Left { get; }

    public MetaLinkHandSnapshot Right { get; }

    public MetaLinkHandSnapshot ForHand(MetaLinkHand hand) => hand switch
    {
        MetaLinkHand.Left => Left,
        MetaLinkHand.Right => Right,
        _ => throw new ArgumentOutOfRangeException(nameof(hand)),
    };
}

/// <summary>Lifecycle and atomic polling boundary for the installed Meta runtime.</summary>
public interface IMetaLinkRuntime : IDisposable
{
    bool IsDisposed { get; }

    MetaLinkRuntimeSnapshot Poll();

    /// <summary>Closes the current session and permits an immediate reconnect attempt.</summary>
    void Reset();
}

/// <summary>Narrow latest-sample view for calibration and composition consumers.</summary>
public interface IMetaLinkControllerSource
{
    bool TryGetLatest(MetaLinkHand hand, out MetaLinkControllerSnapshot? snapshot);
}
