namespace Ltb.OpenVr;

/// <summary>
/// Read-only comparison result for one physical tracker role after LTB
/// neutralized the exact registered device path.
/// </summary>
public enum TrackerRoleDriftStatus
{
    UnchangedNeutral = 0,
    Missing = 1,
    Changed = 2,
}

/// <summary>
/// Current role status for one exact registered physical tracker path.
/// </summary>
public sealed class TrackerRoleDriftEntry
{
    internal TrackerRoleDriftEntry(
        string registeredDevicePath,
        TrackerRoleDriftStatus status,
        string? observedRole)
    {
        RegisteredDevicePath = registeredDevicePath;
        Status = status;
        ObservedRole = observedRole;
    }

    public string RegisteredDevicePath { get; }

    public TrackerRoleDriftStatus Status { get; }

    /// <summary>
    /// The current string role when one was present. Non-string changed values
    /// are deliberately not exposed as settings content.
    /// </summary>
    public string? ObservedRole { get; }

    public bool HasDrift => Status is not TrackerRoleDriftStatus.UnchangedNeutral;
}

/// <summary>
/// Read-only drift report bound to the exact two targets captured by one LTB
/// physical-tracker role neutralization.
/// </summary>
public sealed class TrackerRoleDrift
{
    internal TrackerRoleDrift(
        PhysicalTrackerRoleTargets targets,
        TrackerRoleDriftEntry leftTracker,
        TrackerRoleDriftEntry rightTracker)
    {
        Targets = targets;
        LeftTracker = leftTracker;
        RightTracker = rightTracker;
    }

    public PhysicalTrackerRoleTargets Targets { get; }

    public TrackerRoleDriftEntry LeftTracker { get; }

    public TrackerRoleDriftEntry RightTracker { get; }

    public bool HasDrift => LeftTracker.HasDrift || RightTracker.HasDrift;
}
