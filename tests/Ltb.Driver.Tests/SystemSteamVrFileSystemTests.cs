using Ltb.Driver;

namespace Ltb.Driver.Tests;

public sealed class SystemSteamVrFileSystemTests
{
    [Fact]
    public async Task OwnershipCheckAndCommitExcludeConcurrentWriterAtRealFileBoundary()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ltb-system-file-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var path = Path.Combine(temporaryDirectory, "steamvr.vrsettings");
        await File.WriteAllTextAsync(path, "expected");
        var concurrentWriterWasBlocked = false;
        var fileSystem = new SystemSteamVrFileSystem(ownedPath =>
        {
            var failure = Record.Exception(() => File.WriteAllText(ownedPath, "concurrent"));
            concurrentWriterWasBlocked = failure is IOException;
        });

        try
        {
            var replaced = await fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                "expected",
                "replacement",
                CancellationToken.None);

            Assert.True(replaced);
            Assert.True(concurrentWriterWasBlocked);
            Assert.Equal("replacement", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
