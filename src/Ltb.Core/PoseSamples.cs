namespace Ltb.Core;

/// <summary>Validity metadata carried with a pose sample.</summary>
[Flags]
public enum PoseValidity
{
    /// <summary>No pose channel is known to be valid.</summary>
    None = 0,

    /// <summary>An orientation value is present.</summary>
    Orientation = 1 << 0,

    /// <summary>A position value is present.</summary>
    Position = 1 << 1,

    /// <summary>The runtime reports the sample as currently tracked and valid.</summary>
    TrackingValid = 1 << 2,
}

/// <summary>
/// A pose captured at a monotonic host timestamp. Time is measured in seconds;
/// it is not wall-clock or UTC time and must not be compared across host boots.
/// </summary>
public readonly record struct TimestampedPoseSample
{
    private const PoseValidity AllDefinedValidityFlags =
        PoseValidity.Orientation | PoseValidity.Position | PoseValidity.TrackingValid;

    /// <summary>Creates a monotonic timestamped pose sample.</summary>
    public TimestampedPoseSample(
        double monotonicTimeSeconds,
        RigidTransform pose,
        PoseValidity validity)
    {
        if (!double.IsFinite(monotonicTimeSeconds) || monotonicTimeSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(monotonicTimeSeconds),
                "Monotonic time must be a finite, non-negative number of seconds.");
        }

        if (!pose.IsValid)
        {
            throw new ArgumentException("Pose must be a valid RigidTransform.", nameof(pose));
        }

        if ((validity & ~AllDefinedValidityFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(validity), "Pose validity contains undefined flags.");
        }

        MonotonicTimeSeconds = monotonicTimeSeconds;
        Pose = pose;
        Validity = validity;
    }

    /// <summary>Host monotonic timestamp in seconds.</summary>
    public double MonotonicTimeSeconds { get; }

    /// <summary>Pose transform associated with this sample.</summary>
    public RigidTransform Pose { get; }

    /// <summary>Per-channel and tracking validity flags.</summary>
    public PoseValidity Validity { get; }

    /// <summary>Whether the runtime reports this sample as tracked.</summary>
    public bool IsTrackingValid => Validity.HasFlag(PoseValidity.TrackingValid);

    /// <summary>Whether orientation is present and the sample is tracked.</summary>
    public bool HasValidOrientation =>
        IsTrackingValid && Validity.HasFlag(PoseValidity.Orientation);

    /// <summary>Whether position is present and the sample is tracked.</summary>
    public bool HasValidPosition =>
        IsTrackingValid && Validity.HasFlag(PoseValidity.Position);

    /// <summary>Whether both pose channels are present and the sample is tracked.</summary>
    public bool HasFullValidPose => HasValidOrientation && HasValidPosition;

    /// <summary>Deconstructs this value into timestamp, pose, and validity.</summary>
    public void Deconstruct(
        out double monotonicTimeSeconds,
        out RigidTransform pose,
        out PoseValidity validity)
    {
        monotonicTimeSeconds = MonotonicTimeSeconds;
        pose = Pose;
        validity = Validity;
    }
}

/// <summary>
/// A tracker/controller pair already aligned to one calibration instant.
/// Producers must interpolate streams before constructing a pair; this type
/// accepts only timestamps equal to within
/// <see cref="TimestampMatchToleranceSeconds"/>.
/// </summary>
public readonly record struct SynchronizedPosePair
{
    /// <summary>
    /// Maximum absolute timestamp difference accepted after synchronization:
    /// one microsecond.
    /// </summary>
    public const double TimestampMatchToleranceSeconds = 1e-6;

    /// <summary>Creates a synchronized tracker/controller pair.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when either sample is invalid or their timestamps are not matched.
    /// </exception>
    public SynchronizedPosePair(
        TimestampedPoseSample tracker,
        TimestampedPoseSample controller)
    {
        if (!tracker.Pose.IsValid)
        {
            throw new ArgumentException("Tracker sample must contain a valid pose.", nameof(tracker));
        }

        if (!controller.Pose.IsValid)
        {
            throw new ArgumentException("Controller sample must contain a valid pose.", nameof(controller));
        }

        var timeDeltaSeconds = controller.MonotonicTimeSeconds - tracker.MonotonicTimeSeconds;
        if (!double.IsFinite(timeDeltaSeconds) ||
            Math.Abs(timeDeltaSeconds) > TimestampMatchToleranceSeconds)
        {
            throw new ArgumentException(
                $"Synchronized samples must be timestamp-matched within {TimestampMatchToleranceSeconds:R} seconds; observed delta was {timeDeltaSeconds:R} seconds.",
                nameof(controller));
        }

        Tracker = tracker;
        Controller = controller;
    }

    /// <summary>The Lighthouse tracker sample.</summary>
    public TimestampedPoseSample Tracker { get; }

    /// <summary>The controller sample exposed during calibration.</summary>
    public TimestampedPoseSample Controller { get; }

    /// <summary>
    /// The aligned calibration time in seconds, represented by the midpoint of
    /// the two timestamps that passed the matching tolerance.
    /// </summary>
    public double MonotonicTimeSeconds =>
        Tracker.MonotonicTimeSeconds + (TimeDeltaSeconds * 0.5d);

    /// <summary>Controller timestamp minus tracker timestamp, in seconds.</summary>
    public double TimeDeltaSeconds =>
        Controller.MonotonicTimeSeconds - Tracker.MonotonicTimeSeconds;

    /// <summary>Deconstructs this value into tracker and controller samples.</summary>
    public void Deconstruct(
        out TimestampedPoseSample tracker,
        out TimestampedPoseSample controller)
    {
        tracker = Tracker;
        controller = Controller;
    }
}
