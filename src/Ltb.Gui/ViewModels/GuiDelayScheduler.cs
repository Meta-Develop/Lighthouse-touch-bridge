namespace Ltb.Gui.ViewModels;

/// <summary>
/// Schedules presentation-only delayed work. The returned handle must cancel
/// callbacks that have not started yet.
/// </summary>
public interface IGuiDelayScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class SystemGuiDelayScheduler : IGuiDelayScheduler
{
    public static SystemGuiDelayScheduler Instance { get; } = new();

    private SystemGuiDelayScheduler()
    {
    }

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(callback);
        return new ScheduledAction(delay, callback);
    }

    private sealed class ScheduledAction : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _disposed;

        public ScheduledAction(TimeSpan delay, Action callback)
        {
            _ = RunAsync(delay, callback);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            _cancellation.Dispose();
        }

        private async Task RunAsync(TimeSpan delay, Action callback)
        {
            try
            {
                await Task.Delay(delay, _cancellation.Token).ConfigureAwait(false);
                if (!_cancellation.IsCancellationRequested)
                {
                    callback();
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                // Disposal intentionally cancels callbacks that have not started.
            }
        }
    }
}
