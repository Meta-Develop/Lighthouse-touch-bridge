using System.Security.Cryptography;
using System.Text;

namespace Ltb.Integration.Tests;

public sealed class OpenVrNativeDeploymentTests
{
    private const string ExpectedSha256 =
        "bab8ac6ef64e68a9ca53315b0014d131088584b2efdfa6db511d67ec03cfcb4a";
    private const string ExpectedLicenseSha256 =
        "f56ff606104d4ef18e617921a75c73ad73b5a1a1d70c69590c29de16919e04ad";

    [Fact]
    public void PinnedWindowsX64LibraryIsCopiedBesideConsumerAssembly()
    {
        var nativeLibraryPath = Path.Combine(AppContext.BaseDirectory, "openvr_api.dll");

        Assert.True(
            File.Exists(nativeLibraryPath),
            $"Expected the pinned OpenVR native library at '{nativeLibraryPath}'.");

        using var stream = File.OpenRead(nativeLibraryPath);
        var actualSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        Assert.Equal(ExpectedSha256, actualSha256);
    }

    [Fact]
    public void ValveLicenseNoticeIsCopiedWithTheConsumer()
    {
        var licensePath = Path.Combine(
            AppContext.BaseDirectory,
            "licenses",
            "Valve.OpenVR.LICENSE.txt");

        Assert.True(
            File.Exists(licensePath),
            $"Expected the Valve OpenVR license notice at '{licensePath}'.");

        var actualSha256 = ComputeCanonicalTextSha256(File.ReadAllText(licensePath));

        Assert.Equal(ExpectedLicenseSha256, actualSha256);
    }

    [Theory]
    [InlineData("first\nsecond\n")]
    [InlineData("first\r\nsecond\r\n")]
    [InlineData("first\rsecond\r")]
    public void CanonicalTextHashNormalizesLineEndings(string text)
    {
        Assert.Equal(
            ComputeCanonicalTextSha256("first\nsecond\n"),
            ComputeCanonicalTextSha256(text));
    }

    private static string ComputeCanonicalTextSha256(string text)
    {
        var canonicalText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText));

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
