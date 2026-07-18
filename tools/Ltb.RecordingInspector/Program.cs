using System.Globalization;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.RecordingInspector;

internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        return RecordingInspectorApplication.Run(args, Console.Out, Console.Error);
    }
}

/// <summary>Runs the recording-inspector command against caller-provided output writers.</summary>
internal static class RecordingInspectorApplication
{
    public static int Run(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!InspectorCommandLineOptions.TryParse(args, out var options, out var parseError))
        {
            error.WriteLine(parseError);
            error.WriteLine();
            InspectorCommandLineOptions.PrintUsage(error);
            return 1;
        }

        if (options.ShowHelp)
        {
            InspectorCommandLineOptions.PrintUsage(output);
            return 0;
        }

        try
        {
            using var source = new FileStream(
                options.RecordingPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var recording = PoseRecordingJson.Read(source);
            return options.Command switch
            {
                InspectorCommand.Inspect => Inspect(recording, options, output),
                InspectorCommand.Replay => Replay(recording, options, output),
                _ => throw new InvalidOperationException("Unsupported inspector command."),
            };
        }
        catch (LagEstimationException exception)
        {
            error.WriteLine(
                $"Lag estimation rejected ({exception.Reason}): {exception.Message}");
            return 2;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            PoseRecordingFormatException or
            NotSupportedException or
            ArgumentException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            error.WriteLine($"Recording command failed: {exception.Message}");
            return 2;
        }
    }

    private static int Inspect(
        PoseRecording recording,
        InspectorCommandLineOptions options,
        TextWriter output)
    {
        output.Write(RecordingInspectionSummaryRenderer.Render(
            recording,
            new RecordingInspectionSummaryOptions(
                options.TrackerStreamId,
                options.ControllerStreamId,
                options.RevealDeviceIds)));
        return 0;
    }

    private static int Replay(
        PoseRecording recording,
        InspectorCommandLineOptions options,
        TextWriter output)
    {
        if (!RecordingInspectionSummaryRenderer.TrySelectPair(
                recording,
                options.TrackerStreamId,
                options.ControllerStreamId,
                out var tracker,
                out var controller,
                out var diagnostic))
        {
            throw new ArgumentException(diagnostic);
        }

        var replay = RecordingCalibrationReplay.Replay(
            recording,
            new RecordingReplayOptions(
                tracker!.Identity.StreamId,
                controller!.Identity.StreamId,
                options.Policy));

        output.WriteLine("Lighthouse Touch Bridge - Recording Replay");
        output.WriteLine($"format: {PoseRecording.FormatIdentifier}");
        output.WriteLine($"schema_version: {recording.SchemaVersion}");
        output.WriteLine($"tracker_stream: {tracker.Identity.StreamId}");
        output.WriteLine($"controller_stream: {controller.Identity.StreamId}");
        output.Write(RecordingInspectionSummaryRenderer.RenderLag(replay.Lag));
        output.WriteLine($"aligned_pairs: {replay.AlignedPairs.Count}");
        output.WriteLine();
        output.WriteLine(new CalibrationReport(replay.Calibration));
        return replay.Calibration.Success ? 0 : 2;
    }
}

internal sealed record InspectorCommandLineOptions(
    InspectorCommand Command,
    string? RecordingPath,
    string? TrackerStreamId,
    string? ControllerStreamId,
    CalibrationPolicy Policy,
    bool RevealDeviceIds,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> arguments,
        out InspectorCommandLineOptions options,
        out string? error)
    {
        options = Empty;
        error = null;
        if (arguments.Count == 1 && arguments[0] is "-h" or "--help")
        {
            options = Empty with { ShowHelp = true };
            return true;
        }

        if (arguments.Count == 0 || !TryParseCommand(arguments[0], out var command))
        {
            error = arguments.Count == 0
                ? "An inspect or replay command is required."
                : $"Unknown command '{arguments[0]}'.";
            return false;
        }

        if (arguments.Count == 2 && arguments[1] is "-h" or "--help")
        {
            options = Empty with { Command = command, ShowHelp = true };
            return true;
        }

        if (arguments.Count < 2 || arguments[1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"The {arguments[0]} command requires a recording path.";
            return false;
        }

        var recordingPath = arguments[1];
        string? trackerStreamId = null;
        string? controllerStreamId = null;
        var policy = CalibrationPolicy.Auto;
        var policySpecified = false;
        var revealDeviceIds = false;
        var showHelp = false;
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "-h" or "--help":
                    showHelp = true;
                    break;
                case "--tracker":
                    if (!TryReadValue(arguments, ref index, argument, out trackerStreamId, out error))
                    {
                        return false;
                    }

                    break;
                case "--controller":
                    if (!TryReadValue(arguments, ref index, argument, out controllerStreamId, out error))
                    {
                        return false;
                    }

                    break;
                case "--policy":
                    if (!TryReadValue(arguments, ref index, argument, out var policyValue, out error) ||
                        !TryParsePolicy(policyValue!, out policy))
                    {
                        error ??= $"Unknown policy '{policyValue}'.";
                        return false;
                    }

                    policySpecified = true;
                    break;
                case "--reveal-device-ids":
                    revealDeviceIds = true;
                    break;
                default:
                    error = $"Unknown option '{argument}'.";
                    return false;
            }
        }

        if (command == InspectorCommand.Inspect && policySpecified)
        {
            error = "--policy is only valid with replay.";
            return false;
        }

        options = new InspectorCommandLineOptions(
            command,
            recordingPath,
            trackerStreamId,
            controllerStreamId,
            policy,
            revealDeviceIds,
            showHelp);
        return true;
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Lighthouse Touch Bridge recording inspector and replay utility");
        writer.WriteLine();
        writer.WriteLine("Repository-root usage:");
        writer.WriteLine("  dotnet run --project tools/Ltb.RecordingInspector -- inspect <recording.json> [--tracker <stream-id>] [--controller <stream-id>] [--reveal-device-ids]");
        writer.WriteLine("  dotnet run --project tools/Ltb.RecordingInspector -- replay <recording.json> [--tracker <stream-id>] [--controller <stream-id>] [--policy auto|rotation|full]");
        writer.WriteLine();
        writer.WriteLine("Device IDs are redacted by default from inspect output.");
    }

    private static InspectorCommandLineOptions Empty { get; } = new(
        InspectorCommand.Inspect,
        null,
        null,
        null,
        CalibrationPolicy.Auto,
        false,
        false);

    private static bool TryParseCommand(string value, out InspectorCommand command)
    {
        command = value switch
        {
            "inspect" => InspectorCommand.Inspect,
            "replay" => InspectorCommand.Replay,
            _ => (InspectorCommand)(-1),
        };
        return Enum.IsDefined(command);
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
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Option '{option}' requires a non-empty value.";
            return false;
        }

        error = null;
        return true;
    }
}

internal enum InspectorCommand
{
    Inspect,
    Replay,
}
