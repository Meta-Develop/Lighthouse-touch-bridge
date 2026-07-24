using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverTrackerBatchSamplerTests
{
    [Fact]
    public void StableRosterUsesOneSharedReadPerObservation()
    {
        var left = Tracker("TRACKER-LEFT", index: 7);
        var right = Tracker("TRACKER-RIGHT", index: 3);
        var source = new FakeBatchSource([left, right]);
        var factoryCalls = 0;
        var sampler = new InternalDriverTrackerBatchSampler(devices =>
        {
            factoryCalls++;
            Assert.Equal(["TRACKER-LEFT", "TRACKER-RIGHT"],
                devices.Select(device => device.StableDeviceId));
            return source;
        });

        var first = sampler.Read([right, left]);
        var second = sampler.Read([left, right]);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(2, source.ReadCount);
        Assert.Equal(["TRACKER-LEFT", "TRACKER-RIGHT"], first.Keys);
        Assert.Equal(["TRACKER-LEFT", "TRACKER-RIGHT"], second.Keys);
    }

    [Fact]
    public void TransientIndexChangeRebuildsAccessHandleBeforeReading()
    {
        var initial = Tracker("TRACKER-LEFT", index: 3);
        var moved = Tracker("TRACKER-LEFT", index: 12);
        var created = new List<FakeBatchSource>();
        var sampler = new InternalDriverTrackerBatchSampler(devices =>
        {
            var source = new FakeBatchSource(devices);
            created.Add(source);
            return source;
        });

        _ = sampler.Read([initial]);
        _ = sampler.Read([moved]);

        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[0].ReadCount);
        Assert.Equal(1, created[1].ReadCount);
        Assert.Equal((uint)12, created[1].Devices.Single().TransientDeviceIndex);
    }

    [Fact]
    public void DevicePathChangeRebuildsAccessHandle()
    {
        var initial = Tracker("TRACKER-LEFT", index: 3, path: "/devices/lighthouse/a");
        var changed = Tracker("TRACKER-LEFT", index: 3, path: "/devices/lighthouse/b");
        var factoryCalls = 0;
        var sampler = new InternalDriverTrackerBatchSampler(devices =>
        {
            factoryCalls++;
            return new FakeBatchSource(devices);
        });

        _ = sampler.Read([initial]);
        _ = sampler.Read([changed]);

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void ReturnedCountOrIdentityMismatchFailsClosed()
    {
        var left = Tracker("TRACKER-LEFT", index: 3);
        var right = Tracker("TRACKER-RIGHT", index: 4);
        var wrong = Tracker("TRACKER-OTHER", index: 4);
        var countSampler = new InternalDriverTrackerBatchSampler(
            _ => new FakeBatchSource([left, right], returnedDevices: [left]));
        var identitySampler = new InternalDriverTrackerBatchSampler(
            _ => new FakeBatchSource([left, right], returnedDevices: [left, wrong]));

        Assert.Throws<InvalidDataException>(() => countSampler.Read([left, right]));
        Assert.Throws<InvalidDataException>(() => identitySampler.Read([left, right]));
    }

    private static SteamVrDeviceDescriptor Tracker(
        string serial,
        uint index,
        string? path = null) =>
        new(
            new SteamVrDeviceIdentity(
                serial,
                path ?? $"/devices/lighthouse/{serial}"),
            index,
            SteamVrDeviceCategory.GenericTracker,
            SteamVrControllerRole.None,
            isConnected: true);

    private sealed class FakeBatchSource : TrackedPoseBatchSource
    {
        private readonly IReadOnlyList<SteamVrDeviceDescriptor> _returnedDevices;

        public FakeBatchSource(
            IReadOnlyList<SteamVrDeviceDescriptor> devices,
            IReadOnlyList<SteamVrDeviceDescriptor>? returnedDevices = null)
        {
            Devices = Array.AsReadOnly(devices.ToArray());
            _returnedDevices = returnedDevices ?? Devices;
        }

        public IReadOnlyList<SteamVrDeviceDescriptor> Devices { get; }

        public int ReadCount { get; private set; }

        public IReadOnlyList<TrackedPoseBatchSample> ReadPoses()
        {
            ReadCount++;
            return _returnedDevices
                .Select(device => new TrackedPoseBatchSample(device, default))
                .ToArray();
        }
    }
}
