namespace Ltb.MetaLink.Tests;

public sealed class ClockMappingTests
{
    [Fact]
    public void MapsOffsetFromBracketMidpointAndPreservesUncertainty()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(metaSeconds: 100d, appBeforeSeconds: 10d, appAfterSeconds: 10.2d);

        var mapped = mapper.Map(101d, MetaLinkHand.Left);

        Assert.Equal(11.1d, mapped.AppMonotonicSeconds, 9);
        Assert.Equal(11_100_000_000L, mapped.AppMonotonicNanoseconds);
        Assert.Equal(1d, mapped.EstimatedRate, 12);
        Assert.Equal(0.1d, mapped.UncertaintySeconds, 12);
        Assert.False(mapped.MonotonicityAdjusted);
    }

    [Fact]
    public void EstimatesBoundedClockDriftFromFirstAndLatestPairs()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        mapper.Observe(200d, 110.1d, 110.1d);

        var mapped = mapper.Map(201d, MetaLinkHand.Right);

        Assert.Equal(1.001d, mapped.EstimatedRate, 12);
        Assert.Equal(111.101d, mapped.AppMonotonicSeconds, 9);
    }

    [Fact]
    public void ClampsImplausibleRateAndMaintainsPerHandMonotonicNanoseconds()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);
        mapper.Observe(200d, 112d, 112d);

        var firstLeft = mapper.Map(200d, MetaLinkHand.Left);
        var repeatedLeft = mapper.Map(200d, MetaLinkHand.Left);
        var firstRight = mapper.Map(200d, MetaLinkHand.Right);

        Assert.Equal(1.01d, firstLeft.EstimatedRate, 12);
        Assert.True(repeatedLeft.MonotonicityAdjusted);
        Assert.Equal(firstLeft.AppMonotonicNanoseconds + 1, repeatedLeft.AppMonotonicNanoseconds);
        Assert.False(firstRight.MonotonicityAdjusted);
        Assert.Equal(firstLeft.AppMonotonicNanoseconds, firstRight.AppMonotonicNanoseconds);
    }

    [Fact]
    public void ResetRequiresANewObservation()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(1d, 2d, 2d);
        mapper.Reset();

        Assert.Throws<InvalidOperationException>(() => mapper.Map(1d, MetaLinkHand.Left));
    }
}
