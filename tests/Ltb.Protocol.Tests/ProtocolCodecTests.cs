using System.Buffers.Binary;
using Ltb.Protocol;

namespace Ltb.Protocol.Tests;

public sealed class ProtocolCodecTests
{
    [Fact]
    public void EncodeHeartbeatMatchesGoldenLittleEndianBytes()
    {
        var message = new ProtocolHeartbeat(ProtocolTestData.Ordering(0x1817161514131211));
        var expected = Convert.FromHexString(
            "4C544231010000000200000020000000" +
            "0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718" +
            "191A1B1C1D1E1F20");

        var packet = ProtocolCodec.Encode(message);

        Assert.Equal(ProtocolConstants.HeartbeatPacketSize, packet.Length);
        Assert.Equal(expected, packet);
    }

    [Fact]
    public void EncodeHandStateMatchesGoldenLittleEndianBytes()
    {
        var expected = Convert.FromHexString(
            "4C544231010000000100000074000000" +
            "0102030405060708090A0B0C0D0E0F10" +
            "1112131415161718" +
            "191A1B1C1D1E1F20" +
            "0100FF00" +
            "0000803F000000C00000003F" +
            "0000000000000000000000000000803F" +
            "0000803E000000BF0000C03F" +
            "00000040000040C000008040" +
            "0500000012000000" +
            "0000803E0000403F000000BF0000003FCDCC4C3F");

        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());

        Assert.Equal(ProtocolConstants.HandStatePacketSize, packet.Length);
        Assert.Equal(expected, packet);
    }

    [Fact]
    public void DecodeHeartbeatRoundTripsEveryField()
    {
        var original = new ProtocolHeartbeat(ProtocolTestData.Ordering(42, 9001));

        var decoded = Assert.IsType<ProtocolHeartbeat>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeHandStateRoundTripsEveryField()
    {
        var original = ProtocolTestData.HandState(ProtocolTestData.Ordering(42, 9001));

        var decoded = Assert.IsType<ProtocolHandState>(ProtocolCodec.Decode(ProtocolCodec.Encode(original)));

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void DecodeRejectsEveryTruncationWithoutReturningPartialState()
    {
        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());
        for (var length = 0; length < packet.Length; length++)
        {
            var truncated = packet.AsSpan(0, length).ToArray();
            Assert.False(ProtocolCodec.TryDecode(truncated, out var message));
            Assert.Null(message);
        }
    }

    [Fact]
    public void DecodeRejectsTrailingData()
    {
        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());
        Array.Resize(ref packet, packet.Length + 1);

        var exception = Assert.Throws<ProtocolException>(() => ProtocolCodec.Decode(packet));

        Assert.Contains("trailing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ProtocolConstants.MagicOffset, 0x00)]
    [InlineData(ProtocolConstants.MajorVersionOffset, 0x02)]
    [InlineData(ProtocolConstants.MinorVersionOffset, 0x01)]
    [InlineData(ProtocolConstants.MessageTypeOffset, 0x7F)]
    [InlineData(ProtocolConstants.HeaderReservedOffset, 0x01)]
    [InlineData(ProtocolConstants.PayloadLengthOffset, 0x00)]
    [InlineData(ProtocolConstants.HandOffset, 0x03)]
    [InlineData(ProtocolConstants.HandReservedOffset, 0x01)]
    public void DecodeRejectsMalformedHeaderAndIdentityFields(int offset, byte value)
    {
        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());
        packet[offset] = value;

        Assert.False(ProtocolCodec.TryDecode(packet, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void DecodeRejectsUnknownPresenceButtonAndTouchBits()
    {
        var valid = ProtocolCodec.Encode(ProtocolTestData.HandState());
        var mutations = new (int Offset, uint Value)[]
        {
            (ProtocolConstants.PresenceFlagsOffset, 0x100),
            (ProtocolConstants.ButtonsOffset, 0x80000000),
            (ProtocolConstants.TouchesOffset, 0x80000000),
        };

        foreach (var mutation in mutations)
        {
            var packet = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(mutation.Offset), mutation.Value);
            Assert.False(ProtocolCodec.TryDecode(packet, out _));
        }
    }

    [Fact]
    public void RandomCorpusNeverEscapesTryDecodeAndRoundTripsAcceptedPackets()
    {
        var random = new Random(0x4C5442);
        var validPackets = new[]
        {
            ProtocolCodec.Encode(ProtocolTestData.HandState()),
            ProtocolCodec.Encode(new ProtocolHeartbeat(ProtocolTestData.Ordering())),
        };
        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            byte[] bytes;
            if (iteration % 2 == 0)
            {
                bytes = new byte[random.Next(0, 192)];
                random.NextBytes(bytes);
            }
            else
            {
                bytes = (byte[])validPackets[random.Next(validPackets.Length)].Clone();
                var mutationCount = random.Next(1, 5);
                for (var mutation = 0; mutation < mutationCount; mutation++)
                {
                    var offset = random.Next(bytes.Length);
                    bytes[offset] ^= (byte)(1 << random.Next(8));
                }
            }

            if (ProtocolCodec.TryDecode(bytes, out var decoded))
            {
                Assert.NotNull(decoded);
                Assert.Equal(bytes, ProtocolCodec.Encode(decoded));
            }
        }
    }

    [Fact]
    public void SessionIdentifierUsesRawBytesRatherThanGuidByteOrdering()
    {
        var packet = ProtocolCodec.Encode(new ProtocolHeartbeat(ProtocolTestData.Ordering()));

        Assert.Equal(
            Convert.FromHexString("0102030405060708090A0B0C0D0E0F10"),
            packet.AsSpan(ProtocolConstants.SessionIdOffset, 16).ToArray());
    }
}
