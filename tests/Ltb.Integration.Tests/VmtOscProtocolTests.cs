using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Text;
using Ltb.Core;
using Ltb.Vmt;

namespace Ltb.Integration.Tests;

public sealed class VmtOscProtocolTests
{
    [Fact]
    public void JointDriverPacketIsDeterministicOscWithDocumentedFieldOrder()
    {
        var mount = new RigidTransform(
            Quaternion.Normalize(new Quaternion(0.1f, -0.2f, 0.3f, 0.9f)),
            new Vector3(0.014f, -0.052f, 0.031f));
        var configuration = new VmtDeviceConfiguration(
            new VmtDeviceAddress(1),
            "LHR-TEST0001",
            mount);

        var first = VmtOscProtocol.EncodeJointDriver(configuration, enabled: true);
        var second = VmtOscProtocol.EncodeJointDriver(configuration, enabled: true);

        Assert.Equal(first, second);
        var reader = new TestOscReader(first);
        Assert.Equal("/VMT/Joint/Driver", reader.ReadString());
        Assert.Equal(",iiffffffffs", reader.ReadString());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal((int)VmtDeviceMode.Tracker, reader.ReadInt32());
        Assert.Equal(0f, reader.ReadSingle());
        Assert.Equal(mount.TranslationMeters.X, reader.ReadSingle());
        Assert.Equal(mount.TranslationMeters.Y, reader.ReadSingle());
        Assert.Equal(mount.TranslationMeters.Z, reader.ReadSingle());
        Assert.Equal(mount.Rotation.X, reader.ReadSingle());
        Assert.Equal(mount.Rotation.Y, reader.ReadSingle());
        Assert.Equal(mount.Rotation.Z, reader.ReadSingle());
        Assert.Equal(mount.Rotation.W, reader.ReadSingle());
        Assert.Equal("LHR-TEST0001", reader.ReadString());
        Assert.True(reader.AtEnd);
    }

    [Fact]
    public void DriverConventionRoundTripsFullTransformAndComposesAsTrackerTimesMount()
    {
        var mount = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f),
            new Vector3(0.02f, -0.04f, 0.03f));
        var trackerPose = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
            new Vector3(1f, 2f, 3f));

        var wire = VmtTransformConvention.ToVmtDriverLocal(mount);
        var roundTripped = VmtTransformConvention.FromVmtDriverLocal(wire);
        var output = CoordinateConventions.ComposeRuntimeOutput(trackerPose, roundTripped);

        AssertVectorClose(mount.TranslationMeters, roundTripped.TranslationMeters);
        AssertQuaternionEquivalent(mount.Rotation, roundTripped.Rotation);
        AssertVectorClose(
            trackerPose.TranslationMeters +
            Vector3.Transform(mount.TranslationMeters, trackerPose.Rotation),
            output.TranslationMeters);
        AssertQuaternionEquivalent(
            trackerPose.Rotation * mount.Rotation,
            output.Rotation);

        // Follow/Driver keeps room-space rotation and therefore cannot realize
        // this rigid tracker-local composition. Joint/Driver is intentional.
        Assert.Equal("/VMT/Joint/Driver", VmtTransformConvention.OscAddress);
    }

    [Fact]
    public void RotationOnlyTransformPreservesTrackerOriginExactly()
    {
        var mount = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(0.2f, -0.4f, 0.1f),
            Vector3.Zero);
        var trackerPose = new RigidTransform(
            Quaternion.CreateFromYawPitchRoll(-0.5f, 0.3f, 0.7f),
            new Vector3(0.7f, -1.2f, 2.4f));
        var wire = VmtTransformConvention.ToVmtDriverLocal(mount);

        var output = trackerPose * VmtTransformConvention.FromVmtDriverLocal(wire);

        Assert.Equal(Vector3.Zero, wire.PositionMeters);
        Assert.Equal(trackerPose.TranslationMeters, output.TranslationMeters);
    }

    [Theory]
    [InlineData(0, "/devices/vmt/VMT_0")]
    [InlineData(57, "/devices/vmt/VMT_57")]
    public void DeviceAddressRoundTripsCanonicalRuntimePath(int index, string path)
    {
        var address = new VmtDeviceAddress(index);

        Assert.Equal(path, address.DevicePath);
        Assert.Equal(address, VmtDeviceAddress.Parse(path));
        Assert.True(VmtDeviceAddress.TryParse(path, out var parsed));
        Assert.Equal(address, parsed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(58)]
    public void DeviceAddressRejectsOutOfRangeIndex(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VmtDeviceAddress(index));
    }

    [Theory]
    [InlineData("/devices/vmt/VMT_01")]
    [InlineData("/devices/vmt/VMT_58")]
    [InlineData("/devices/vmt/vmt_1")]
    [InlineData("VMT_1")]
    public void DeviceAddressRejectsNonCanonicalOrUnsupportedPaths(string path)
    {
        Assert.False(VmtDeviceAddress.TryParse(path, out _));
        Assert.Throws<FormatException>(() => VmtDeviceAddress.Parse(path));
    }

    [Fact]
    public void ConfigurationBoundsFollowSerialAndRequiresValidTransform()
    {
        Assert.Throws<ArgumentException>(() => new VmtDeviceConfiguration(
            new VmtDeviceAddress(1),
            " LHR-TEST0001",
            RigidTransform.Identity));
        Assert.Throws<ArgumentException>(() => new VmtDeviceConfiguration(
            new VmtDeviceAddress(1),
            "LHR-TEST\0BAD",
            RigidTransform.Identity));
        Assert.Throws<ArgumentException>(() => new VmtDeviceConfiguration(
            new VmtDeviceAddress(1),
            new string('x', VmtDeviceConfiguration.MaximumFollowSerialUtf8Bytes + 1),
            RigidTransform.Identity));
        Assert.Throws<ArgumentException>(() => new VmtDeviceConfiguration(
            new VmtDeviceAddress(1),
            "LHR-TEST0001",
            default));
    }

    [Fact]
    public async Task ClientEmitsActivationAndDeactivationThroughFakeTransport()
    {
        var transport = new FakeVmtDatagramTransport();
        await using var client = new VmtClient(
            transport,
            heartbeatStaleAfter: TimeSpan.FromSeconds(2));
        var configuration = new VmtDeviceConfiguration(
            new VmtDeviceAddress(4),
            "LHR-TEST0001",
            RigidTransform.Identity,
            VmtDeviceMode.ViveTrackerCompatible);

        await client.ActivateAsync(configuration);
        await client.DeactivateAsync(configuration);

        Assert.Collection(
            transport.Sent,
            AssertAutoPoseUpdateEnabled,
            packet => Assert.Equal((int)VmtDeviceMode.ViveTrackerCompatible, ReadEnable(packet)),
            packet => Assert.Equal(0, ReadEnable(packet)));
    }

    [Fact]
    public void AutoPoseUpdatePacketIsDeterministicOsc()
    {
        var first = VmtOscProtocol.EncodeSetAutoPoseUpdate(enabled: true);
        var second = VmtOscProtocol.EncodeSetAutoPoseUpdate(enabled: true);

        Assert.Equal(first, second);
        AssertAutoPoseUpdateEnabled(first);
    }

    [Fact]
    public async Task HeartbeatMetadataAndStalenessUseInjectedClock()
    {
        var now = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var transport = new FakeVmtDatagramTransport();
        transport.QueueResponse(EncodeStringMessage(
            VmtOscProtocol.AliveAddress,
            VmtOscProtocol.AliveTypeTags,
            "0.15",
            "C:/VMT-TEST"));
        await using var client = new VmtClient(
            transport,
            heartbeatStaleAfter: TimeSpan.FromSeconds(2),
            clock);

        Assert.Equal(VmtDriverHealthState.NeverObserved, client.DriverHealth.State);
        Assert.True(await client.ObserveNextResponseAsync());

        var alive = client.DriverHealth;
        Assert.True(alive.IsAlive);
        Assert.Equal("0.15", alive.Version);
        Assert.Equal("C:/VMT-TEST", alive.InstallPath);
        Assert.Equal(now, alive.LastHeartbeatUtc);

        clock.AdjustUtc(TimeSpan.FromDays(-1));
        clock.AdvanceElapsed(TimeSpan.FromSeconds(2));
        Assert.True(client.DriverHealth.IsStale);
        Assert.Equal(TimeSpan.FromSeconds(2), client.DriverHealth.HeartbeatAge);
        Assert.Equal(now, client.DriverHealth.LastHeartbeatUtc);
    }

    [Fact]
    public async Task ClientRejectsHeartbeatFromUnexpectedDriverAddress()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var transport = new FakeVmtDatagramTransport();
        var heartbeat = EncodeStringMessage(
            VmtOscProtocol.AliveAddress,
            VmtOscProtocol.AliveTypeTags,
            "0.15",
            "C:/VMT-TEST");
        transport.QueueResponse(heartbeat, IPAddress.Parse("192.0.2.10"));
        transport.QueueResponse(heartbeat, IPAddress.Loopback);
        await using var client = new VmtClient(
            transport,
            heartbeatStaleAfter: TimeSpan.FromSeconds(2),
            clock);

        Assert.False(await client.ObserveNextResponseAsync());
        Assert.Equal(VmtDriverHealthState.NeverObserved, client.DriverHealth.State);
        Assert.True(await client.ObserveNextResponseAsync());
        Assert.True(client.DriverHealth.IsAlive);
        Assert.True(VmtResponseSource.HasExpectedDriverAddress(
            IPAddress.Loopback,
            IPAddress.Parse("::ffff:127.0.0.1")));
    }

    [Fact]
    public void NonAliveOrMalformedResponsesDoNotRefreshHeartbeat()
    {
        var now = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualTimeProvider(now);
        var monitor = new VmtDriverHealthMonitor(TimeSpan.FromSeconds(1), clock);

        Assert.False(monitor.ObserveDriverDatagram(EncodeStringMessage(
            "/VMT/Out/Log",
            ",ss",
            "0",
            "not alive")));
        Assert.False(monitor.ObserveDriverDatagram([1, 2, 3]));
        Assert.Equal(VmtDriverHealthState.NeverObserved, monitor.Snapshot.State);
    }

    [Fact]
    public void MonotonicClockRegressionFailsClosed()
    {
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var monitor = new VmtDriverHealthMonitor(TimeSpan.FromSeconds(1), clock);
        Assert.True(monitor.ObserveDriverDatagram(EncodeStringMessage(
            VmtOscProtocol.AliveAddress,
            VmtOscProtocol.AliveTypeTags,
            "0.15",
            "C:/VMT-TEST")));

        clock.AdvanceElapsed(TimeSpan.FromTicks(-1));

        Assert.True(monitor.Snapshot.IsStale);
    }

    private static int ReadEnable(byte[] datagram)
    {
        var reader = new TestOscReader(datagram);
        Assert.Equal(VmtOscProtocol.JointDriverAddress, reader.ReadString());
        Assert.Equal(VmtOscProtocol.JointDriverTypeTags, reader.ReadString());
        _ = reader.ReadInt32();
        return reader.ReadInt32();
    }

    private static void AssertAutoPoseUpdateEnabled(byte[] datagram)
    {
        var reader = new TestOscReader(datagram);
        Assert.Equal(VmtOscProtocol.SetAutoPoseUpdateAddress, reader.ReadString());
        Assert.Equal(VmtOscProtocol.SetAutoPoseUpdateTypeTags, reader.ReadString());
        Assert.Equal(1, reader.ReadInt32());
        Assert.True(reader.AtEnd);
    }

    private static byte[] EncodeStringMessage(
        string address,
        string typeTags,
        params string[] values)
    {
        using var stream = new MemoryStream();
        WriteOscString(stream, address);
        WriteOscString(stream, typeTags);
        foreach (var value in values)
        {
            WriteOscString(stream, value);
        }

        return stream.ToArray();
    }

    private static void WriteOscString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-6f);

    private static void AssertQuaternionEquivalent(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(expected, actual));
        Assert.InRange(dot, 1f - 1e-6f, 1f + 1e-6f);
    }
}

internal ref struct TestOscReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _offset;

    public TestOscReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _offset = 0;
    }

    public bool AtEnd => _offset == _data.Length;

    public string ReadString()
    {
        var remaining = _data[_offset..];
        var terminator = remaining.IndexOf((byte)0);
        Assert.True(terminator >= 0);
        var value = Encoding.UTF8.GetString(remaining[..terminator]);
        _offset += (terminator + 1 + 3) & ~3;
        return value;
    }

    public int ReadInt32()
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(_data.Slice(_offset, sizeof(int)));
        _offset += sizeof(int);
        return value;
    }

    public float ReadSingle() => BitConverter.Int32BitsToSingle(ReadInt32());
}

internal sealed class FakeVmtDatagramTransport : IVmtDatagramTransport, IVmtResponseSourceFilter
{
    private readonly Queue<VmtReceivedDatagram> _responses = new();

    public List<byte[]> Sent { get; } = [];

    public bool IsDisposed { get; private set; }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> datagram,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add(datagram.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask<VmtReceivedDatagram> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_responses.Dequeue());
    }

    public void QueueResponse(byte[] datagram, IPAddress? remoteAddress = null) =>
        _responses.Enqueue(new VmtReceivedDatagram(
            datagram,
            new IPEndPoint(
                remoteAddress ?? IPAddress.Loopback,
                UdpVmtDatagramTransport.DefaultDriverPort)));

    public bool IsExpectedResponseSource(IPEndPoint remoteEndpoint) =>
        VmtResponseSource.HasExpectedDriverAddress(
            IPAddress.Loopback,
            remoteEndpoint.Address);

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    private long _timestampTicks;

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestampTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void AdvanceElapsed(TimeSpan elapsed) => _timestampTicks += elapsed.Ticks;

    public void AdjustUtc(TimeSpan adjustment) => _utcNow += adjustment;
}
