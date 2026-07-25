namespace Ltb.OpenVr;

/// <summary>
/// The exact registered OpenVR device paths for the two physical trackers whose
/// SteamVR roles must be neutralized while LTB publishes controller devices.
/// </summary>
public sealed class PhysicalTrackerRoleTargets
{
    public PhysicalTrackerRoleTargets(
        string leftTrackerDevicePath,
        string rightTrackerDevicePath)
    {
        LeftTrackerDevicePath = ValidateExactRegisteredDevicePath(
            leftTrackerDevicePath,
            nameof(leftTrackerDevicePath));
        RightTrackerDevicePath = ValidateExactRegisteredDevicePath(
            rightTrackerDevicePath,
            nameof(rightTrackerDevicePath));
        if (string.Equals(
                LeftTrackerDevicePath,
                RightTrackerDevicePath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The left and right physical trackers must have distinct registered device paths.",
                nameof(rightTrackerDevicePath));
        }
    }

    public string LeftTrackerDevicePath { get; }

    public string RightTrackerDevicePath { get; }

    private static string ValidateExactRegisteredDevicePath(
        string registeredDevicePath,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            registeredDevicePath,
            parameterName);
        if (!OpenVrDevicePath.TryNormalize(
                registeredDevicePath,
                out var canonicalPath) ||
            !string.Equals(
                registeredDevicePath,
                canonicalPath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A canonical OpenVR registered device path in the form " +
                "'/devices/<driver>/<device>' is required.",
                parameterName);
        }

        return registeredDevicePath;
    }
}
