using System.Text.Json;

namespace Ltb.Gui;

internal sealed record WindowPlacement(
    int X,
    int Y,
    double Width,
    double Height);

internal readonly record struct WindowWorkingArea(
    int X,
    int Y,
    int Width,
    int Height,
    double Scaling);

internal interface IWindowPlacementStore
{
    ValueTask<WindowPlacement?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        WindowPlacement placement,
        CancellationToken cancellationToken = default);
}

internal sealed class JsonWindowPlacementStore : IWindowPlacementStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    internal JsonWindowPlacementStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    internal static JsonWindowPlacementStore CreateDefault()
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return new JsonWindowPlacementStore(Path.Combine(
            localData,
            "LighthouseTouchBridge",
            "window-placement.json"));
    }

    public async ValueTask<WindowPlacement?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var placement = await JsonSerializer.DeserializeAsync<WindowPlacement>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return IsValid(placement) ? placement : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(
        WindowPlacement placement,
        CancellationToken cancellationToken = default)
    {
        if (!IsValid(placement))
        {
            throw new ArgumentException("Window placement must contain finite positive dimensions.");
        }

        var directory = Path.GetDirectoryName(_path) ??
            throw new InvalidOperationException("Window placement path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    placement,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A later save uses a unique temp file; stale cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Persistence failure must never prevent application shutdown.
            }
        }
    }

    internal static WindowPlacement Normalize(
        WindowPlacement placement,
        IReadOnlyList<WindowWorkingArea> workingAreas,
        double minimumWidth,
        double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(workingAreas);
        if (workingAreas.Count == 0)
        {
            return placement with
            {
                Width = Math.Max(minimumWidth, placement.Width),
                Height = Math.Max(minimumHeight, placement.Height),
            };
        }

        var area = workingAreas.FirstOrDefault(candidate =>
            placement.X >= candidate.X &&
            placement.X < candidate.X + candidate.Width &&
            placement.Y >= candidate.Y &&
            placement.Y < candidate.Y + candidate.Height);
        if (area.Width <= 0 || area.Height <= 0 || area.Scaling <= 0d)
        {
            area = workingAreas[0];
        }

        var maximumWidth = area.Width / area.Scaling;
        var maximumHeight = area.Height / area.Scaling;
        var width = Math.Min(
            Math.Max(minimumWidth, placement.Width),
            Math.Max(minimumWidth, maximumWidth));
        var height = Math.Min(
            Math.Max(minimumHeight, placement.Height),
            Math.Max(minimumHeight, maximumHeight));
        var widthPixels = Math.Min(
            area.Width,
            Math.Max(1, (int)Math.Ceiling(width * area.Scaling)));
        var heightPixels = Math.Min(
            area.Height,
            Math.Max(1, (int)Math.Ceiling(height * area.Scaling)));
        var x = Math.Clamp(placement.X, area.X, area.X + area.Width - widthPixels);
        var y = Math.Clamp(placement.Y, area.Y, area.Y + area.Height - heightPixels);
        return new WindowPlacement(x, y, width, height);
    }

    private static bool IsValid(WindowPlacement? placement) =>
        placement is not null &&
        double.IsFinite(placement.Width) &&
        placement.Width > 0d &&
        double.IsFinite(placement.Height) &&
        placement.Height > 0d;
}
