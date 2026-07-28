using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class ExternalSteamVrIntegrationWarningTests
{
    [Theory]
    [InlineData(
        @"C:\SteamVR\drivers\01SpaceCalibrator\\",
        ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
        ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation)]
    [InlineData(
        "/opt/steamvr/drivers/space-calibrator/",
        ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
        ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation)]
    [InlineData(
        "/opt/steamvr/drivers/OpenVR_Space-Calibrator",
        ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
        ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation)]
    [InlineData(
        @"C:\SteamVR\drivers\BIGSCREEN_BEYOND",
        ExternalSteamVrIntegrationIdentity.BigscreenBeyond,
        ExternalSteamVrIntegrationCategory.AdjacentNonControllerPresentation)]
    [InlineData(
        "/opt/steamvr/drivers/Virtual-Motion_Tracker",
        ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
        ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict)]
    [InlineData(
        @"C:\SteamVR\drivers\V-M-T",
        ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
        ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict)]
    [InlineData(
        "/opt/steamvr/drivers/ALVR_SERVER/",
        ExternalSteamVrIntegrationIdentity.AlvrServer,
        ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict)]
    public void RecognizesPortableNormalizedRegistrationLeafNames(
        string registeredRoot,
        ExternalSteamVrIntegrationIdentity identity,
        ExternalSteamVrIntegrationCategory category)
    {
        var warning = Assert.Single(
            ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots([registeredRoot]));

        Assert.Equal(registeredRoot, warning.RegisteredDriverRoot);
        Assert.Equal(identity, warning.Identity);
        Assert.Equal(category, warning.Category);
        Assert.Equal(
            category ==
                ExternalSteamVrIntegrationCategory.PotentialControllerPresentationConflict,
            warning.IsPotentialControllerPresentationConflict);
        Assert.False(string.IsNullOrWhiteSpace(warning.DisplayName));
        Assert.Contains(
            "Registration alone does not show",
            warning.Guidance,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/steamvr/drivers/arbitrary-third-party")]
    [InlineData("/opt/steamvr/drivers/not-vmt")]
    [InlineData("/opt/steamvr/drivers/alvr")]
    [InlineData("/opt/steamvr/drivers/bigscreen")]
    [InlineData(@"C:\owners\vmt\drivers\custom-driver")]
    [InlineData("")]
    [InlineData("   ")]
    public void OmitsUnknownOrBlankRegistrations(string registeredRoot)
    {
        var warnings =
            ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots([registeredRoot]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void PreservesRecognizedRegistryOrderAndDuplicates()
    {
        string[] registeredRoots =
        [
            "/drivers/vmt",
            "/drivers/unknown",
            @"C:\drivers\bigscreenbeyond",
            "/drivers/vmt",
            "/drivers/alvr_server",
            "/drivers/space-calibrator",
        ];

        var warnings =
            ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots(registeredRoots);

        Assert.Equal(
            [
                ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
                ExternalSteamVrIntegrationIdentity.BigscreenBeyond,
                ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
                ExternalSteamVrIntegrationIdentity.AlvrServer,
                ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
            ],
            warnings.Select(warning => warning.Identity));
        Assert.Equal(
            [
                registeredRoots[0],
                registeredRoots[2],
                registeredRoots[3],
                registeredRoots[4],
                registeredRoots[5],
            ],
            warnings.Select(warning => warning.RegisteredDriverRoot));
    }

    [Fact]
    public void ReturnsReadOnlyEvidenceWithoutMutatingTheInput()
    {
        var registeredRoots = new List<string>
        {
            "/drivers/alvr_server",
            "/drivers/custom",
        };
        var originalRoots = registeredRoots.ToArray();

        var warnings =
            ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots(registeredRoots);

        Assert.Equal(originalRoots, registeredRoots);
        var mutableView = Assert.IsAssignableFrom<IList<ExternalSteamVrIntegrationWarning>>(
            warnings);
        Assert.Throws<NotSupportedException>(() => mutableView.Clear());
    }

    [Fact]
    public void RejectsMissingRegistryEvidence()
    {
        Assert.Throws<ArgumentNullException>(
            () => ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots(null!));
    }

    [Fact]
    public void KnownGuidanceDistinguishesAdjacentAndPotentialConflictIntegrations()
    {
        var warnings = ExternalSteamVrIntegrationWarning.FromRegisteredDriverRoots(
        [
            "/drivers/01spacecalibrator",
            "/drivers/bigscreenbeyond",
            "/drivers/vmt",
            "/drivers/alvr_server",
        ]);

        Assert.Collection(
            warnings,
            warning =>
            {
                Assert.Equal(
                    ExternalSteamVrIntegrationIdentity.SpaceCalibrator,
                    warning.Identity);
                Assert.False(warning.IsPotentialControllerPresentationConflict);
                Assert.Contains(
                    "not itself a controller-presentation path",
                    warning.Guidance,
                    StringComparison.Ordinal);
            },
            warning =>
            {
                Assert.Equal(
                    ExternalSteamVrIntegrationIdentity.BigscreenBeyond,
                    warning.Identity);
                Assert.False(warning.IsPotentialControllerPresentationConflict);
                Assert.Contains(
                    "adjacent HMD integration",
                    warning.Guidance,
                    StringComparison.Ordinal);
            },
            warning =>
            {
                Assert.Equal(
                    ExternalSteamVrIntegrationIdentity.VirtualMotionTracker,
                    warning.Identity);
                Assert.True(warning.IsPotentialControllerPresentationConflict);
                Assert.Contains(
                    "LTB will not change them",
                    warning.Guidance,
                    StringComparison.Ordinal);
            },
            warning =>
            {
                Assert.Equal(
                    ExternalSteamVrIntegrationIdentity.AlvrServer,
                    warning.Identity);
                Assert.True(warning.IsPotentialControllerPresentationConflict);
                Assert.Contains(
                    "official Meta Horizon Link",
                    warning.Guidance,
                    StringComparison.Ordinal);
            });
    }
}
