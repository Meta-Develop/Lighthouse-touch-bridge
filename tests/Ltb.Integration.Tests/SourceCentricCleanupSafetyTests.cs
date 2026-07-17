using Ltb.App;

namespace Ltb.Integration.Tests;

public sealed class SourceCentricCleanupSafetyTests
{
    [Fact]
    public async Task CaptureSafetyReleasesBothHandsBeforeDeactivatingEitherSlot()
    {
        var timeline = new List<string>();
        var backend = new FakeProductionWizardBackend(timeline, "unused-profiles.json");
        var runtime = CreateRuntime(backend);
        var devices = await runtime.WaitForDevicesAsync(CancellationToken.None);

        await runtime.ReleaseOverridesAsync(devices, CancellationToken.None);

        Assert.Equal(
            [
                "release:override:left",
                "release:override:right",
                "release:vmt:left",
                "release:vmt:right",
            ],
            backend.MutationJournal);
        Assert.Empty(backend.ActiveOverrideHands);
        Assert.Empty(backend.ActiveVmtHands);
    }

    [Fact]
    public async Task CaptureSafetyReleaseFailureAttemptsOtherHandAndDeactivatesNeitherSlot()
    {
        var timeline = new List<string>();
        var backend = new FakeProductionWizardBackend(timeline, "unused-profiles.json")
        {
            FailReleaseOverrideCall = 1,
        };
        var runtime = CreateRuntime(backend);
        var devices = await runtime.WaitForDevicesAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            runtime.ReleaseOverridesAsync(devices, CancellationToken.None));

        Assert.Contains("did not safely complete", failure.Message);
        Assert.Equal(
            [
                "release:override:left",
                "release:override:right",
            ],
            backend.MutationJournal);
        Assert.Empty(backend.ActiveOverrideHands);
        Assert.Equal(
            [CalibrationWizardHand.Left, CalibrationWizardHand.Right],
            backend.ActiveVmtHands.OrderBy(hand => hand));
    }

    private static ProductionCalibrationWizardRuntime CreateRuntime(
        FakeProductionWizardBackend backend) =>
        new(
            backend,
            new ProductionCalibrationWizardOptions
            {
                DeviceRetryDelay = TimeSpan.FromMilliseconds(10),
                CleanupTimeout = TimeSpan.FromMilliseconds(100),
            });
}
