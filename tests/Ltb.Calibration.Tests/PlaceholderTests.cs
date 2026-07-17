namespace Ltb.Calibration.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void CalibrationAssemblyMarkerIsAvailable()
    {
        Assert.Equal("Ltb.Calibration", Ltb.Calibration.ProjectMarker.Name);
    }
}
