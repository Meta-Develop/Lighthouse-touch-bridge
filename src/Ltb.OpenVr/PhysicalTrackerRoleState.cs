using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ltb.OpenVr;

/// <summary>
/// The exact prior presence and JSON value of one physical tracker's SteamVR
/// role entry.
/// </summary>
public sealed class PhysicalTrackerRoleSnapshot
{
    private readonly JsonNode? _previousValue;

    internal PhysicalTrackerRoleSnapshot(
        string registeredDevicePath,
        bool wasPresent,
        JsonNode? previousValue)
    {
        RegisteredDevicePath = registeredDevicePath;
        WasPresent = wasPresent;
        _previousValue = previousValue?.DeepClone();
        PreviousValue = wasPresent
            ? JsonSerializer.SerializeToElement<JsonNode?>(_previousValue)
            : null;
    }

    public string RegisteredDevicePath { get; }

    public bool WasPresent { get; }

    /// <summary>
    /// The prior JSON value, or <see langword="null"/> when the entry was
    /// absent. An explicitly present JSON <c>null</c> is represented by a
    /// non-null element whose <see cref="JsonElement.ValueKind"/> is
    /// <see cref="JsonValueKind.Null"/>.
    /// </summary>
    public JsonElement? PreviousValue { get; }

    internal JsonNode? ClonePreviousValue() => _previousValue?.DeepClone();

    internal bool Matches(JsonObject trackers)
    {
        var isPresent = trackers.TryGetPropertyValue(
            RegisteredDevicePath,
            out var currentValue);
        return WasPresent == isPresent &&
            (!WasPresent || JsonNode.DeepEquals(_previousValue, currentValue));
    }
}

/// <summary>
/// Immutable prior state captured by one exact-two physical tracker role
/// neutralization operation.
/// </summary>
public sealed class PhysicalTrackerRoleState
{
    internal PhysicalTrackerRoleState(
        PhysicalTrackerRoleTargets targets,
        bool trackersSectionWasPresent,
        PhysicalTrackerRoleSnapshot leftTracker,
        PhysicalTrackerRoleSnapshot rightTracker)
    {
        Targets = targets;
        TrackersSectionWasPresent = trackersSectionWasPresent;
        LeftTracker = leftTracker;
        RightTracker = rightTracker;
    }

    public PhysicalTrackerRoleTargets Targets { get; }

    public bool TrackersSectionWasPresent { get; }

    public PhysicalTrackerRoleSnapshot LeftTracker { get; }

    public PhysicalTrackerRoleSnapshot RightTracker { get; }

    internal IEnumerable<PhysicalTrackerRoleSnapshot> Snapshots
    {
        get
        {
            yield return LeftTracker;
            yield return RightTracker;
        }
    }
}
