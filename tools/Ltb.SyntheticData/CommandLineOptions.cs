using Ltb.Calibration;

namespace Ltb.SyntheticData;

internal sealed record CommandLineOptions(
    SyntheticCommand Command,
    SyntheticScenario Scenario,
    int Seed,
    CalibrationPolicy Policy,
    string? OutputPath,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out CommandLineOptions options,
        out string? error)
    {
        var scenario = SyntheticScenario.Clean;
        var seed = 20260717;
        var policy = CalibrationPolicy.Auto;
        string? outputPath = null;
        var showHelp = false;
        var command = SyntheticCommand.Calibrate;
        var startIndex = 0;

        if (arguments.Count > 0 &&
            string.Equals(arguments[0], "record-simulate", StringComparison.Ordinal))
        {
            command = SyntheticCommand.RecordSimulate;
            startIndex = 1;
        }

        for (var index = startIndex; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--scenario":
                    if (!TryReadValue(arguments, ref index, argument, out var scenarioValue, out error) ||
                        !TryParseScenario(scenarioValue!, out scenario))
                    {
                        error ??= $"Unknown scenario '{scenarioValue}'.";
                        options = Empty;
                        return false;
                    }

                    break;
                case "--seed":
                    if (!TryReadValue(arguments, ref index, argument, out var seedValue, out error) ||
                        !int.TryParse(seedValue, out seed))
                    {
                        error ??= $"Seed must be a 32-bit integer; received '{seedValue}'.";
                        options = Empty;
                        return false;
                    }

                    break;
                case "--policy":
                    if (!TryReadValue(arguments, ref index, argument, out var policyValue, out error) ||
                        !TryParsePolicy(policyValue!, out policy))
                    {
                        error ??= $"Unknown policy '{policyValue}'.";
                        options = Empty;
                        return false;
                    }

                    break;
                case "--output":
                    if (!TryReadValue(arguments, ref index, argument, out outputPath, out error))
                    {
                        options = Empty;
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(outputPath))
                    {
                        error = "Output path cannot be empty.";
                        options = Empty;
                        return false;
                    }

                    break;
                default:
                    error = $"Unknown option '{argument}'.";
                    options = Empty;
                    return false;
            }
        }

        if (!showHelp && command == SyntheticCommand.RecordSimulate && outputPath is null)
        {
            error = "The record-simulate command requires --output <recording.json>.";
            options = Empty;
            return false;
        }

        if (!showHelp && command == SyntheticCommand.Calibrate && outputPath is not null)
        {
            error = "--output is only valid with the record-simulate command.";
            options = Empty;
            return false;
        }

        options = new CommandLineOptions(command, scenario, seed, policy, outputPath, showHelp);
        error = null;
        return true;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Lighthouse Touch Bridge synthetic data utility");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  dotnet run --project tools/Ltb.SyntheticData -- [options]");
        writer.WriteLine("  dotnet run --project tools/Ltb.SyntheticData -- record-simulate --output <recording.json> [options]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --scenario <name>  clean | noisy | static | single-axis | pure-translation | translation-degenerate");
        writer.WriteLine("                     Default: clean");
        writer.WriteLine("  --seed <integer>   Deterministic random seed. Default: 20260717");
        writer.WriteLine("  --policy <name>    auto | rotation | full. Default: auto (offline report only)");
        writer.WriteLine("  --output <path>    Destination for record-simulate (required for that command)");
        writer.WriteLine("  -h, --help         Show this help.");
        writer.WriteLine();
        writer.WriteLine("With no subcommand, the Milestone 0 truth-aligned report behavior is preserved.");
        writer.WriteLine("record-simulate exports raw lagged streams using recording schema 1.");
    }

    private static CommandLineOptions Empty { get; } =
        new(default, default, default, default, null, default);

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= arguments.Count)
        {
            value = null;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        value = arguments[++index];
        error = null;
        return true;
    }

    private static bool TryParseScenario(string value, out SyntheticScenario scenario)
    {
        scenario = value.ToLowerInvariant() switch
        {
            "clean" => SyntheticScenario.Clean,
            "noisy" => SyntheticScenario.Noisy,
            "static" => SyntheticScenario.Static,
            "single-axis" or "single-axis-rotation" => SyntheticScenario.SingleAxisRotation,
            "pure-translation" => SyntheticScenario.PureTranslation,
            "translation-degenerate" or "partial-position" => SyntheticScenario.TranslationDegenerate,
            _ => (SyntheticScenario)(-1),
        };
        return Enum.IsDefined(scenario);
    }

    private static bool TryParsePolicy(string value, out CalibrationPolicy policy)
    {
        policy = value.ToLowerInvariant() switch
        {
            "auto" => CalibrationPolicy.Auto,
            "rotation" or "rotation-only" => CalibrationPolicy.RotationOnly,
            "full" or "full-6dof" or "full-six-dof" => CalibrationPolicy.FullSixDof,
            _ => (CalibrationPolicy)(-1),
        };
        return Enum.IsDefined(policy);
    }
}

internal enum SyntheticCommand
{
    Calibrate,
    RecordSimulate,
}
