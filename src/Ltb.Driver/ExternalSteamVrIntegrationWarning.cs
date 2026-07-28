using System.Collections.ObjectModel;
using System.Text;

namespace Ltb.Driver;

public enum ExternalSteamVrIntegrationIdentity
{
    SpaceCalibrator = 0,
    BigscreenBeyond,
    VirtualMotionTracker,
    AlvrServer,
}

public enum ExternalSteamVrIntegrationCategory
{
    AdjacentNonControllerPresentation = 0,
    PotentialControllerPresentationConflict,
}

/// <summary>
/// Read-only presentation evidence derived solely from one registered external
/// driver root. A warning never claims that the driver is loaded, running, or
/// currently publishing a SteamVR device.
/// </summary>
public sealed record ExternalSteamVrIntegrationWarning(
    string RegisteredDriverRoot,
    ExternalSteamVrIntegrationIdentity Identity,
    ExternalSteamVrIntegrationCategory Category,
    string DisplayName,
    string Guidance)
{
    public bool IsPotentialControllerPresentationConflict =>
        Category == ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict;

    /// <summary>
    /// Classifies recognized registered-root leaf names without probing driver
    /// files or runtime state. Unknown roots are deliberately omitted. The
    /// returned warning order and duplicates match the registry observations.
    /// </summary>
    public static IReadOnlyList<ExternalSteamVrIntegrationWarning> FromRegisteredDriverRoots(
        IReadOnlyList<string> registeredDriverRoots)
    {
        ArgumentNullException.ThrowIfNull(registeredDriverRoots);
        var warnings = new List<ExternalSteamVrIntegrationWarning>();
        foreach (var registeredDriverRoot in registeredDriverRoots)
        {
            var identity = ClassifyRoot(registeredDriverRoot);
            if (identity is not null)
            {
                warnings.Add(Create(registeredDriverRoot, identity.Value));
            }
        }

        return new ReadOnlyCollection<ExternalSteamVrIntegrationWarning>(warnings);
    }

    private static ExternalSteamVrIntegrationIdentity? ClassifyRoot(string? registeredDriverRoot)
    {
        if (string.IsNullOrWhiteSpace(registeredDriverRoot))
        {
            return null;
        }

        var trimmed = registeredDriverRoot.AsSpan().Trim();
        while (!trimmed.IsEmpty && IsDirectorySeparator(trimmed[^1]))
        {
            trimmed = trimmed[..^1];
        }

        var lastSeparator = trimmed.LastIndexOfAny('/', '\\');
        var leaf = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        var normalized = NormalizeLeaf(leaf);
        return normalized switch
        {
            "01spacecalibrator" or
                "spacecalibrator" or
                "openvrspacecalibrator" =>
                ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
            "bigscreenbeyond" => ExternalSteamVrIntegrationIdentity.BigscreenBeyond,
            "vmt" or "virtualmotiontracker" =>
                ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
            "alvrserver" => ExternalSteamVrIntegrationIdentity.AlvrServer,
            _ => null,
        };
    }

    private static string NormalizeLeaf(ReadOnlySpan<char> leaf)
    {
        var normalized = new StringBuilder(leaf.Length);
        foreach (var character in leaf)
        {
            if (character is >= 'A' and <= 'Z')
            {
                normalized.Append((char)(character + ('a' - 'A')));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                normalized.Append(character);
            }
        }

        return normalized.ToString();
    }

    private static bool IsDirectorySeparator(char character) =>
        character is '/' or '\\';

    private static ExternalSteamVrIntegrationWarning Create(
        string registeredDriverRoot,
        ExternalSteamVrIntegrationIdentity identity) =>
        identity switch
        {
            ExternalSteamVrIntegrationIdentity.SpaceCalibrator => new(
                registeredDriverRoot,
                identity,
                ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation,
                "OpenVR Space Calibrator",
                "Registration alone does not show that Space Calibrator is loaded or active. " +
                "It is not itself a controller-presentation path, but confirm that no " +
                "continuous-calibration or device-mapping configuration targets LTB devices."),
            ExternalSteamVrIntegrationIdentity.BigscreenBeyond => new(
                registeredDriverRoot,
                identity,
                ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation,
                "Bigscreen Beyond",
                "Registration alone does not show that the Bigscreen Beyond driver is loaded " +
                "or active. This is an adjacent HMD integration, not a controller-presentation " +
                "conflict; keep the intended Beyond device as SteamVR's sole HMD."),
            ExternalSteamVrIntegrationIdentity.VirtualMotionTracker => new(
                registeredDriverRoot,
                identity,
                ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict,
                "Virtual Motion Tracker (VMT)",
                "Registration alone does not show that VMT is loaded, active, or publishing a " +
                "device. Before using LTB, confirm that VMT and TrackingOverrides are not " +
                "presenting or overriding either controller hand; LTB will not change them."),
            ExternalSteamVrIntegrationIdentity.AlvrServer => new(
                registeredDriverRoot,
                identity,
                ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict,
                "ALVR server",
                "Registration alone does not show that ALVR is loaded, active, or publishing a " +
                "device. The supported LTB path uses official Meta Horizon Link; confirm that " +
                "ALVR is not presenting a Quest HMD or controllers in SteamVR."),
            _ => throw new ArgumentOutOfRangeException(nameof(identity), identity, null),
        };
}
