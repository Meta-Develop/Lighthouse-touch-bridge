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
    public void PreservesConservativeBracketUncertaintyAcrossRefreshes()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 9.8d, 10.2d);
        mapper.Observe(101d, 11d, 11d);

        var mapped = mapper.Map(101d, MetaLinkHand.Left);

        Assert.Equal(0.2d, mapped.UncertaintySeconds, 12);
    }

    [Fact]
    public void RejectsImplausibleClockScaleInsteadOfClampingIt()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);

        var error = Assert.Throws<InvalidOperationException>(
            () => mapper.Observe(200d, 112d, 112d));

        Assert.Contains("implausible scale", error.Message, StringComparison.Ordinal);
        var stillMapped = mapper.Map(100d, MetaLinkHand.Left);
        Assert.Equal(1d, stillMapped.EstimatedRate);
        Assert.False(stillMapped.MonotonicityAdjusted);
    }

    [Theory]
    [InlineData(100d, 11d, 11d)]
    [InlineData(99d, 11d, 11d)]
    [InlineData(101d, 10d, 10d)]
    [InlineData(101d, 9d, 9d)]
    public void RejectsRepeatedOrRegressingObservations(
        double metaSeconds,
        double appBeforeSeconds,
        double appAfterSeconds)
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);

        Assert.Throws<InvalidOperationException>(
            () => mapper.Observe(metaSeconds, appBeforeSeconds, appAfterSeconds));
    }

    [Fact]
    public void RejectsDiscontinuousObservationEvenWhenAggregateScaleIsPlausible()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10d);

        var error = Assert.Throws<InvalidOperationException>(
            () => mapper.Observe(200d, 110.5d, 110.5d));

        Assert.Contains("discontinuous", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsRepeatedRegressingAndDiscontinuousPerHandTimestamps()
    {
        var mapper = new MetaClockMapper();
        mapper.Observe(100d, 10d, 10.2d);

        var left = mapper.Map(100d, MetaLinkHand.Left);
        var right = mapper.Map(100d, MetaLinkHand.Right);

        Assert.False(left.MonotonicityAdjusted);
        Assert.Equal(left.AppMonotonicNanoseconds, right.AppMonotonicNanoseconds);
        Assert.Throws<InvalidOperationException>(() => mapper.Map(100d, MetaLinkHand.Left));
        Assert.Throws<InvalidOperationException>(() => mapper.Map(99d, MetaLinkHand.Left));
        Assert.Throws<InvalidOperationException>(() => mapper.Map(106d, MetaLinkHand.Right));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteObservationsAndHandTimestamps(double invalid)
    {
        var mapper = new MetaClockMapper();

        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Observe(invalid, 1d, 1d));
        mapper.Observe(1d, 1d, 1d);
        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(invalid, MetaLinkHand.Left));
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
