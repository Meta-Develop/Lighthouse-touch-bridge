namespace Ltb.OpenVr;

/// <summary>Deterministic in-memory device enumerator for tests and demos.</summary>
public sealed class SimulatedSteamVrDeviceEnumerator : SteamVrDeviceEnumerator
{
    private readonly IReadOnlyList<SteamVrDeviceDescriptor> _devices;

    public SimulatedSteamVrDeviceEnumerator(IEnumerable<SteamVrDeviceDescriptor> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var snapshot = devices
            .OrderBy(device => device.Identity.SerialNumber, StringComparer.Ordinal)
            .ThenBy(device => device.Identity.DevicePath, StringComparer.Ordinal)
            .ThenBy(device => device.TransientDeviceIndex)
            .ToArray();

        var duplicateSerial = snapshot
            .GroupBy(device => device.Identity.SerialNumber, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSerial is not null)
        {
            throw new ArgumentException(
                $"Simulated devices must have unique serials; '{duplicateSerial.Key}' is duplicated.",
                nameof(devices));
        }

        _devices = Array.AsReadOnly(snapshot);
    }

    public IReadOnlyList<SteamVrDeviceDescriptor> EnumerateDevices() => _devices;
}

/// <summary>Deterministic, finite input-controller source backed by memory.</summary>
public sealed class SimulatedInputControllerPoseSource : InputControllerPoseSource
{
    private readonly SimulatedPoseSequence _sequence;

    public SimulatedInputControllerPoseSource(
        SteamVrDeviceDescriptor device,
        IEnumerable<PoseSourceSample> samples)
    {
        Device = RequireCategory(device, SteamVrDeviceCategory.InputController);
        _sequence = new SimulatedPoseSequence(samples);
    }

    public SteamVrDeviceDescriptor Device { get; }

    public PoseSourceSample ReadPose() => _sequence.ReadPose();

    private static SteamVrDeviceDescriptor RequireCategory(
        SteamVrDeviceDescriptor device,
        SteamVrDeviceCategory category)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Category != category)
        {
            throw new ArgumentException($"Device must have category {category}.", nameof(device));
        }

        return device;
    }
}

/// <summary>Deterministic, finite tracker source backed by memory.</summary>
public sealed class SimulatedTrackedPoseSource : TrackedPoseSource
{
    private readonly SimulatedPoseSequence _sequence;

    public SimulatedTrackedPoseSource(
        SteamVrDeviceDescriptor device,
        IEnumerable<PoseSourceSample> samples)
    {
        Device = RequireTrackedDevice(device);
        _sequence = new SimulatedPoseSequence(samples);
    }

    public SteamVrDeviceDescriptor Device { get; }

    public PoseSourceSample ReadPose() => _sequence.ReadPose();

    private static SteamVrDeviceDescriptor RequireTrackedDevice(SteamVrDeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Capabilities.HasPosition ||
            device.Category == SteamVrDeviceCategory.InputController)
        {
            throw new ArgumentException(
                "Tracked-pose source requires a non-controller device with position capability.",
                nameof(device));
        }

        return device;
    }
}

internal sealed class SimulatedPoseSequence
{
    private readonly PoseSourceSample[] _samples;
    private int _nextIndex;

    public SimulatedPoseSequence(IEnumerable<PoseSourceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _samples = samples.ToArray();
    }

    public PoseSourceSample ReadPose()
    {
        if (_nextIndex >= _samples.Length)
        {
            throw new InvalidOperationException("The simulated pose sequence is exhausted.");
        }

        return _samples[_nextIndex++];
    }
}
