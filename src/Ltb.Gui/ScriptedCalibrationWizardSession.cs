using Ltb.App;
using Ltb.Core;

namespace Ltb.Gui;

/// <summary>
/// Composition port between the GUI shell and one wizard run. The GUI never
/// constructs runtimes or backends directly, so swapping the scripted demo
/// session for a production session changes no view or view-model code.
/// </summary>
public interface ICalibrationWizardSession
{
    Task<CalibrationWizardResult> RunAsync(
        ICalibrationWizardOutput output,
        CancellationToken cancellationToken);
}

/// <summary>
/// The same deterministic fake-backed composition the console
/// <c>wizard-demo</c> command uses: a scripted runtime, the file-backed
/// profile store, and the UI-neutral two-hand wizard state machine.
/// </summary>
public sealed class ScriptedCalibrationWizardSession : ICalibrationWizardSession
{
    private readonly string _profileStorePath;
    private readonly string? _logPath;

    public ScriptedCalibrationWizardSession(string profileStorePath, string? logPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileStorePath);
        if (logPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        }

        _profileStorePath = profileStorePath;
        _logPath = logPath;
    }

    public async Task<CalibrationWizardResult> RunAsync(
        ICalibrationWizardOutput output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var jsonLog = _logPath is null ? null : new JsonLinesLtbLogSink(_logPath);
        var runtime = new ScriptedCalibrationWizardRuntime(output);
        var backend = new FileCalibrationWizardBackend(_profileStorePath);
        var wizard = new TwoHandCalibrationWizard(runtime, backend, output, jsonLog);
        return await wizard.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
