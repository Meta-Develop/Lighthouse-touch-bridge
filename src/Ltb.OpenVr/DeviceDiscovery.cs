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
/// Current OpenVR driver and controller identity properties. These values are
/// observations from the connected runtime and are never populated from a
/// stored calibration profile.
/// </summary>
public sealed record SteamVrDeviceMetadata
{
    public SteamVrDeviceMetadata(
        string driverId,
        string? trackingSystemName,
        string? manufacturerName,
        string? modelNumber,
        string? controllerType)
        : this(
            driverId,
            trackingSystemName,
            manufacturerName,
            modelNumber,
            controllerType,
            inputProfilePath: null)
    {
    }

    public SteamVrDeviceMetadata(
        string driverId,
        string? trackingSystemName,
        string? manufacturerName,
        string? modelNumber,
        string? controllerType,
        string? inputProfilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        DriverId = driverId;
        TrackingSystemName = NormalizeOptional(trackingSystemName);
        ManufacturerName = NormalizeOptional(manufacturerName);
        ModelNumber = NormalizeOptional(modelNumber);
        ControllerType = NormalizeOptional(controllerType);
        InputProfilePath = NormalizeOptional(inputProfilePath);
    }

    public string DriverId { get; }

    public string? TrackingSystemName { get; }

    public string? ManufacturerName { get; }

    public string? ModelNumber { get; }

    public string? ControllerType { get; }

    /// <summary>
    /// OpenVR input-profile path reported by the active driver. This is runtime
    /// evidence and is not inferred from a stored calibration profile.
    /// </summary>
    public string? InputProfilePath { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
        bool isConnected,
        SteamVrDeviceMetadata? metadata = null)
        : this(
            identity,
            transientDeviceIndex,
            category,
            controllerRole,
            isConnected,
            metadata,
            capabilities: null)
    {
    }

    public SteamVrDeviceDescriptor(
        SteamVrDeviceIdentity identity,
        uint transientDeviceIndex,
        SteamVrDeviceCategory category,
        SteamVrControllerRole controllerRole,
        bool isConnected,
        SteamVrDeviceMetadata? metadata,
        SteamVrDeviceCapabilities? capabilities)
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
        Metadata = metadata;
        Capabilities = capabilities ?? SteamVrDeviceCapabilityClassifier.Infer(
            identity.DevicePath,
            category,
            controllerRole,
            metadata);
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

    public SteamVrDeviceMetadata? Metadata { get; }

    /// <summary>
    /// Data-driven runtime capabilities used for hardware-family-independent
    /// selection. These capabilities do not replace current connection or pose
    /// validity checks.
    /// </summary>
    public SteamVrDeviceCapabilities Capabilities { get; }

    /// <summary>
    /// True only for a connected, position-capable physical tracked device
    /// that may authoritatively source the bridged runtime pose.
    /// </summary>
    public bool CanUseAsPhysicalPoseSource =>
        IsConnected &&
        Capabilities.HasPosition &&
        Capabilities.IsPhysicalPoseSourceEligible &&
        !Capabilities.IsVirtualPoseSource;

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
