namespace Ltb.OpenVr;

/// <summary>
/// Fail-closed readiness result for the SteamVR display HMD. OpenVR reserves
/// transient tracked-device index zero for the active display HMD.
/// </summary>
public sealed record ActiveHmdReadinessResult(bool IsReady, string Diagnostic);

/// <summary>
/// Verifies that ALVR is not supplying the active SteamVR display and that the
/// active HMD reports Lighthouse driver or tracking-system evidence.
/// </summary>
public static class ActiveHmdReadiness
{
    public const uint ActiveDisplayHmdIndex = 0;

    private const string Remediation =
        "Configure ALVR in tracking-reference-only mode and make the intended " +
        "Lighthouse HMD the active SteamVR display HMD, then restart SteamVR and retry.";

    private static readonly string[] DisallowedRuntimeTerms =
    [
        "alvr",
        "quest",
        "meta",
        "oculus",
    ];

    public static ActiveHmdReadinessResult Evaluate(
        IReadOnlyList<SteamVrDeviceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var activeCandidates = devices
            .Where(device => device.TransientDeviceIndex == ActiveDisplayHmdIndex)
            .ToArray();
        if (activeCandidates.Length == 0)
        {
            return NotReady(
                "SteamVR did not report a device at the active display HMD OpenVR index 0.");
        }

        if (activeCandidates.Length != 1)
        {
            return NotReady(
                "SteamVR reported inconsistent device enumeration: multiple devices use " +
                "the active display HMD OpenVR index 0.");
        }

        var active = activeCandidates[0];
        if (active.Category != SteamVrDeviceCategory.HeadMountedDisplay)
        {
            return NotReady(
                $"SteamVR device at active display OpenVR index 0 has class " +
                $"'{active.Category}', not HeadMountedDisplay.");
        }

        if (!active.IsConnected)
        {
            return NotReady(
                "The active SteamVR display HMD at OpenVR index 0 is disconnected.");
        }

        var metadata = active.Metadata;
        if (metadata is null)
        {
            return NotReady(
                "The active SteamVR display HMD has no driver or tracking-system metadata, " +
                "so Lighthouse readiness cannot be verified.");
        }

        var driver = metadata.DriverId;
        var trackingSystem = metadata.TrackingSystemName;
        var observedEvidence = new[]
        {
            driver,
            trackingSystem,
            metadata.ManufacturerName,
            metadata.ModelNumber,
            active.Identity.DevicePath,
        };
        if (observedEvidence.Any(ContainsDisallowedRuntimeTerm))
        {
            return NotReady(
                "The active SteamVR display HMD reports Quest/ALVR/Meta/Oculus runtime " +
                $"evidence (driver='{driver}', tracking_system='{Format(trackingSystem)}').");
        }

        if (!ContainsLighthouseEvidence(driver) &&
            !ContainsLighthouseEvidence(trackingSystem))
        {
            return NotReady(
                "The active SteamVR display HMD does not report positive Lighthouse " +
                $"driver or tracking-system evidence (driver='{driver}', " +
                $"tracking_system='{Format(trackingSystem)}').");
        }

        return new ActiveHmdReadinessResult(
            true,
            "The connected active SteamVR display HMD at OpenVR index 0 reports " +
            "Lighthouse driver/tracking-system evidence.");
    }

    private static ActiveHmdReadinessResult NotReady(string reason) =>
        new(false, $"{reason} {Remediation}");

    private static bool ContainsLighthouseEvidence(string? value) =>
        value?.Contains("lighthouse", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsDisallowedRuntimeTerm(string? value) =>
        value is not null && DisallowedRuntimeTerms.Any(term =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Format(string? value) => value ?? "unavailable";
}
