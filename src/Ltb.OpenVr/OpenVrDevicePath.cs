namespace Ltb.OpenVr;

/// <summary>
/// Converts OpenVR's RegisteredDeviceType property into the canonical device
/// path syntax consumed by TrackingOverrides. Invalid or unavailable values
/// resolve to a diagnostic URI that cannot be selected as an override source.
/// </summary>
internal static class OpenVrDevicePath
{
    private const string CanonicalPrefix = "/devices/";
    private const string DiagnosticPrefix = "openvr://device/";

    public static string Resolve(string? registeredDeviceType, string serialNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        return TryNormalize(registeredDeviceType, out var canonicalPath)
            ? canonicalPath
            : DiagnosticPrefix + Uri.EscapeDataString(serialNumber);
    }

    public static bool TryNormalize(
        string? registeredDeviceType,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(registeredDeviceType) ||
            registeredDeviceType.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return false;
        }

        string driverAndDevice;
        if (registeredDeviceType.StartsWith(CanonicalPrefix, StringComparison.Ordinal))
        {
            driverAndDevice = registeredDeviceType[CanonicalPrefix.Length..];
        }
        else
        {
            if (registeredDeviceType.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }

            driverAndDevice = registeredDeviceType;
        }

        var segments = driverAndDevice.Split('/');
        if (segments.Length != 2 ||
            segments.Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            return false;
        }

        canonicalPath = CanonicalPrefix + driverAndDevice;
        return true;
    }

    public static bool TryGetDriverId(string canonicalPath, out string driverId)
    {
        driverId = string.Empty;
        if (!canonicalPath.StartsWith(CanonicalPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = canonicalPath[CanonicalPrefix.Length..];
        var separator = remainder.IndexOf('/');
        if (separator <= 0)
        {
            return false;
        }

        driverId = remainder[..separator];
        return true;
    }
}
