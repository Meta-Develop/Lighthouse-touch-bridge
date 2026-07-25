namespace Ltb.Configuration;

/// <summary>
/// An immutable, deterministic set of calibration profiles. A store contains
/// at most one profile for each exact tracker-serial and hand pair.
/// </summary>
public sealed class CalibrationProfileStore
{
    private readonly CalibrationProfile[] profiles;

    public CalibrationProfileStore(IEnumerable<CalibrationProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        this.profiles = profiles
            .Select(profile => profile ?? throw new ArgumentException("Profiles must not contain null entries.", nameof(profiles)))
            .OrderBy(profile => profile.Hand)
            .ThenBy(profile => profile.TrackerSerial, StringComparer.Ordinal)
            .ThenBy(profile => profile.ProfileName, StringComparer.Ordinal)
            .ToArray();

        var keys = new HashSet<ProfileKey>();
        foreach (var profile in this.profiles)
        {
            if (!keys.Add(new ProfileKey(profile.TrackerSerial, profile.Hand)))
            {
                throw new ArgumentException(
                    $"A profile for tracker '{profile.TrackerSerial}' and hand '{profile.Hand}' already exists.",
                    nameof(profiles));
            }
        }
    }

    public static CalibrationProfileStore Empty { get; } = new(Array.Empty<CalibrationProfile>());

    public IReadOnlyList<CalibrationProfile> Profiles => Array.AsReadOnly(profiles);

    /// <summary>
    /// Finds a candidate by exact, ordinal tracker serial and controller hand.
    /// A candidate is not authorized for reuse until its runtime identity is
    /// evaluated with the identity-aware <see cref="FindMatchingProfile(string, ControllerHand, string, string, string, string?)"/>.
    /// </summary>
    public CalibrationProfile? FindCandidateProfile(string trackerSerial, ControllerHand hand)
    {
        var serial = ProfileValidation.RequireIdentity(trackerSerial, nameof(trackerSerial));
        ProfileValidation.RequireDefined(hand, nameof(hand));

        return profiles.FirstOrDefault(profile =>
            profile.Hand == hand &&
            string.Equals(profile.TrackerSerial, serial, StringComparison.Ordinal));
    }

    /// <summary>
    /// Compatibility alias for candidate lookup. This overload does not assert
    /// profile reuse compatibility and must be followed by recalibration evaluation.
    /// </summary>
    [Obsolete("Use FindCandidateProfile for lookup or the identity-aware FindMatchingProfile overload for reuse.")]
    public CalibrationProfile? FindMatchingProfile(string trackerSerial, ControllerHand hand) =>
        FindCandidateProfile(trackerSerial, hand);

    /// <summary>
    /// Finds a reusable schema-2 profile only when tracker, hand, driver
    /// profile, controller runtime/model, and exact runtime identity match.
    /// An unchanged schema-1 legacy profile can never match this overload.
    /// </summary>
    public CalibrationProfile? FindMatchingProfile(
        string trackerSerial,
        ControllerHand hand,
        string driverProfile,
        string controllerRuntime,
        string controllerModel,
        string? controllerIdentity = null)
    {
        var candidate = FindCandidateProfile(trackerSerial, hand);
        return candidate?.MatchesController(
            driverProfile,
            controllerRuntime,
            controllerModel,
            controllerIdentity) == true
            ? candidate
            : null;
    }

    /// <summary>Returns a new store with the profile for the same serial and hand replaced.</summary>
    public CalibrationProfileStore Upsert(CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CalibrationProfileStore(
            profiles
                .Where(existing =>
                    existing.Hand != profile.Hand ||
                    !string.Equals(existing.TrackerSerial, profile.TrackerSerial, StringComparison.Ordinal))
                .Append(profile));
    }

    private readonly record struct ProfileKey(string TrackerSerial, ControllerHand Hand);
}
