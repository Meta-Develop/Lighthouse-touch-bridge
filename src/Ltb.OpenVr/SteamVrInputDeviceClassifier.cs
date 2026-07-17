namespace Ltb.OpenVr;

public sealed record SteamVrInputDeviceClassification(
    bool IsSupported,
    string Diagnostic,
    string? ControllerRuntime = null,
    string? ControllerModel = null);

/// <summary>
/// Fail-closed classifier for the OpenVR identity tuple emitted by ALVR's
/// Quest2Touch emulation. ALVR deliberately exposes Oculus-compatible values,
/// so ALVR process identity must be proven independently by the application.
/// </summary>
public static class SteamVrInputDeviceClassifier
{
    public const string SupportedRuntime = "ALVR";
    public const string SupportedModel = "Quest 2 Touch";

    public static SteamVrInputDeviceClassification Classify(
        SteamVrDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Category != SteamVrDeviceCategory.InputController)
        {
            return Unsupported("device is not an OpenVR input controller");
        }

        if (device.ControllerRole is not (
                SteamVrControllerRole.LeftHand or SteamVrControllerRole.RightHand))
        {
            return Unsupported("controller has no left/right hand role");
        }

        if (device.Metadata is not { } metadata)
        {
            return Unsupported("current OpenVR driver/model metadata is unavailable");
        }

        var driver = Normalize(metadata.DriverId);
        var trackingSystem = Normalize(metadata.TrackingSystemName);
        var manufacturer = Normalize(metadata.ManufacturerName);
        if (driver != "oculus" || trackingSystem != "oculus")
        {
            return Unsupported(
                "registered driver and tracking-system properties are not the Oculus emulation tuple");
        }

        if (manufacturer != "oculus")
        {
            return Unsupported("manufacturer property is not Oculus");
        }

        var model = Normalize(metadata.ModelNumber);
        var controllerType = Normalize(metadata.ControllerType);
        if (model.Length == 0)
        {
            return Unsupported("OpenVR model-number property is unavailable");
        }

        if (controllerType.Length == 0)
        {
            return Unsupported("OpenVR controller-type property is unavailable");
        }

        var expectedModel = device.ControllerRole == SteamVrControllerRole.LeftHand
            ? "miramarleftcontroller"
            : "miramarrightcontroller";
        if (model != expectedModel)
        {
            return Unsupported(
                $"model '{metadata.ModelNumber}' does not match the Quest 2 {device.ControllerRole} Miramar tuple");
        }

        if (controllerType != "oculustouch")
        {
            return Unsupported(
                $"controller type '{metadata.ControllerType}' is not oculus_touch");
        }

        return new SteamVrInputDeviceClassification(
            true,
            "current OpenVR properties match ALVR Quest2Touch Oculus emulation",
            SupportedRuntime,
            SupportedModel);
    }

    private static SteamVrInputDeviceClassification Unsupported(string diagnostic) =>
        new(false, diagnostic);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
