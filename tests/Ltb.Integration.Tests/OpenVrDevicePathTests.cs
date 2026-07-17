using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class OpenVrDevicePathTests
{
    [Theory]
    [InlineData("vmt/VMT_1", "/devices/vmt/VMT_1")]
    [InlineData("/devices/vmt/VMT_1", "/devices/vmt/VMT_1")]
    [InlineData("lighthouse/vive_tracker", "/devices/lighthouse/vive_tracker")]
    [InlineData("oculus/controller", "/devices/oculus/controller")]
    public void RegisteredDeviceTypesNormalizeToCanonicalOverridePaths(
        string registeredDeviceType,
        string expected)
    {
        var normalized = OpenVrDevicePath.TryNormalize(
            registeredDeviceType,
            out var actual);

        Assert.True(normalized);
        Assert.Equal(expected, actual);
        Assert.Equal(
            expected,
            OpenVrDevicePath.Resolve(registeredDeviceType, "SERIAL-TEST"));
    }

    [Theory]
    [InlineData("/devices/oculus/Quest2-Left_Controller_Left", "oculus")]
    [InlineData("/devices/lighthouse/LHR-1234", "lighthouse")]
    public void CanonicalDevicePathExposesRegisteredDriverIdentity(
        string canonicalPath,
        string expectedDriver)
    {
        Assert.True(OpenVrDevicePath.TryGetDriverId(canonicalPath, out var driver));
        Assert.Equal(expectedDriver, driver);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vmt")]
    [InlineData("/devices/vmt")]
    [InlineData("/devices/vmt/VMT_1/extra")]
    [InlineData("/user/hand/left")]
    [InlineData("vmt//VMT_1")]
    [InlineData("vmt/../VMT_1")]
    [InlineData("vmt/VMT_1\n")]
    [InlineData("vmt/VMT 1")]
    public void MalformedOrUnavailableValuesAreNotOverrideCapable(string? value)
    {
        var normalized = OpenVrDevicePath.TryNormalize(value, out var path);

        Assert.False(normalized);
        Assert.Equal(string.Empty, path);

        var fallback = OpenVrDevicePath.Resolve(value, "LHR-TEST/0001");
        Assert.Equal("openvr://device/LHR-TEST%2F0001", fallback);
        Assert.Throws<ArgumentException>(() => new TrackingOverrideBinding(
            fallback,
            TrackingOverrideBinding.LeftHandPath));
    }
}
