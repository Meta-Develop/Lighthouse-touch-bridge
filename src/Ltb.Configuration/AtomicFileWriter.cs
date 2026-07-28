using System.Text;

namespace Ltb.Configuration;

internal static class AtomicFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static void Write(string path, string contents)
    {
        Write(path, contents, afterStaging: null);
    }

    /// <summary>
    /// Test seam at the exact post-stage/pre-rename boundary. Production
    /// callers use the two-argument overload and cannot inject behavior.
    /// </summary>
    internal static void Write(
        string path,
        string contents,
        Action<string>? afterStaging)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("File path must have a parent directory.", nameof(path));
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

            afterStaging?.Invoke(temporaryPath);
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
