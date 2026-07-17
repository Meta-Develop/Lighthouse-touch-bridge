namespace Ltb.Configuration.Tests;

public sealed class PlaceholderTests
{
    [Fact]
    public void ConfigurationAssemblyMarkerIsAvailable()
    {
        Assert.Equal("Ltb.Configuration", Ltb.Configuration.ProjectMarker.Name);
    }
}
