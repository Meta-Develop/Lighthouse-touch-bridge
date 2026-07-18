using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class GuiModeAndValidationTests
{
    [Fact]
    public void CommandLineSelectsProductionAndPreservesConsoleOptionNames()
    {
        var options = GuiCommandLineOptions.Parse(
            [
                "wizard",
                "--profiles", "profiles.json",
                "--left-vmt-slot", "2",
                "--right-vmt-slot", "57",
                "--steamvr-settings", "steamvr.vrsettings",
                "--duration", "12.5",
                "--rate", "120",
                "--log", "events.jsonl",
                "--monitor-rate", "40",
                "--reconnect-delay", "1.5",
            ],
            "default.json",
            out var diagnostic);

        Assert.Null(diagnostic);
        Assert.Equal(CalibrationWizardMode.Production, options.Mode);
        Assert.Equal("profiles.json", options.ProfileStorePath);
        Assert.Equal("2", options.LeftVmtSlot);
        Assert.Equal("57", options.RightVmtSlot);
        Assert.Equal("steamvr.vrsettings", options.SteamVrSettingsPath);
        Assert.Equal("12.5", options.CaptureDurationSeconds);
        Assert.Equal("120", options.CaptureRateHz);
        Assert.Equal("events.jsonl", options.LogPath);
        Assert.Equal("40", options.MonitorRateHz);
        Assert.Equal("1.5", options.ReconnectDelaySeconds);
    }

    [Fact]
    public void CommandLineKeepsScriptedDemoReachable()
    {
        var options = GuiCommandLineOptions.Parse(
            ["wizard-demo", "--profiles", "demo.json"],
            "default.json",
            out var diagnostic);

        Assert.Null(diagnostic);
        Assert.Equal(CalibrationWizardMode.ScriptedDemo, options.Mode);
        Assert.Equal("demo.json", options.ProfileStorePath);
    }

    [Fact]
    public async Task InvalidProductionParametersDoNotCreateASession()
    {
        var factory = new RecordingSessionFactory();
        using var viewModel = new CalibrationWizardViewModel(
            factory,
            ProductionOptions() with
            {
                LeftVmtSlot = "7",
                RightVmtSlot = "7",
            });

        await viewModel.StartAsync();

        Assert.Null(factory.ProductionOptions);
        Assert.StartsWith(
            "configuration_error:",
            viewModel.ResultSummary,
            StringComparison.Ordinal);
        Assert.Contains("must be distinct", viewModel.ResultSummary);
    }

    [Fact]
    public async Task ProductionModeCreatesValidatedProductionSession()
    {
        var factory = new RecordingSessionFactory();
        using var viewModel = new CalibrationWizardViewModel(factory, ProductionOptions());

        await viewModel.StartAsync();

        var options = Assert.IsType<ProductionCalibrationWizardSessionOptions>(
            factory.ProductionOptions);
        Assert.Equal("profiles.json", options.ProfileStorePath);
        Assert.Equal(3, options.LeftVmtSlot);
        Assert.Equal(4, options.RightVmtSlot);
        Assert.Equal("steamvr.vrsettings", options.SteamVrSettingsPath);
        Assert.Equal(15d, options.CaptureDurationSeconds);
        Assert.Equal(100d, options.CaptureRateHz);
        Assert.Equal("events.jsonl", options.LogPath);
        Assert.Equal(30d, options.MonitorRateHz);
        Assert.Equal(0.5d, options.ReconnectDelaySeconds);
        Assert.StartsWith("wizard_result: failed", viewModel.ResultSummary);
    }

    [Fact]
    public void InWindowModeSelectionSwitchesBothModeFlags()
    {
        using var viewModel = new CalibrationWizardViewModel(
            new RecordingSessionFactory(),
            new GuiCommandLineOptions { ProfileStorePath = "profiles.json" });

        Assert.True(viewModel.IsScriptedDemoMode);
        Assert.False(viewModel.IsProductionMode);

        viewModel.IsProductionMode = true;

        Assert.True(viewModel.IsProductionMode);
        Assert.False(viewModel.IsScriptedDemoMode);
        Assert.Contains("production", viewModel.CurrentDiagnostic);
    }

    private static GuiCommandLineOptions ProductionOptions() => new()
    {
        Mode = CalibrationWizardMode.Production,
        ProfileStorePath = "profiles.json",
        LeftVmtSlot = "3",
        RightVmtSlot = "4",
        SteamVrSettingsPath = "steamvr.vrsettings",
        CaptureDurationSeconds = "15",
        CaptureRateHz = "100",
        LogPath = "events.jsonl",
        MonitorRateHz = "30",
        ReconnectDelaySeconds = "0.5",
    };

    private sealed class RecordingSessionFactory : ICalibrationWizardSessionFactory
    {
        public ProductionCalibrationWizardSessionOptions? ProductionOptions { get; private set; }

        public ICalibrationWizardSession CreateScripted(
            string profileStorePath,
            string? logPath) => new CompletedSession();

        public ICalibrationWizardSession CreateProduction(
            ProductionCalibrationWizardSessionOptions options)
        {
            ProductionOptions = options;
            return new CompletedSession();
        }
    }

    private sealed class CompletedSession : ICalibrationWizardSession
    {
        public Task<CalibrationWizardResult> RunAsync(
            ICalibrationWizardOutput output,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CalibrationWizardResult(
                false,
                false,
                CalibrationWizardState.Ready,
                [CalibrationWizardState.Ready],
                Array.Empty<CalibrationWizardProfileView>(),
                "synthetic completed session"));
    }
}
