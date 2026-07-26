namespace Ltb.OpenVr;

/// <summary>
/// Describes why an OpenVR settings device path cannot be resolved from an
/// offline paired Lighthouse configuration record.
/// </summary>
public enum OfflineOpenVrDevicePathStatus
{
    /// <summary>
    /// The matching live OpenVR device must supply the registered path
    /// evidence before the path can be used as a settings key.
    /// </summary>
    LiveRegisteredDevicePathRequired,
}

/// <summary>
/// Provenance carried by offline identity evidence. It explicitly does not
/// attest the registered device token used as a SteamVR settings key.
/// </summary>
public enum OfflineOpenVrDevicePathEvidenceProvenance
{
    PairedLighthouseConfig,
}

/// <summary>
/// Carries the offline identity evidence that can safely be retained while
/// requiring a live OpenVR device-path lookup.
/// </summary>
public sealed record OfflineOpenVrDevicePathResult
{
    internal OfflineOpenVrDevicePathResult(
        OfflineOpenVrDevicePathStatus status,
        string stableSerial,
        string model,
        string driverId,
        OfflineOpenVrDevicePathEvidenceProvenance evidenceProvenance,
        string diagnostic)
    {
        Status = status;
        StableSerial = stableSerial;
        Model = model;
        DriverId = driverId;
        EvidenceProvenance = evidenceProvenance;
        Diagnostic = diagnostic;
    }

    public OfflineOpenVrDevicePathStatus Status { get; }

    public string StableSerial { get; }

    public string Model { get; }

    public string DriverId { get; }

    public OfflineOpenVrDevicePathEvidenceProvenance EvidenceProvenance { get; }

    public string? CanonicalDevicePath { get; }

    public string Diagnostic { get; }

    public bool IsResolved => CanonicalDevicePath is not null;
}

/// <summary>
/// Defines the evidence boundary between an offline paired Lighthouse record
/// and the registered device path used by SteamVR settings.
/// </summary>
public static class OfflineOpenVrDevicePath
{
    public const string LighthouseDriverId = "lighthouse";

    public static OfflineOpenVrDevicePathResult FromPairedLighthouseRecord(
        string stableSerial,
        string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableSerial);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return new OfflineOpenVrDevicePathResult(
            OfflineOpenVrDevicePathStatus.LiveRegisteredDevicePathRequired,
            stableSerial.Trim().ToUpperInvariant(),
            model.Trim(),
            LighthouseDriverId,
            OfflineOpenVrDevicePathEvidenceProvenance.PairedLighthouseConfig,
            "Offline Lighthouse config.json proves the stable serial and model, but not " +
            "the registered device token. Resolve the matching live OpenVR registered " +
            "device path before changing SteamVR settings; do not derive it from the " +
            "serial or the lowercase configuration-directory name.");
    }
}

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
            if (registeredDeviceType.StartsWith('/'))
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
