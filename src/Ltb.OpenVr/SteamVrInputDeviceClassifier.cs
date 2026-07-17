namespace Ltb.OpenVr;

public sealed record SteamVrInputDeviceClassification(
    bool IsSupported,
    string Diagnostic,
    string? ControllerRuntime = null,
    string? ControllerModel = null,
    SteamVrControllerFamily ControllerFamily = SteamVrControllerFamily.None,
    string? InputProfile = null);

/// <summary>
/// Fail-closed classifier for supported Meta Touch profiles exposed through
/// ALVR/OpenVR. Profile rules are centralized here so application callers only
/// consume controller capabilities and never branch on model strings.
/// ALVR availability must still be proven independently by the application.
/// </summary>
public static class SteamVrInputDeviceClassifier
{
    /// <summary>Legacy Quest 2 compatibility value; use classification output.</summary>
    public const string SupportedRuntime = "ALVR";

    /// <summary>Legacy Quest 2 compatibility value; use classification output.</summary>
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

        var profile = SteamVrControllerProfileCatalog.Match(
            device.ControllerRole,
            metadata);
        if (profile is null)
        {
            return Unsupported(
                "current driver, tracking-system, manufacturer, model, controller-type, " +
                "and input-profile properties do not match a supported Meta Touch profile");
        }

        if (device.Capabilities.ControllerFamily != profile.Family ||
            !string.Equals(
                device.Capabilities.ControllerRuntime,
                profile.Runtime,
                StringComparison.Ordinal) ||
            !string.Equals(
                device.Capabilities.ControllerModel,
                profile.Model,
                StringComparison.Ordinal))
        {
            return Unsupported("descriptor capabilities are inconsistent with current metadata");
        }

        return new SteamVrInputDeviceClassification(
            true,
            $"current OpenVR properties match the {profile.Runtime} {profile.Model} profile",
            profile.Runtime,
            profile.Model,
            profile.Family,
            metadata.InputProfilePath);
    }

    private static SteamVrInputDeviceClassification Unsupported(string diagnostic) =>
        new(false, diagnostic);
}

internal sealed record SteamVrControllerProfile(
    string Runtime,
    string Model,
    SteamVrControllerFamily Family,
    IReadOnlySet<string> DriverIds,
    IReadOnlySet<string> TrackingSystems,
    IReadOnlySet<string> Manufacturers,
    IReadOnlySet<string> ControllerTypes,
    IReadOnlySet<string> LeftModels,
    IReadOnlySet<string> RightModels,
    IReadOnlySet<string> InputProfileFileNames);

internal static class SteamVrControllerProfileCatalog
{
    private static readonly IReadOnlyList<SteamVrControllerProfile> Profiles =
    [
        MetaTouchProfile(
            "Quest 2 Touch",
            leftModels:
            [
                "miramarleftcontroller",
                "oculusquest2leftcontroller",
                "quest2leftcontroller",
                "metaquest2leftcontroller",
                "quest2touch",
                "metaquest2touch",
            ],
            rightModels:
            [
                "miramarrightcontroller",
                "oculusquest2rightcontroller",
                "quest2rightcontroller",
                "metaquest2rightcontroller",
                "quest2touch",
                "metaquest2touch",
            ],
            inputProfiles:
            [
                "oculustouchprofilejson",
                "quest2touchprofilejson",
                "metaquest2touchprofilejson",
            ]),
        MetaTouchProfile(
            "Quest 3 Touch Plus",
            leftModels:
            [
                "metaquest3controller",
                "quest3controller",
                "oculusquest3leftcontroller",
                "metaquest3leftcontroller",
                "quest3leftcontroller",
                "touchplusleftcontroller",
                "metaquest3touchplusleftcontroller",
                "quest3touch",
                "quest3touchplus",
                "metaquest3touchplus",
            ],
            rightModels:
            [
                "metaquest3controller",
                "quest3controller",
                "oculusquest3rightcontroller",
                "metaquest3rightcontroller",
                "quest3rightcontroller",
                "touchplusrightcontroller",
                "metaquest3touchplusrightcontroller",
                "quest3touch",
                "quest3touchplus",
                "metaquest3touchplus",
            ],
            inputProfiles:
            [
                "oculustouchprofilejson",
                "quest3touchplusprofilejson",
                "metaquest3touchplusprofilejson",
            ]),
        MetaTouchProfile(
            "Quest Pro Touch",
            leftModels:
            [
                "metaquestprocontroller",
                "questprocontroller",
                "oculusquestproleftcontroller",
                "metaquestproleftcontroller",
                "questproleftcontroller",
                "touchproleftcontroller",
                "metaquestprotouchleftcontroller",
                "questprotouch",
                "metaquestprotouch",
            ],
            rightModels:
            [
                "metaquestprocontroller",
                "questprocontroller",
                "oculusquestprorightcontroller",
                "metaquestprorightcontroller",
                "questprorightcontroller",
                "touchprorightcontroller",
                "metaquestprotouchrightcontroller",
                "questprotouch",
                "metaquestprotouch",
            ],
            inputProfiles:
            [
                "oculustouchprofilejson",
                "questprotouchprofilejson",
                "metaquestprotouchprofilejson",
                "touchproprofilejson",
            ]),
    ];

    public static SteamVrControllerProfile? Match(
        SteamVrControllerRole controllerRole,
        SteamVrDeviceMetadata? metadata)
    {
        if (metadata is null || controllerRole is not (
                SteamVrControllerRole.LeftHand or SteamVrControllerRole.RightHand))
        {
            return null;
        }

        var driver = Normalize(metadata.DriverId);
        var trackingSystem = Normalize(metadata.TrackingSystemName);
        var manufacturer = Normalize(metadata.ManufacturerName);
        var model = Normalize(metadata.ModelNumber);
        var controllerType = Normalize(metadata.ControllerType);
        var inputProfileFileName = NormalizeInputProfileFileName(
            metadata.InputProfilePath);

        return Profiles.SingleOrDefault(profile =>
            profile.DriverIds.Contains(driver) &&
            profile.TrackingSystems.Contains(trackingSystem) &&
            profile.Manufacturers.Contains(manufacturer) &&
            profile.ControllerTypes.Contains(controllerType) &&
            ModelsForRole(profile, controllerRole).Contains(model) &&
            (metadata.InputProfilePath is null ||
             profile.InputProfileFileNames.Contains(inputProfileFileName)));
    }

    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string NormalizeInputProfileFileName(string? inputProfilePath)
    {
        if (inputProfilePath is null)
        {
            return string.Empty;
        }

        var separatorIndex = inputProfilePath.LastIndexOfAny(['/', '\\']);
        var fileName = separatorIndex < 0
            ? inputProfilePath
            : inputProfilePath[(separatorIndex + 1)..];
        return Normalize(fileName);
    }

    private static IReadOnlySet<string> ModelsForRole(
        SteamVrControllerProfile profile,
        SteamVrControllerRole controllerRole) =>
        controllerRole == SteamVrControllerRole.LeftHand
            ? profile.LeftModels
            : profile.RightModels;

    private static SteamVrControllerProfile MetaTouchProfile(
        string model,
        string[] leftModels,
        string[] rightModels,
        string[] inputProfiles) =>
        new(
            "ALVR",
            model,
            SteamVrControllerFamily.MetaTouch,
            Set("oculus", "alvr"),
            Set("oculus", "alvr"),
            Set("oculus", "meta", "metaplatformstechnologies"),
            Set("oculustouch", "metatouch"),
            Set(leftModels),
            Set(rightModels),
            Set(inputProfiles));

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
