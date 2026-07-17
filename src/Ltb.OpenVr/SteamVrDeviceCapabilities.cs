namespace Ltb.OpenVr;

/// <summary>Controller family independent of a specific controller generation.</summary>
public enum SteamVrControllerFamily
{
    None = 0,
    MetaTouch = 1,
    Other = 2,
}

/// <summary>
/// Capabilities inferred at the narrow SteamVR/OpenVR adaptation boundary.
/// Callers select devices from these facts rather than model-name branches.
/// </summary>
public sealed record SteamVrDeviceCapabilities
{
    public SteamVrDeviceCapabilities(
        bool hasPosition,
        bool isPhysicalPoseSourceEligible,
        bool isVirtualPoseSource,
        SteamVrControllerFamily controllerFamily = SteamVrControllerFamily.None,
        string? controllerRuntime = null,
        string? controllerModel = null,
        string? inputProfile = null)
    {
        if (!Enum.IsDefined(controllerFamily))
        {
            throw new ArgumentOutOfRangeException(nameof(controllerFamily));
        }

        if (isPhysicalPoseSourceEligible && isVirtualPoseSource)
        {
            throw new ArgumentException(
                "A virtual pose source cannot be eligible as a physical pose source.",
                nameof(isVirtualPoseSource));
        }

        HasPosition = hasPosition;
        IsPhysicalPoseSourceEligible = isPhysicalPoseSourceEligible;
        IsVirtualPoseSource = isVirtualPoseSource;
        ControllerFamily = controllerFamily;
        ControllerRuntime = NormalizeOptional(controllerRuntime);
        ControllerModel = NormalizeOptional(controllerModel);
        InputProfile = NormalizeOptional(inputProfile);
    }

    /// <summary>
    /// Whether this device class/profile can expose tracked position. Current
    /// sample validity still determines whether position is usable at a given
    /// instant.
    /// </summary>
    public bool HasPosition { get; }

    /// <summary>
    /// Whether the device may be selected as an authoritative physical mount
    /// pose source. Connection is evaluated separately by the descriptor.
    /// </summary>
    public bool IsPhysicalPoseSourceEligible { get; }

    /// <summary>
    /// Whether the descriptor represents a virtual pose source such as a VMT
    /// output. Virtual outputs must never be selected as physical tracker input.
    /// </summary>
    public bool IsVirtualPoseSource { get; }

    public SteamVrControllerFamily ControllerFamily { get; }

    public string? ControllerRuntime { get; }

    public string? ControllerModel { get; }

    public string? InputProfile { get; }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class SteamVrDeviceCapabilityClassifier
{
    private static readonly HashSet<string> VirtualPoseSourceDrivers =
        new(StringComparer.Ordinal)
        {
            "vmt",
        };

    public static SteamVrDeviceCapabilities Infer(
        string devicePath,
        SteamVrDeviceCategory category,
        SteamVrControllerRole controllerRole,
        SteamVrDeviceMetadata? metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        var hasPosition = category is
            SteamVrDeviceCategory.HeadMountedDisplay or
            SteamVrDeviceCategory.InputController or
            SteamVrDeviceCategory.GenericTracker or
            SteamVrDeviceCategory.TrackingReference;
        var driver = metadata?.DriverId;
        if (string.IsNullOrWhiteSpace(driver) &&
            OpenVrDevicePath.TryGetDriverId(devicePath, out var pathDriver))
        {
            driver = pathDriver;
        }

        var isVirtual = VirtualPoseSourceDrivers.Contains(
            SteamVrControllerProfileCatalog.Normalize(driver));
        var controllerProfile = category == SteamVrDeviceCategory.InputController
            ? SteamVrControllerProfileCatalog.Match(controllerRole, metadata)
            : null;
        var family = category == SteamVrDeviceCategory.InputController
            ? controllerProfile?.Family ?? SteamVrControllerFamily.Other
            : SteamVrControllerFamily.None;

        return new SteamVrDeviceCapabilities(
            hasPosition,
            isPhysicalPoseSourceEligible:
                category == SteamVrDeviceCategory.GenericTracker && !isVirtual,
            isVirtual,
            family,
            controllerProfile?.Runtime,
            controllerProfile?.Model,
            metadata?.InputProfilePath);
    }
}
