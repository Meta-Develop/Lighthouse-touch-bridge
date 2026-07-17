using System.Globalization;
using Ltb.Calibration;

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
            var result = HandEyeCalibrationSolver.Solve(dataset.AlignedPairs, commandLine.Policy);
            var report = CalibrationConsoleReport.Create(dataset, result);
            Console.Write(report.Text);
            return report.IsFailure ? 2 : 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Calibration could not run: {exception.Message}");
            return 2;
        }
    }
}
