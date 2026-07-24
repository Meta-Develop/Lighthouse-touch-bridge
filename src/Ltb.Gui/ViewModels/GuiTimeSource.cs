using System.Diagnostics;

namespace Ltb.Gui.ViewModels;

public interface IGuiTimeSource
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);
}

internal sealed class SystemGuiTimeSource : IGuiTimeSource
{
    public static SystemGuiTimeSource Instance { get; } = new();

    private SystemGuiTimeSource()
    {
    }

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);
}
