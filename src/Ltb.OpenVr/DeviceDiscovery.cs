namespace Ltb.OpenVr;

/// <summary>Runtime-neutral classification of a SteamVR tracked device.</summary>
public enum SteamVrDeviceCategory
{
    Unknown = 0,
    HeadMountedDisplay = 1,
    InputController = 2,
    GenericTracker = 3,
    TrackingReference = 4,
    DisplayRedirect = 5,
}

/// <summary>Runtime-neutral hand role reported for an input controller.</summary>
public enum SteamVrControllerRole
{
    None = 0,
    LeftHand = 1,
    RightHand = 2,
    Other = 3,
}

/// <summary>
/// Stable identity for a SteamVR device. The serial number is the canonical
/// association key; the registered device path is retained for diagnostics.
/// Neither value contains the transient OpenVR device index.
/// </summary>
public sealed record SteamVrDeviceIdentity
{
    public SteamVrDeviceIdentity(string serialNumber, string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        SerialNumber = serialNumber;
        DevicePath = devicePath;
    }

    public string SerialNumber { get; }

    public string DevicePath { get; }
}

/// <summary>A point-in-time SteamVR device enumeration result.</summary>
public sealed record SteamVrDeviceDescriptor
{
    public SteamVrDeviceDescriptor(
        SteamVrDeviceIdentity identity,
        uint transientDeviceIndex,
        SteamVrDeviceCategory category,
        SteamVrControllerRole controllerRole,
        bool isConnected)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (!Enum.IsDefined(controllerRole))
        {
            throw new ArgumentOutOfRangeException(nameof(controllerRole));
        }

        if (category != SteamVrDeviceCategory.InputController &&
            controllerRole != SteamVrControllerRole.None)
        {
            throw new ArgumentException(
                "Only input-controller devices may have a controller role.",
                nameof(controllerRole));
        }

        TransientDeviceIndex = transientDeviceIndex;
        Category = category;
        ControllerRole = controllerRole;
        IsConnected = isConnected;
    }

    public SteamVrDeviceIdentity Identity { get; }

    /// <summary>
    /// Current runtime slot. This value is only an access handle and must not
    /// be persisted or used as physical-device identity.
    /// </summary>
    public uint TransientDeviceIndex { get; }

    public SteamVrDeviceCategory Category { get; }

    public SteamVrControllerRole ControllerRole { get; }

    public bool IsConnected { get; }

    /// <summary>Canonical persistent key used for tracker association.</summary>
    public string StableDeviceId => Identity.SerialNumber;

    public bool IsSamePhysicalDeviceAs(SteamVrDeviceDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(
            Identity.SerialNumber,
            other.Identity.SerialNumber,
            StringComparison.Ordinal);
    }
}

/// <summary>Narrow device-enumeration boundary used by the application.</summary>
public interface SteamVrDeviceEnumerator
{
    IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices();
}
