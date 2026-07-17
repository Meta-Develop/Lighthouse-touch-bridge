using System.Text;
using Ltb.Core;

namespace Ltb.Vmt;

/// <summary>A stable VMT virtual-device slot and its OpenVR device path.</summary>
public readonly record struct VmtDeviceAddress
{
    public const int MinimumIndex = 0;
    public const int MaximumIndex = 57;

    public VmtDeviceAddress(int index)
    {
        if (index is < MinimumIndex or > MaximumIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"VMT device index must be between {MinimumIndex} and {MaximumIndex}.");
        }

        Index = index;
    }

    public int Index { get; }

    public string DevicePath => $"/devices/vmt/VMT_{Index}";

    public static VmtDeviceAddress Parse(string devicePath)
    {
        if (!TryParse(devicePath, out var address))
        {
            throw new FormatException(
                $"'{devicePath}' is not a canonical VMT device path in the supported index range.");
        }

        return address;
    }

    public static bool TryParse(string? devicePath, out VmtDeviceAddress address)
    {
        address = default;
        const string prefix = "/devices/vmt/VMT_";

        if (devicePath is null ||
            !devicePath.StartsWith(prefix, StringComparison.Ordinal) ||
            !int.TryParse(devicePath.AsSpan(prefix.Length), out var index) ||
            index is < MinimumIndex or > MaximumIndex)
        {
            return false;
        }

        var candidate = new VmtDeviceAddress(index);
        if (!string.Equals(candidate.DevicePath, devicePath, StringComparison.Ordinal))
        {
            return false;
        }

        address = candidate;
        return true;
    }

    public override string ToString() => DevicePath;
}

/// <summary>
/// VMT's registration mode. The mode is only honored on a slot's first
/// registration after SteamVR starts.
/// </summary>
public enum VmtDeviceMode
{
    Tracker = 1,
    LeftController = 2,
    RightController = 3,
    TrackingReference = 4,
    LeftControllerIndexCompatible = 5,
    RightControllerIndexCompatible = 6,
    ViveTrackerCompatible = 7,
}

/// <summary>
/// Configuration for one serial-following VMT device. The mount transform is
/// LTB's <c>X_mount = T_T_C</c>: controller output frame expressed in the
/// physical tracker frame.
/// </summary>
public sealed record VmtDeviceConfiguration
{
    public const int MaximumFollowSerialUtf8Bytes = 256;

    public VmtDeviceConfiguration(
        VmtDeviceAddress device,
        string followDeviceSerial,
        RigidTransform trackerFromVirtualDevice,
        VmtDeviceMode mode = VmtDeviceMode.Tracker)
    {
        ValidateFollowDeviceSerial(followDeviceSerial);

        if (!trackerFromVirtualDevice.IsValid)
        {
            throw new ArgumentException(
                "The tracker-to-virtual-device mount transform must be valid.",
                nameof(trackerFromVirtualDevice));
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported VMT device mode.");
        }

        Device = device;
        FollowDeviceSerial = followDeviceSerial;
        TrackerFromVirtualDevice = trackerFromVirtualDevice;
        Mode = mode;
    }

    public VmtDeviceAddress Device { get; }

    public string FollowDeviceSerial { get; }

    public RigidTransform TrackerFromVirtualDevice { get; }

    public VmtDeviceMode Mode { get; }

    private static void ValidateFollowDeviceSerial(string followDeviceSerial)
    {
        ArgumentNullException.ThrowIfNull(followDeviceSerial);

        if (string.IsNullOrWhiteSpace(followDeviceSerial) ||
            !string.Equals(followDeviceSerial, followDeviceSerial.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The VMT follow-device serial must be a non-blank stable identifier without leading or trailing whitespace.",
                nameof(followDeviceSerial));
        }

        if (followDeviceSerial.Any(character => character == '\0' || char.IsControl(character)))
        {
            throw new ArgumentException(
                "The VMT follow-device serial must not contain NUL or control characters.",
                nameof(followDeviceSerial));
        }

        if (Encoding.UTF8.GetByteCount(followDeviceSerial) > MaximumFollowSerialUtf8Bytes)
        {
            throw new ArgumentException(
                $"The VMT follow-device serial must not exceed {MaximumFollowSerialUtf8Bytes} UTF-8 bytes.",
                nameof(followDeviceSerial));
        }
    }
}
