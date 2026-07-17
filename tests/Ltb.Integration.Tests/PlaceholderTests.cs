namespace Ltb.Integration.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void RuntimeComponentMarkersAreAvailable()
    {
        Assert.Equal("Ltb.Core", Ltb.Core.ProjectMarker.Name);
        Assert.Equal("Ltb.OpenVr", Ltb.OpenVr.ProjectMarker.Name);
        Assert.Equal("Ltb.Alvr", Ltb.Alvr.ProjectMarker.Name);
        Assert.Equal("Ltb.Vmt", Ltb.Vmt.ProjectMarker.Name);
        Assert.Equal("Ltb.Calibration", Ltb.Calibration.ProjectMarker.Name);
        Assert.Equal("Ltb.Configuration", Ltb.Configuration.ProjectMarker.Name);
    }
}
