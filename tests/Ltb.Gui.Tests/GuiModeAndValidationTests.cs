using System.Xml.Linq;
using Ltb.App;
using Ltb.Gui.ViewModels;

namespace Ltb.Gui.Tests;

public sealed class GuiModeAndValidationTests
{
    [Fact]
    public void MainWindowXamlDeclaresTabbedWorkflowAndSharedSetupContract()
    {
        var document = XDocument.Load(
            LocateRepositoryFile("src", "Ltb.Gui", "MainWindow.axaml"));
        XNamespace avalonia = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var name = x + "Name";

        var tabs = NamedElement(document, avalonia, name, "TabControl", "MainTabs");
        var tabItems = tabs.Elements(avalonia + "TabItem").ToArray();
        Assert.Equal(
            ["Setup", "Status", "Calibration", "Diagnostics (Debug)"],
            tabItems.Select(tab => (string?)tab.Attribute("Header")));

        var header = NamedElement(document, avalonia, name, "Border", "PersistentHeader");
        Assert.Same(tabs.Parent, header.Parent);
        Assert.True(
            tabs.Parent!.Elements().ToList().IndexOf(header) <
            tabs.Parent.Elements().ToList().IndexOf(tabs));
        AssertBinding(
            NamedElement(document, avalonia, name, "TextBlock", "PhaseText"),
            "Text",
            "PhaseText");
        AssertBinding(
            NamedElement(document, avalonia, name, "TextBlock", "OverallStatusText"),
            "Text",
            "OverallStatus");
        Assert.NotNull(
            NamedElement(document, avalonia, name, "Border", "EvidenceOriginBadge"));

        Assert.Equal(4, document.Descendants(avalonia + "ScrollViewer").Count());
        Assert.Equal(
            [
                "SetupScrollViewer",
                "StatusScrollViewer",
                "CalibrationScrollViewer",
                "DiagnosticsScrollViewer",
            ],
            tabItems.Select(
                tab => (string?)Assert.Single(tab.Elements(avalonia + "ScrollViewer"))
                    .Attribute(name)));
        Assert.All(
            tabItems.Select(tab => Assert.Single(tab.Elements(avalonia + "ScrollViewer"))),
            scroll =>
            {
                Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
                Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
            });

        var setupSteps =
            NamedElement(document, avalonia, name, "ItemsControl", "SetupStepsList");
        AssertBinding(setupSteps, "ItemsSource", "SetupSteps");
        AssertBinding(
            NamedElement(document, avalonia, name, "ProgressBar", "PreflightProgress"),
            "IsVisible",
            "IsPreflightProbing");
        AssertBinding(
            NamedElement(document, avalonia, name, "Button", "RefreshPrerequisitesButton"),
            "Command",
            "RefreshPrerequisitesCommand");
        AssertBinding(
            NamedElement(document, avalonia, name, "TextBlock", "StartGateReasonText"),
            "Text",
            "StartGateReason");
        AssertBinding(
            NamedElement(document, avalonia, name, "TextBlock", "CalibrationGateReasonText"),
            "Text",
            "CalibrationGateReason");

        var actionPanel =
            NamedElement(document, avalonia, name, "WrapPanel", "SetupActionsPanel");
        var actionButtons = actionPanel.Elements(avalonia + "Button").ToArray();
        Assert.Equal(
            ["ActionButton", "CalibrationButton"],
            actionButtons.Select(button => (string?)button.Attribute(name)));
        Assert.Equal("primary", (string?)actionButtons[0].Attribute("Classes"));
        Assert.Equal("secondary", (string?)actionButtons[1].Attribute("Classes"));
        AssertBinding(actionButtons[0], "Command", "ActionCommand");
        AssertBinding(actionButtons[1], "Command", "CalibrationCommand");

        var maintenance =
            NamedElement(document, avalonia, name, "Expander", "MaintenanceExpander");
        Assert.Equal("False", (string?)maintenance.Attribute("IsExpanded"));
        Assert.Contains("Advanced", (string?)maintenance.Attribute("Header"));
        Assert.NotNull(
            maintenance.Descendants(avalonia + "Button")
                .Single(button => (string?)button.Attribute(name) == "RemoveDriverButton"));
        Assert.NotNull(
            maintenance.Descendants(avalonia + "TextBlock")
                .Single(text => (string?)text.Attribute(name) == "LegacyNotice"));

        AssertBinding(
            NamedElement(document, avalonia, name, "ToggleSwitch", "DebugToggle"),
            "IsChecked",
            "IsDebugEnabled");
        AssertBinding(
            NamedElement(document, avalonia, name, "Border", "DebugDrawer"),
            "IsVisible",
            "IsDebugEnabled");
        var diagnosticsText = string.Join(
            " ",
            tabItems[3]
                .Descendants(avalonia + "TextBlock")
                .Select(text => (string?)text.Attribute("Text") ?? string.Empty));
        Assert.Contains("10 Hz maximum", diagnosticsText, StringComparison.Ordinal);
        Assert.Contains("fixed 600-sample ring", diagnosticsText, StringComparison.Ordinal);
        Assert.Contains("Software lower bound only", diagnosticsText, StringComparison.Ordinal);
        Assert.Contains("motion-to-photon latency", diagnosticsText, StringComparison.Ordinal);
    }

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

    private static XElement NamedElement(
        XDocument document,
        XNamespace avalonia,
        XName nameAttribute,
        string elementName,
        string controlName) =>
        document
            .Descendants(avalonia + elementName)
            .Single(element => (string?)element.Attribute(nameAttribute) == controlName);

    private static void AssertBinding(
        XElement element,
        string attributeName,
        string propertyName) =>
        Assert.Equal(
            $"{{Binding {propertyName}}}",
            (string?)element.Attribute(attributeName));

    private static string LocateRepositoryFile(params string[] relativeParts)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidateParts = new string[relativeParts.Length + 1];
                candidateParts[0] = directory.FullName;
                Array.Copy(relativeParts, 0, candidateParts, 1, relativeParts.Length);
                var candidate = Path.Combine(candidateParts);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException(
            $"Unable to locate repository file '{Path.Combine(relativeParts)}'.");
    }
}
