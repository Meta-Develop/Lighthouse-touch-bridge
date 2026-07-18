namespace Ltb.Gui;

public enum CalibrationWizardMode
{
    ScriptedDemo,
    Production,
}

/// <summary>
/// Editable GUI launch values. Strings preserve exactly what the user typed;
/// the view model parses them before asking <c>Ltb.App</c> to validate the
/// production policy contract.
/// </summary>
public sealed record GuiCommandLineOptions
{
    public CalibrationWizardMode Mode { get; init; } = CalibrationWizardMode.ScriptedDemo;

    public string ProfileStorePath { get; init; } = string.Empty;

    public string LeftVmtSlot { get; init; } = "0";

    public string RightVmtSlot { get; init; } = "1";

    public string SteamVrSettingsPath { get; init; } = string.Empty;

    public string CaptureDurationSeconds { get; init; } = "10";

    public string CaptureRateHz { get; init; } = "90";

    public string LogPath { get; init; } = string.Empty;

    public string MonitorRateHz { get; init; } = "20";

    public string ReconnectDelaySeconds { get; init; } = "0.25";

    public static GuiCommandLineOptions Parse(
        IReadOnlyList<string> arguments,
        string defaultProfileStorePath,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileStorePath);
        diagnostic = null;
        var options = new GuiCommandLineOptions
        {
            ProfileStorePath = defaultProfileStorePath,
        };
        var index = 0;
        if (arguments.Count > 0 && arguments[0] is "wizard" or "wizard-demo")
        {
            options = options with
            {
                Mode = arguments[0] == "wizard"
                    ? CalibrationWizardMode.Production
                    : CalibrationWizardMode.ScriptedDemo,
            };
            index++;
        }

        while (index < arguments.Count)
        {
            var name = arguments[index++];
            if (name is "-h" or "--help")
            {
                diagnostic = "Choose scripted demo or production, edit the fields, then press Start. " +
                    "Accepted options: --profiles, --left-vmt-slot, --right-vmt-slot, " +
                    "--steamvr-settings, --duration, --rate, --log, --monitor-rate, " +
                    "and --reconnect-delay.";
                continue;
            }

            if (!TryReadValue(arguments, ref index, name, out var value, out diagnostic))
            {
                return options;
            }

            options = name switch
            {
                "--profiles" => options with { ProfileStorePath = value },
                "--left-vmt-slot" => options with { LeftVmtSlot = value },
                "--right-vmt-slot" => options with { RightVmtSlot = value },
                "--steamvr-settings" => options with { SteamVrSettingsPath = value },
                "--duration" => options with { CaptureDurationSeconds = value },
                "--rate" => options with { CaptureRateHz = value },
                "--log" => options with { LogPath = value },
                "--monitor-rate" => options with { MonitorRateHz = value },
                "--reconnect-delay" => options with { ReconnectDelaySeconds = value },
                _ => Unknown(options, name, out diagnostic),
            };
            if (diagnostic is not null)
            {
                return options;
            }
        }

        return options;
    }

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string value,
        out string? diagnostic)
    {
        if (option is not (
                "--profiles" or
                "--left-vmt-slot" or
                "--right-vmt-slot" or
                "--steamvr-settings" or
                "--duration" or
                "--rate" or
                "--log" or
                "--monitor-rate" or
                "--reconnect-delay"))
        {
            value = string.Empty;
            diagnostic = $"Unknown GUI option '{option}'.";
            return false;
        }

        if (index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
        {
            value = string.Empty;
            diagnostic = $"Option '{option}' requires a value.";
            return false;
        }

        value = arguments[index++];
        diagnostic = null;
        return true;
    }

    private static GuiCommandLineOptions Unknown(
        GuiCommandLineOptions options,
        string option,
        out string? diagnostic)
    {
        diagnostic = $"Unknown GUI option '{option}'.";
        return options;
    }
}
