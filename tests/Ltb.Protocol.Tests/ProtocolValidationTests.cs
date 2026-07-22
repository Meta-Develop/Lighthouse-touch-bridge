using System.Buffers.Binary;
using Ltb.Protocol;

namespace Ltb.Protocol.Tests;

public sealed class ProtocolValidationTests
{
    public static TheoryData<ProtocolHandState> InvalidNumericStates => new()
    {
        ProtocolTestData.HandState() with
        {
            DriverSpacePose = new ProtocolDriverPose(
                new ProtocolVector3(float.NaN, 0f, 0f),
                ProtocolQuaternion.Identity),
        },
        ProtocolTestData.HandState() with
        {
            DriverSpacePose = new ProtocolDriverPose(
                ProtocolVector3.Zero,
                new ProtocolQuaternion(0f, 0f, 0f, float.PositiveInfinity)),
        },
        ProtocolTestData.HandState() with
        {
            DriverSpacePose = new ProtocolDriverPose(
                ProtocolVector3.Zero,
                new ProtocolQuaternion(0f, 0f, 0f, 0.5f)),
        },
        ProtocolTestData.HandState() with
        {
            Motion = new ProtocolMotion(
                new ProtocolVector3(float.NegativeInfinity, 0f, 0f),
                ProtocolVector3.Zero),
        },
        ProtocolTestData.HandState() with
        {
            Input = ProtocolTestData.HandState().Input with { Trigger = -0.01f },
        },
        ProtocolTestData.HandState() with
        {
            Input = ProtocolTestData.HandState().Input with { Grip = 1.01f },
        },
        ProtocolTestData.HandState() with
        {
            Input = ProtocolTestData.HandState().Input with { StickX = -1.01f },
        },
        ProtocolTestData.HandState() with
        {
            Input = ProtocolTestData.HandState().Input with { StickY = 1.01f },
        },
        ProtocolTestData.HandState() with { BatteryLevel = float.NaN },
        ProtocolTestData.HandState() with { BatteryLevel = 1.01f },
    };

    [Theory]
    [MemberData(nameof(InvalidNumericStates))]
    public void EncodeRejectsNonFiniteRangeAndQuaternionViolations(ProtocolHandState state)
    {
        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Fact]
    public void EncodeRejectsZeroSessionAndZeroTimestamp()
    {
        var zeroSession = new ProtocolHeartbeat(
            new ProtocolOrdering(ProtocolSessionId.Empty, 0, 1));
        var zeroTimestamp = new ProtocolHeartbeat(
            new ProtocolOrdering(ProtocolTestData.SessionA, 0, 0));

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(zeroSession));
        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(zeroTimestamp));
    }

    [Fact]
    public void EncodeRejectsTrackingWithoutConnectedValidPose()
    {
        var state = ProtocolTestData.HandState() with
        {
            Presence = ProtocolPresence.Connected | ProtocolPresence.Tracked,
            Input = ProtocolInputState.Neutral,
            BatteryLevel = 0f,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Fact]
    public void EncodeRejectsBatteryCapabilityInV1()
    {
        var state = ProtocolHandState.Neutral(
            ProtocolTestData.Ordering(),
            ProtocolHand.Left) with
        {
            Presence = ProtocolPresence.BatteryPresent,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Theory]
    [InlineData(ProtocolPresence.OrientationValid)]
    [InlineData(ProtocolPresence.PositionValid)]
    [InlineData(ProtocolPresence.LinearVelocityValid)]
    [InlineData(ProtocolPresence.AngularVelocityValid)]
    [InlineData(ProtocolPresence.InputsValid)]
    [InlineData(ProtocolPresence.Tracked)]
    public void EncodeRejectsEveryValidityFlagWhenDisconnected(ProtocolPresence validityFlag)
    {
        var state = ProtocolHandState.Neutral(
            ProtocolTestData.Ordering(),
            ProtocolHand.Left) with
        {
            Presence = validityFlag,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Fact]
    public void DecodeRejectsValidityFlagsWhenConnectedBitIsClear()
    {
        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());
        var presence = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(ProtocolConstants.PresenceFlagsOffset));
        presence &= unchecked((ushort)~(ushort)ProtocolPresence.Connected);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(ProtocolConstants.PresenceFlagsOffset),
            presence);

        Assert.False(ProtocolCodec.TryDecode(packet, out var decoded));
        Assert.Null(decoded);
    }

    [Theory]
    [InlineData(ProtocolPresence.LinearVelocityValid)]
    [InlineData(ProtocolPresence.AngularVelocityValid)]
    public void EncodeRejectsNonZeroVelocityWhenValidityFlagIsClear(ProtocolPresence validityFlag)
    {
        var original = ProtocolTestData.HandState();
        var state = original with
        {
            Presence = original.Presence & ~validityFlag,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Theory]
    [InlineData(ProtocolPresence.LinearVelocityValid)]
    [InlineData(ProtocolPresence.AngularVelocityValid)]
    public void DecodeRejectsNonZeroVelocityWhenValidityFlagIsClear(ProtocolPresence validityFlag)
    {
        var packet = ProtocolCodec.Encode(ProtocolTestData.HandState());
        var presence = BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(ProtocolConstants.PresenceFlagsOffset));
        presence &= unchecked((ushort)~(ushort)validityFlag);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(ProtocolConstants.PresenceFlagsOffset),
            presence);

        Assert.False(ProtocolCodec.TryDecode(packet, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void EncodeRejectsInvalidInputsWithoutInputsValidFlag()
    {
        var state = ProtocolTestData.HandState() with
        {
            Presence = ProtocolPresence.Connected,
            BatteryLevel = 0f,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Fact]
    public void EncodeRejectsBatteryValueInV1()
    {
        var state = ProtocolHandState.Neutral(
            ProtocolTestData.Ordering(),
            ProtocolHand.Left,
            connected: true) with
        {
            BatteryLevel = 0.5f,
        };

        Assert.Throws<ProtocolException>(() => ProtocolCodec.Encode(state));
    }

    [Fact]
    public void NormalizedQuaternionAtToleranceBoundaryIsAccepted()
    {
        var state = ProtocolTestData.HandState() with
        {
            DriverSpacePose = new ProtocolDriverPose(
                ProtocolVector3.Zero,
                new ProtocolQuaternion(0f, 0f, 0f, 1f)),
        };

        var packet = ProtocolCodec.Encode(state);

        Assert.Equal(ProtocolConstants.HandStatePacketSize, packet.Length);
    }

    [Fact]
    public void AllDefinedCapacitiveTouchStatesAreAccepted()
    {
        var state = ProtocolTestData.HandState() with
        {
            Input = ProtocolTestData.HandState().Input with
            {
                Touches = ProtocolConstants.AllowedTouches,
            },
        };

        var decoded = Assert.IsType<ProtocolHandState>(
            ProtocolCodec.Decode(ProtocolCodec.Encode(state)));

        Assert.Equal((ProtocolTouches)0x7F, decoded.Input.Touches);
    }
}
