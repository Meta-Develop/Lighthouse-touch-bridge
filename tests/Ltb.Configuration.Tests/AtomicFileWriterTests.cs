using Ltb.Configuration;

namespace Ltb.Configuration.Tests;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ltb-atomic-writer-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PostStageFailurePreservesOriginalBytesAndRemovesTemporaryResidue()
    {
        var path = Path.Combine(_root, "receipts.json");
        Directory.CreateDirectory(_root);
        var originalBytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF };
        File.WriteAllBytes(path, originalBytes);
        string? stagedPath = null;

        var failure = Assert.Throws<IOException>(() => AtomicFileWriter.Write(
            path,
            "replacement",
            temporaryPath =>
            {
                stagedPath = temporaryPath;
                Assert.True(File.Exists(temporaryPath));
                throw new IOException("Scripted post-stage failure.");
            }));

        Assert.Equal("Scripted post-stage failure.", failure.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
        Assert.NotNull(stagedPath);
        Assert.False(File.Exists(stagedPath));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
