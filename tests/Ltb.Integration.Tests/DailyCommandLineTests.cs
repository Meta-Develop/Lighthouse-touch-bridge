using Ltb.App;
using Ltb.OpenVr;

namespace Ltb.Integration.Tests;

public sealed class DailyCommandLineTests
{
    private const string DailyUsage =
        "  dotnet run --project src/Ltb.App -- daily --profiles <profile-store.json> " +
        "--left-vmt-slot <0..57> --right-vmt-slot <0..57> " +
        "--steamvr-settings <steamvr.vrsettings> [--log <events.jsonl>] " +
        "[--monitor-rate <hz>] [--reconnect-delay <seconds>]";

    private const string DailyExitMeanings =
        "Daily exit codes: 0 clean cancellation, 2 startup/profile/application failure, " +
        "3 SteamVR/runtime health termination, 4 any incomplete cleanup or rollback.";

    private const string WizardUsage =
        "  dotnet run --project src/Ltb.App -- wizard-demo " +
        "--profiles <profile-store.json> [--log <events.jsonl>]";

    [Fact]
    public void DailyCommandParsesRequiredAndOptionalSettings()
    {
        Assert.True(AppCommandLineOptions.TryParse(
            [
                "daily",
                "--profiles", "profiles.json",
                "--left-vmt-slot", "3",
                "--right-vmt-slot", "57",
                "--steamvr-settings", "steamvr.vrsettings",
                "--log", "events.jsonl",
                "--monitor-rate", "40",
                "--reconnect-delay", "1.5",
            ],
            out var options,
            out var error), error);

        Assert.Equal(AppCommand.Daily, options.Command);
        Assert.Equal("profiles.json", options.DailyProfileStorePath);
        Assert.Equal(3, options.DailyLeftVmtSlot);
        Assert.Equal(57, options.DailyRightVmtSlot);
        Assert.Equal("steamvr.vrsettings", options.SteamVrSettingsPath);
        Assert.Equal("events.jsonl", options.DailyLogPath);
        Assert.Equal(40d, options.MonitorRateHz);
        Assert.Equal(1.5d, options.DailyReconnectDelaySeconds);
    }

    [Fact]
    public void DailyCommandUsesDocumentedDefaultsWhenOptionalSettingsAreOmitted()
    {
        Assert.True(AppCommandLineOptions.TryParse(
            RequiredDailyArguments(),
            out var options,
            out var error), error);

        Assert.Null(options.DailyLogPath);
        Assert.Equal(20d, options.MonitorRateHz);
        Assert.Equal(0.25d, options.DailyReconnectDelaySeconds);
    }

    [Theory]
    [InlineData("--profiles")]
    [InlineData("--left-vmt-slot")]
    [InlineData("--right-vmt-slot")]
    [InlineData("--steamvr-settings")]
    public void DailyCommandRejectsEachMissingRequiredSetting(string optionToOmit)
    {
        var arguments = WithoutOption(RequiredDailyArguments(), optionToOmit);

        Assert.False(AppCommandLineOptions.TryParse(arguments, out _, out var error));
        Assert.Contains("daily command requires", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-2, 2, "requires")]
    [InlineData(58, 2, "between 0 and 57")]
    [InlineData(0, 58, "between 0 and 57")]
    [InlineData(11, 11, "must be distinct")]
    public void DailyCommandRejectsInvalidOrDuplicateSlots(
        int leftSlot,
        int rightSlot,
        string expectedDiagnostic)
    {
        var arguments = RequiredDailyArguments();
        arguments[4] = leftSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);
        arguments[6] = rightSlot.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(AppCommandLineOptions.TryParse(arguments, out _, out var error));
        Assert.Contains(expectedDiagnostic, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--monitor-rate", "0", "--monitor-rate")]
    [InlineData("--monitor-rate", "1000.001", "--monitor-rate")]
    [InlineData("--monitor-rate", "NaN", "--monitor-rate")]
    [InlineData("--reconnect-delay", "0", "--reconnect-delay")]
    [InlineData("--reconnect-delay", "300.001", "--reconnect-delay")]
    [InlineData("--reconnect-delay", "NaN", "--reconnect-delay")]
    public void DailyCommandRejectsInvalidMonitorAndReconnectValues(
        string option,
        string value,
        string expectedDiagnostic)
    {
        var arguments = RequiredDailyArguments().ToList();
        arguments.Add(option);
        arguments.Add(value);

        Assert.False(AppCommandLineOptions.TryParse(arguments, out _, out var error));
        Assert.Contains(expectedDiagnostic, error, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonDailyCommandsWithDailyOptions))]
    public void DailyOnlyOptionsAreRejectedByOtherCommands(string[] arguments)
    {
        Assert.False(AppCommandLineOptions.TryParse(arguments, out _, out _));
    }

    [Fact]
    public void UsageContainsExactDailyAndWizardCommandsAndDailyExitMeanings()
    {
        using var writer = new StringWriter();

        AppCommandLineOptions.PrintUsage(writer);

        var lines = writer.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(DailyUsage, lines);
        Assert.Contains(WizardUsage, lines);
        Assert.Contains(DailyExitMeanings, lines);
    }

    [Fact]
    public void DeviceDiagnosticsExposeGeneralizedCapabilitiesAndInputProfile()
    {
        var device = new SteamVrDeviceDescriptor(
            new SteamVrDeviceIdentity(
                "TOUCH-QUEST3-LEFT",
                "/devices/alvr/TOUCH-QUEST3-LEFT"),
            4,
            SteamVrDeviceCategory.InputController,
            SteamVrControllerRole.LeftHand,
            true,
            new SteamVrDeviceMetadata(
                "alvr",
                "ALVR",
                "Meta",
                "Meta Quest 3 Controller",
                "meta_touch",
                "/input/meta_quest_3_touch_plus_profile.json"));
        using var writer = new StringWriter();

        Program.WriteDeviceDetails(writer, device);

        var output = writer.ToString();
        Assert.Contains("has_position: true", output);
        Assert.Contains("physical_pose_source_eligible: false", output);
        Assert.Contains("virtual_pose_source: false", output);
        Assert.Contains("controller_family: meta_touch", output);
        Assert.Contains("controller_runtime: ALVR", output);
        Assert.Contains("controller_model: Quest 3 Touch Plus", output);
        Assert.Contains(
            "input_profile: /input/meta_quest_3_touch_plus_profile.json",
            output);
    }

    [Theory]
    [MemberData(nameof(UnrelatedCommandsWithLogOption))]
    public void LogOptionIsRejectedByCommandsWithoutStructuredLogSupport(string[] arguments)
    {
        Assert.False(AppCommandLineOptions.TryParse(arguments, out _, out _));
    }

    public static TheoryData<string[]> NonDailyCommandsWithDailyOptions => new()
    {
        { ["devices", "--left-vmt-slot", "1"] },
        {
            [
                "bridge",
                "--profile", "profile.json",
                "--vmt-slot", "1",
                "--steamvr-settings", "steamvr.vrsettings",
                "--right-vmt-slot", "2",
            ]
        },
        { ["wizard-demo", "--profiles", "profiles.json", "--reconnect-delay", "0.25"] },
        { ["devices", "--reconnect-delay", "0.25"] },
    };

    public static TheoryData<string[]> UnrelatedCommandsWithLogOption => new()
    {
        { ["devices", "--log", "events.jsonl"] },
        {
            [
                "bridge",
                "--profile", "profile.json",
                "--vmt-slot", "1",
                "--steamvr-settings", "steamvr.vrsettings",
                "--log", "events.jsonl",
            ]
        },
        {
            [
                "record",
                "--tracker", "TRACKER",
                "--controller", "TOUCH",
                "--output", "recording.json",
                "--override-released",
                "--log", "events.jsonl",
            ]
        },
    };

    private static string[] RequiredDailyArguments() =>
    [
        "daily",
        "--profiles", "profiles.json",
        "--left-vmt-slot", "1",
        "--right-vmt-slot", "2",
        "--steamvr-settings", "steamvr.vrsettings",
    ];

    private static string[] WithoutOption(string[] arguments, string optionToOmit)
    {
        var optionIndex = Array.IndexOf(arguments, optionToOmit);
        Assert.True(optionIndex >= 0);
        return arguments
            .Where((_, index) => index != optionIndex && index != optionIndex + 1)
            .ToArray();
    }
}
