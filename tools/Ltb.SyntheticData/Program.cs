using System.Globalization;
using Ltb.Calibration;
using Ltb.Core;

namespace Ltb.SyntheticData;

internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        if (!CommandLineOptions.TryParse(args, out var commandLine, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            CommandLineOptions.PrintUsage(Console.Error);
            return 1;
        }

        if (commandLine.ShowHelp)
        {
            CommandLineOptions.PrintUsage(Console.Out);
            return 0;
        }

        try
        {
            var generationOptions = SyntheticGenerationOptions.ForScenario(
                commandLine.Scenario,
                commandLine.Seed);
            var dataset = SyntheticPoseGenerator.Generate(generationOptions);
            if (commandLine.Command == SyntheticCommand.RecordSimulate)
            {
                var recording = SyntheticRecordingFactory.Create(dataset);
                using var destination = new FileStream(
                    commandLine.OutputPath!,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                PoseRecordingJson.Write(recording, destination);
                Console.WriteLine("Lighthouse Touch Bridge - Synthetic Recording Export");
                Console.WriteLine($"format: {PoseRecording.FormatIdentifier}");
                Console.WriteLine($"schema_version: {recording.SchemaVersion}");
                Console.WriteLine($"scenario: {commandLine.Scenario}");
                Console.WriteLine($"seed: {commandLine.Seed}");
                Console.WriteLine($"known_controller_lag_ms: {dataset.KnownLagSeconds * 1_000d:F3}");
                Console.WriteLine($"tracker_samples: {dataset.RawTrackerSamples.Count}");
                Console.WriteLine($"controller_samples: {dataset.RawControllerSamples.Count}");
                Console.WriteLine($"output: {commandLine.OutputPath}");
                return 0;
            }

            var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, commandLine.Policy);
            var report = CalibrationConsoleReport.Create(dataset, result);
            Console.Write(report.Text);
            return report.IsFailure ? 2 : 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Synthetic data command failed: {exception.Message}");
            return 2;
        }
    }
}
