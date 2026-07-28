using System.Collections.ObjectModel;
using System.Globalization;

namespace Ltb.Configuration;

/// <summary>Independent schema and bounds for live tracker-path evidence.</summary>
public static class TrackerPathObservationSchema
{
    public const int CurrentVersion = 1;

    public const int MaximumObservations = 64;

    public const int MaximumHistoryEntries = 8;

    public const int MaximumTrackerSerialLength = 256;

    public const int MaximumRegisteredDevicePathLength = 2048;

    public const int MaximumSerializedBytes = 8 * 1024 * 1024;
}

/// <summary>
/// One live OpenVR observation offered for durable recording. The serial is
/// canonicalized exactly once; the registered path is preserved exactly.
/// </summary>
public sealed class TrackerPathObservationCandidate
{
    public TrackerPathObservationCandidate(
        string trackerSerial,
        string registeredDevicePath,
        DateTimeOffset observedAtUtc)
    {
        TrackerSerial = TrackerPathObservationValidation.CanonicalizeTrackerSerial(
            trackerSerial,
            nameof(trackerSerial));
        RegisteredDevicePath =
            TrackerPathObservationValidation.RequireRegisteredDevicePath(
                registeredDevicePath,
                nameof(registeredDevicePath));
        ObservedAtUtc = TrackerPathObservationValidation.RequireUtc(
            observedAtUtc,
            nameof(observedAtUtc));
    }

    public string TrackerSerial { get; }

    public string RegisteredDevicePath { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public override string ToString() =>
        $"TrackerPathObservationCandidate {{ identity = redacted, observed_utc = " +
        $"{TrackerPathObservationValidation.FormatUtc(ObservedAtUtc)} }}";
}

/// <summary>One prior exact path invalidated by a later live observation.</summary>
public sealed class TrackerPathObservationHistoryEntry
{
    public TrackerPathObservationHistoryEntry(
        string priorRegisteredDevicePath,
        DateTimeOffset priorLastObservedUtc,
        DateTimeOffset replacementUtc)
    {
        PriorRegisteredDevicePath =
            TrackerPathObservationValidation.RequireRegisteredDevicePath(
                priorRegisteredDevicePath,
                nameof(priorRegisteredDevicePath));
        PriorLastObservedUtc = TrackerPathObservationValidation.RequireUtc(
            priorLastObservedUtc,
            nameof(priorLastObservedUtc));
        ReplacementUtc = TrackerPathObservationValidation.RequireUtc(
            replacementUtc,
            nameof(replacementUtc));
        if (ReplacementUtc <= PriorLastObservedUtc)
        {
            throw new ArgumentException(
                "A path replacement UTC must be later than the prior observation UTC.",
                nameof(replacementUtc));
        }
    }

    public string PriorRegisteredDevicePath { get; }

    public DateTimeOffset PriorLastObservedUtc { get; }

    public DateTimeOffset ReplacementUtc { get; }

    public override string ToString() =>
        $"TrackerPathObservationHistoryEntry {{ identity = redacted, " +
        $"prior_last_observed_utc = " +
        $"{TrackerPathObservationValidation.FormatUtc(PriorLastObservedUtc)}, " +
        $"replacement_utc = " +
        $"{TrackerPathObservationValidation.FormatUtc(ReplacementUtc)} }}";
}

/// <summary>
/// Immutable current serial/path evidence plus bounded oldest-to-newest path
/// replacement history. UTC values establish provenance and order only.
/// </summary>
public sealed class TrackerPathObservation
{
    public TrackerPathObservation(
        string trackerSerial,
        string registeredDevicePath,
        DateTimeOffset lastObservedUtc,
        IReadOnlyList<TrackerPathObservationHistoryEntry>? pathChangeHistory = null)
    {
        var canonicalSerial =
            TrackerPathObservationValidation.CanonicalizeTrackerSerial(
                trackerSerial,
                nameof(trackerSerial));
        if (!string.Equals(trackerSerial, canonicalSerial, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Current tracker serial evidence must already be canonical uppercase.",
                nameof(trackerSerial));
        }

        TrackerSerial = canonicalSerial;
        RegisteredDevicePath =
            TrackerPathObservationValidation.RequireRegisteredDevicePath(
                registeredDevicePath,
                nameof(registeredDevicePath));
        LastObservedUtc = TrackerPathObservationValidation.RequireUtc(
            lastObservedUtc,
            nameof(lastObservedUtc));

        var history = pathChangeHistory?.ToArray()
            ?? Array.Empty<TrackerPathObservationHistoryEntry>();
        if (history.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "Path-change history must not contain null entries.",
                nameof(pathChangeHistory));
        }

        if (history.Length > TrackerPathObservationSchema.MaximumHistoryEntries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pathChangeHistory),
                "Path-change history exceeds the supported bound.");
        }

        PathChangeHistory =
            new ReadOnlyCollection<TrackerPathObservationHistoryEntry>(history);
        ValidateHistory();
    }

    public string TrackerSerial { get; }

    public string RegisteredDevicePath { get; }

    public DateTimeOffset LastObservedUtc { get; }

    public IReadOnlyList<TrackerPathObservationHistoryEntry> PathChangeHistory { get; }

    public override string ToString() =>
        $"TrackerPathObservation {{ identity = redacted, last_observed_utc = " +
        $"{TrackerPathObservationValidation.FormatUtc(LastObservedUtc)}, " +
        $"history_length = {PathChangeHistory.Count.ToString(CultureInfo.InvariantCulture)} }}";

    private void ValidateHistory()
    {
        for (var index = 0; index < PathChangeHistory.Count; index++)
        {
            var entry = PathChangeHistory[index];
            var replacementPath = index + 1 < PathChangeHistory.Count
                ? PathChangeHistory[index + 1].PriorRegisteredDevicePath
                : RegisteredDevicePath;
            if (string.Equals(
                    entry.PriorRegisteredDevicePath,
                    replacementPath,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Path-change history contains an invalid no-op transition.",
                    nameof(PathChangeHistory));
            }

            if (entry.ReplacementUtc > LastObservedUtc ||
                (index > 0 &&
                 PathChangeHistory[index - 1].ReplacementUtc >
                 entry.PriorLastObservedUtc))
            {
                throw new ArgumentException(
                    "Path-change history must be chronological.",
                    nameof(PathChangeHistory));
            }
        }
    }
}

internal static class TrackerPathObservationValidation
{
    internal const string UtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    internal static string CanonicalizeTrackerSerial(
        string trackerSerial,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(trackerSerial, parameterName);
        if (trackerSerial.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Tracker serial evidence must not contain control characters.",
                parameterName);
        }

        var canonical = trackerSerial.Trim().ToUpperInvariant();
        if (canonical.Length is < 1
            or > TrackerPathObservationSchema.MaximumTrackerSerialLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Tracker serial evidence is outside the supported length bound.");
        }

        if (canonical.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException(
                "Tracker serial evidence must not contain whitespace after trimming.",
                parameterName);
        }

        return canonical;
    }

    internal static string RequireRegisteredDevicePath(
        string registeredDevicePath,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(registeredDevicePath, parameterName);
        if (registeredDevicePath.Length is < 1
            or > TrackerPathObservationSchema.MaximumRegisteredDevicePathLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Registered-device-path evidence is outside the supported length bound.");
        }

        if (registeredDevicePath.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "Registered-device-path evidence must not contain control or whitespace characters.",
                parameterName);
        }

        const string prefix = "/devices/";
        if (!registeredDevicePath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Registered-device-path evidence does not have the required canonical shape.",
                parameterName);
        }

        var segments = registeredDevicePath[prefix.Length..].Split('/');
        if (segments.Length != 2 ||
            segments.Any(segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Registered-device-path evidence does not have the required canonical shape.",
                parameterName);
        }

        return registeredDevicePath;
    }

    internal static DateTimeOffset RequireUtc(
        DateTimeOffset value,
        string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Observation provenance must have a zero UTC offset.",
                parameterName);
        }

        return value;
    }

    internal static string FormatUtc(DateTimeOffset value) =>
        value.ToString(UtcFormat, CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                UtcFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Tracker-path evidence contains a noncanonical UTC value.");
        }

        return parsed;
    }
}
