using Ltb.App;

namespace Ltb.Gui;

public interface ICalibrationWizardSessionFactory
{
    ICalibrationWizardSession CreateScripted(string profileStorePath, string? logPath);

    ICalibrationWizardSession CreateProduction(
        ProductionCalibrationWizardSessionOptions options);
}

public sealed class CalibrationWizardSessionFactory : ICalibrationWizardSessionFactory
{
    public ICalibrationWizardSession CreateScripted(
        string profileStorePath,
        string? logPath) =>
        new ScriptedCalibrationWizardSession(profileStorePath, logPath);

    public ICalibrationWizardSession CreateProduction(
        ProductionCalibrationWizardSessionOptions options) =>
        new ProductionCalibrationWizardSession(options);
}

/// <summary>
/// GUI adapter over the shared <c>Ltb.App</c> production composition. The app
/// factory owns native runtime construction, wizard/watchdog sequencing, and
/// SafeDisable; this adapter only projects the completed result to the common
/// GUI session port.
/// </summary>
public sealed class ProductionCalibrationWizardSession : ICalibrationWizardSession
{
    private readonly ProductionCalibrationWizardSessionOptions _options;

    public ProductionCalibrationWizardSession(
        ProductionCalibrationWizardSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.TryValidate(out var diagnostic))
        {
            throw new ArgumentException(diagnostic, nameof(options));
        }

        _options = options;
    }

    public async Task<CalibrationWizardResult> RunAsync(
        ICalibrationWizardOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var session = ProductionCalibrationWizardSessionFactory.Create(_options);
        var result = await session.RunAsync(output, cancellationToken).ConfigureAwait(false);
        return result.ToWizardResult();
    }
}
