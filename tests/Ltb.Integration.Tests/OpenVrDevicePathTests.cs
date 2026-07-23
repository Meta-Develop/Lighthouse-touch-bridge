using System.Text.RegularExpressions;
using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class OpenVrDevicePathTests
{
    private const string BuildIdentity = "driver_ltb-0.1.0-ipc-1.0";

    [Fact]
    public void NativeLtbRegisteredDeviceTypesDriveManagedLoadedReadiness()
    {
        var nativeSource = File.ReadAllText(FindNativeControllerSource());
        var prefixMatch = Regex.Match(
            nativeSource,
            """constexpr\s+char\s+kRegisteredDeviceTypePrefix\[\]\s*=\s*"(?<prefix>[^"]+)";""");

        Assert.True(
            prefixMatch.Success,
            "The native controller must declare its registered-device type prefix.");
        Assert.Matches(
            """const\s+auto\s+registered_device_type\s*=\s*std::string\{kRegisteredDeviceTypePrefix\}\s*\+\s*serial_number_;""",
            nativeSource);
        Assert.Matches(
            """SetStringProperty\(\s*property_container_,\s*vr::Prop_RegisteredDeviceType_String,\s*registered_device_type\.c_str\(\)\s*\)""",
            nativeSource);

        var prefix = prefixMatch.Groups["prefix"].Value;
        var left = LtbController(
            InternalDriverLoadedReadiness.LeftControllerSerial,
            SteamVrControllerRole.LeftHand,
            1,
            prefix);
        var right = LtbController(
            InternalDriverLoadedReadiness.RightControllerSerial,
            SteamVrControllerRole.RightHand,
            2,
            prefix);
        var result = InternalDriverLoadedReadiness.Evaluate(
            [LighthouseHmd(), left, right],
            BuildIdentity);

        Assert.Equal(
            "/devices/ltb/LTB-TOUCH-LEFT",
            left.Identity.DevicePath);
        Assert.Equal(
            "/devices/ltb/LTB-TOUCH-RIGHT",
            right.Identity.DevicePath);
        Assert.Equal(InternalDriverLoadedReadiness.DriverId, left.Metadata?.DriverId);
        Assert.Equal(InternalDriverLoadedReadiness.DriverId, right.Metadata?.DriverId);
        Assert.True(result.IsReady, result.Diagnostic);
    }

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

    private static SteamVrDeviceDescriptor LtbController(
        string serial,
        SteamVrControllerRole role,
        uint index,
        string registeredDeviceTypePrefix)
    {
        var devicePath = OpenVrDevicePath.Resolve(
            registeredDeviceTypePrefix + serial,
            serial);
        var metadata = OpenVrDeviceMetadataComposer.Compose(
            devicePath,
            InternalDriverLoadedReadiness.TrackingSystemName,
            actualTrackingSystemName: null,
            "Meta-Develop",
            "Lighthouse Touch Bridge Controller",
            InternalDriverLoadedReadiness.ControllerType,
            InternalDriverLoadedReadiness.InputProfilePath,
            BuildIdentity);
        return new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(serial, devicePath),
            index,
            SteamVrDeviceCategory.InputController,
            role,
            isConnected: true,
            metadata);
    }

    private static SteamVrDeviceDescriptor LighthouseHmd() =>
        new(
            new SteamVrDeviceIdentity(
                "ACTIVE-HMD",
                "/devices/lighthouse/ACTIVE-HMD"),
            0,
            SteamVrDeviceCategory.HeadMountedDisplay,
            SteamVrControllerRole.None,
            isConnected: true,
            new SteamVrDeviceMetadata(
                "lighthouse",
                "lighthouse",
                "Bigscreen",
                "Beyond",
                controllerType: null));

    private static string FindNativeControllerSource()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "native",
                "driver_ltb",
                "src",
                "openvr",
                "controller_device.cpp");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            "Could not locate native/driver_ltb/src/openvr/controller_device.cpp.");
    }
}
