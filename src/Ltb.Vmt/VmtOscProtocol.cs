using System.Buffers.Binary;
using System.Text;

namespace Ltb.Vmt;

/// <summary>One VMT driver heartbeat decoded from OSC.</summary>
public readonly record struct VmtAliveMessage(string Version, string InstallPath);

/// <summary>Minimal deterministic OSC 1.0 codec for the VMT commands LTB uses.</summary>
public static class VmtOscProtocol
{
    public const string JointDriverAddress = "/VMT/Joint/Driver";
    public const string SetAutoPoseUpdateAddress = "/VMT/Set/AutoPoseUpdate";
    public const string AliveAddress = "/VMT/Out/Alive";
    public const string JointDriverTypeTags = ",iiffffffffs";
    public const string SetAutoPoseUpdateTypeTags = ",i";
    public const string AliveTypeTags = ",ss";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] EncodeSetAutoPoseUpdate(bool enabled)
    {
        using var stream = new MemoryStream(capacity: 40);
        WriteOscString(stream, SetAutoPoseUpdateAddress);
        WriteOscString(stream, SetAutoPoseUpdateTypeTags);
        WriteInt32(stream, enabled ? 1 : 0);
        return stream.ToArray();
    }

    public static byte[] EncodeJointDriver(
        VmtDeviceConfiguration configuration,
        bool enabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var transform = VmtTransformConvention.ToVmtDriverLocal(
            configuration.TrackerFromVirtualDevice);

        using var stream = new MemoryStream(capacity: 128);
        WriteOscString(stream, JointDriverAddress);
        WriteOscString(stream, JointDriverTypeTags);
        WriteInt32(stream, configuration.Device.Index);
        WriteInt32(stream, enabled ? (int)configuration.Mode : 0);
        WriteSingle(stream, 0f);
        WriteSingle(stream, transform.PositionMeters.X);
        WriteSingle(stream, transform.PositionMeters.Y);
        WriteSingle(stream, transform.PositionMeters.Z);
        WriteSingle(stream, transform.RotationXyzw.X);
        WriteSingle(stream, transform.RotationXyzw.Y);
        WriteSingle(stream, transform.RotationXyzw.Z);
        WriteSingle(stream, transform.RotationXyzw.W);
        WriteOscString(stream, configuration.FollowDeviceSerial);
        return stream.ToArray();
    }

    public static bool TryDecodeAlive(
        ReadOnlySpan<byte> datagram,
        out VmtAliveMessage alive)
    {
        alive = default;
        var offset = 0;

        try
        {
            if (!TryReadOscString(datagram, ref offset, out var address) ||
                !string.Equals(address, AliveAddress, StringComparison.Ordinal) ||
                !TryReadOscString(datagram, ref offset, out var typeTags) ||
                !string.Equals(typeTags, AliveTypeTags, StringComparison.Ordinal) ||
                !TryReadOscString(datagram, ref offset, out var version) ||
                !TryReadOscString(datagram, ref offset, out var installPath) ||
                offset != datagram.Length)
            {
                return false;
            }

            alive = new VmtAliveMessage(version, installPath);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteOscString(Stream stream, string value)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("OSC strings must not contain NUL characters.", nameof(value));
        }

        var bytes = StrictUtf8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);

        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteSingle(Stream stream, float value) =>
        WriteInt32(stream, BitConverter.SingleToInt32Bits(value));

    private static bool TryReadOscString(
        ReadOnlySpan<byte> datagram,
        ref int offset,
        out string value)
    {
        value = string.Empty;
        if (offset >= datagram.Length)
        {
            return false;
        }

        var remaining = datagram[offset..];
        var terminator = remaining.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        value = StrictUtf8.GetString(remaining[..terminator]);
        var paddedLength = (terminator + 1 + 3) & ~3;
        if (paddedLength > remaining.Length ||
            remaining.Slice(terminator, paddedLength - terminator).ContainsAnyExcept((byte)0))
        {
            return false;
        }

        offset += paddedLength;
        return true;
    }
}
