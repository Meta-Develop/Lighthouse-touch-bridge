namespace Ltb.Gui;

/// <summary>
/// Serializes the cancel-first desktop close handshake. Cleanup is attempted
/// once, placement persistence is best effort, and neither failure may trap
/// the process by preventing the final close dispatch.
/// </summary>
internal sealed class WindowCloseCoordinator
{
    private readonly object _sync = new();
    private readonly Func<Task> _cleanup;
    private readonly Func<Task> _savePlacement;
    private readonly Action _close;
    private Task? _closeOperation;
    private bool _allowFinalClose;

    internal WindowCloseCoordinator(
        Func<Task> cleanup,
        Func<Task> savePlacement,
        Action close)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _savePlacement = savePlacement ??
            throw new ArgumentNullException(nameof(savePlacement));
        _close = close ?? throw new ArgumentNullException(nameof(close));
    }

    internal bool AllowFinalClose
    {
        get
        {
            lock (_sync)
            {
                return _allowFinalClose;
            }
        }
    }

    internal Task RequestCloseAsync()
    {
        lock (_sync)
        {
            return _closeOperation ??= CompleteCloseAsync();
        }
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await _cleanup();
        }
        catch
        {
            // Cleanup already owns its fail-safe ordering. A terminal failure
            // is not retried because cleanup may have completed side effects.
        }

        try
        {
            await _savePlacement();
        }
        catch
        {
            // Local placement persistence must never prevent process exit.
        }

        lock (_sync)
        {
            _allowFinalClose = true;
        }

        _close();
    }
}
