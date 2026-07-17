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
    /// Finds a profile by exact, ordinal tracker serial and controller hand.
    /// Device enumeration order and controller identity are deliberately ignored.
    /// </summary>
    public CalibrationProfile? FindMatchingProfile(string trackerSerial, ControllerHand hand)
    {
        var serial = ProfileValidation.RequireIdentity(trackerSerial, nameof(trackerSerial));
        ProfileValidation.RequireDefined(hand, nameof(hand));

        return profiles.FirstOrDefault(profile =>
            profile.Hand == hand &&
            string.Equals(profile.TrackerSerial, serial, StringComparison.Ordinal));
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
