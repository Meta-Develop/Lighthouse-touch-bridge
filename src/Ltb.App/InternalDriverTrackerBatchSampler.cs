using Ltb.Core;
using Ltb.OpenVr;

namespace Ltb.App;

/// <summary>
/// Reuses one shared OpenVR pose source only while every freshly enumerated
/// stable identity and transient access index remains exact.
/// </summary>
internal sealed class InternalDriverTrackerBatchSampler
{
    private readonly Func<IReadOnlyList<SteamVrDeviceDescriptor>, TrackedPoseBatchSource>
        _createSource;
    private TrackerBatchRosterEntry[] _roster = [];
    private TrackedPoseBatchSource? _source;

    public InternalDriverTrackerBatchSampler(
        Func<IReadOnlyList<SteamVrDeviceDescriptor>, TrackedPoseBatchSource> createSource)
    {
        _createSource = createSource ?? throw new ArgumentNullException(nameof(createSource));
    }

    public IReadOnlyDictionary<string, PoseSourceSample> Read(
        IReadOnlyList<SteamVrDeviceDescriptor> currentDevices)
    {
        ArgumentNullException.ThrowIfNull(currentDevices);
        var snapshot = currentDevices.ToArray();
        if (snapshot.Any(device => device is null))
        {
            throw new ArgumentException(
                "Tracker batch devices cannot contain null entries.",
                nameof(currentDevices));
        }

        var devices = snapshot
            .OrderBy(device => device.StableDeviceId, StringComparer.Ordinal)
            .ToArray();
        var duplicateSerial = devices
            .GroupBy(device => device.StableDeviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateSerial is not null)
        {
            throw new InvalidDataException(
                $"SteamVR enumerated duplicate physical tracker serial '{duplicateSerial}'.");
        }

        if (devices.Length == 0)
        {
            Reset();
            return new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal);
        }

        var currentRoster = devices.Select(TrackerBatchRosterEntry.From).ToArray();
        if (_source is null || !_roster.SequenceEqual(currentRoster))
        {
            _source = _createSource(Array.AsReadOnly(devices))
                ?? throw new InvalidOperationException(
                    "The OpenVR tracker-batch factory returned null.");
            _roster = currentRoster;
        }

        ValidateSourceRoster(_source, _roster);
        var batch = _source.ReadPoses()
            ?? throw new InvalidDataException("OpenVR returned a null tracker-pose batch.");
        if (batch.Count != _roster.Length)
        {
            throw new InvalidDataException(
                $"OpenVR returned {batch.Count} tracker poses for {_roster.Length} enumerated candidates.");
        }

        var samples = new Dictionary<string, PoseSourceSample>(
            _roster.Length,
            StringComparer.Ordinal);
        for (var index = 0; index < _roster.Length; index++)
        {
            var actual = TrackerBatchRosterEntry.From(batch[index].Device);
            if (actual != _roster[index])
            {
                throw new InvalidDataException(
                    $"OpenVR tracker-pose batch identity mismatch at position {index}: " +
                    $"expected {_roster[index]}, observed {actual}.");
            }

            if (!samples.TryAdd(actual.StableSerial, batch[index].Sample))
            {
                throw new InvalidDataException(
                    $"OpenVR tracker-pose batch repeated stable serial '{actual.StableSerial}'.");
            }
        }

        return samples;
    }

    public void Reset()
    {
        _source = null;
        _roster = [];
    }

    private static void ValidateSourceRoster(
        TrackedPoseBatchSource source,
        IReadOnlyList<TrackerBatchRosterEntry> expected)
    {
        var sourceDevices = source.Devices
            ?? throw new InvalidDataException(
                "The OpenVR tracker-batch source exposed a null device roster.");
        if (sourceDevices.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"The OpenVR tracker-batch source retained {sourceDevices.Count} devices for " +
                $"{expected.Count} enumerated candidates.");
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var actual = TrackerBatchRosterEntry.From(sourceDevices[index]);
            if (actual != expected[index])
            {
                throw new InvalidDataException(
                    $"The cached OpenVR tracker-batch source identity changed at position {index}: " +
                    $"expected {expected[index]}, observed {actual}.");
            }
        }
    }

    private readonly record struct TrackerBatchRosterEntry(
        string StableSerial,
        string DevicePath,
        uint TransientDeviceIndex)
    {
        public static TrackerBatchRosterEntry From(SteamVrDeviceDescriptor device)
        {
            ArgumentNullException.ThrowIfNull(device);
            return new TrackerBatchRosterEntry(
                device.StableDeviceId,
                device.Identity.DevicePath,
                device.TransientDeviceIndex);
        }

        public override string ToString() =>
            $"serial '{StableSerial}', path '{DevicePath}', index {TransientDeviceIndex}";
    }
}
