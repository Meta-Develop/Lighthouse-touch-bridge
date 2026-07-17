using System.Text;

namespace Ltb.Configuration;

/// <summary>Loads and atomically saves calibration profile JSON files.</summary>
public static class CalibrationProfileFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static CalibrationProfile LoadProfile(string path) =>
        CalibrationProfileJson.DeserializeProfile(Read(path));

    public static void SaveProfile(string path, CalibrationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        WriteAtomically(path, CalibrationProfileJson.SerializeProfile(profile));
    }

    public static CalibrationProfileStore LoadStore(string path) =>
        CalibrationProfileJson.DeserializeStore(Read(path));

    public static void SaveStore(string path, CalibrationProfileStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        WriteAtomically(path, CalibrationProfileJson.SerializeStore(store));
    }

    private static string Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadAllText(path, Utf8WithoutBom);
    }

    private static void WriteAtomically(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Profile path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(fullPath);
        var temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
