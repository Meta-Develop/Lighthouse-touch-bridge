using Ltb.MetaLink.Interop;

namespace Ltb.MetaLink.Tests;

public sealed class OvrRuntimeDiscoveryTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Meta Horizon")]
    [InlineData(@"D:\Oculus")]
    public void UsesRegisteredBaseFromExactRegistry32Contract(string registeredBase)
    {
        var registry = new RecordingRegistry(registeredBase);
        var fileSystem = new RecordingFileSystem();
        var expected = $@"{registeredBase}\Support\oculus-runtime\LibOVRRT64_1.dll";
        fileSystem.ExistingFiles.Add(expected);
        var locator = Locator(registry, fileSystem);

        var located = locator.TryLocate(out var fullPath, out var failure);

        Assert.True(located);
        Assert.Null(failure);
        Assert.Equal(expected, fullPath);
        Assert.Equal(OvrRegistryHive.LocalMachine, registry.Hive);
        Assert.Equal(OvrRegistryView.Registry32, registry.View);
        Assert.Equal(@"Software\Oculus VR, LLC\Oculus", registry.SubKeyPath);
        Assert.Equal("Base", registry.ValueName);
        Assert.Equal([expected], fileSystem.FileExistQueries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative-meta-root")]
    public void MissingOrRelativeBaseIsNotInstalledWithoutFallback(string? registeredBase)
    {
        var registry = new RecordingRegistry(registeredBase);
        var fileSystem = new RecordingFileSystem();
        var locator = Locator(registry, fileSystem);

        var located = locator.TryLocate(out var fullPath, out var failure);

        Assert.False(located);
        Assert.Null(fullPath);
        Assert.NotNull(failure);
        Assert.Equal(MetaLinkReadiness.NotInstalled, failure.Readiness);
        Assert.Contains("repair", failure.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fileSystem.FileExistQueries);
    }

    [Fact]
    public void MissingRegisteredDllDoesNotProbeFallbackLocations()
    {
        const string registeredBase = @"D:\MetaRoot";
        var registry = new RecordingRegistry(registeredBase);
        var fileSystem = new RecordingFileSystem();
        var locator = Locator(registry, fileSystem);

        var located = locator.TryLocate(out var fullPath, out var failure);

        Assert.False(located);
        Assert.Null(fullPath);
        Assert.NotNull(failure);
        Assert.Equal(MetaLinkReadiness.NotInstalled, failure.Readiness);
        Assert.Equal(
            [@"D:\MetaRoot\Support\oculus-runtime\LibOVRRT64_1.dll"],
            fileSystem.FileExistQueries);
    }

    [Fact]
    public void UnsupportedPlatformDoesNotTouchRegistryOrFilesystem()
    {
        var registry = new RecordingRegistry(@"C:\Meta");
        var fileSystem = new RecordingFileSystem();
        var locator = new WindowsOvrRuntimeLocator(
            new FixedPlatform(false),
            registry,
            fileSystem);

        var located = locator.TryLocate(out _, out var failure);

        Assert.False(located);
        Assert.NotNull(failure);
        Assert.Equal(MetaLinkReadiness.AbiUnavailable, failure.Readiness);
        Assert.Equal(0, registry.ReadCount);
        Assert.Empty(fileSystem.FileExistQueries);
    }

    [Theory]
    [InlineData(NativeLoadFailure.DllNotFound)]
    [InlineData(NativeLoadFailure.BadImageFormat)]
    [InlineData(NativeLoadFailure.FileLoad)]
    [InlineData(NativeLoadFailure.MissingAbiExport)]
    public void FactoryClassifiesExpectedLoadAndAbiProbeFailuresAsAbiUnavailable(
        NativeLoadFailure nativeLoadFailure)
    {
        var registeredDll = CompleteRegisteredDllPath();
        var loader = new RecordingThrowingLoader(CreateFailure(nativeLoadFailure));
        var factory = new OvrNativeApiFactory(new FixedLocator(registeredDll), loader);

        var created = factory.TryCreate(out var api, out var failure);

        Assert.False(created);
        Assert.Null(api);
        Assert.NotNull(failure);
        Assert.Equal(MetaLinkReadiness.AbiUnavailable, failure.Readiness);
        Assert.Contains(
            nativeLoadFailure == NativeLoadFailure.MissingAbiExport
                ? "SDK 32.0.0 ABI"
                : "registered full path",
            failure.Diagnostic,
            StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(registeredDll));
        Assert.Equal([registeredDll], loader.Paths);
    }

    [Fact]
    public void FactoryClassifiesUnexpectedLoadFailureAsFaultedWithoutPathFallback()
    {
        var registeredDll = CompleteRegisteredDllPath();
        var loader = new RecordingThrowingLoader(
            new InvalidOperationException("synthetic unexpected loader failure"));
        var factory = new OvrNativeApiFactory(new FixedLocator(registeredDll), loader);

        var created = factory.TryCreate(out var api, out var failure);

        Assert.False(created);
        Assert.Null(api);
        Assert.NotNull(failure);
        Assert.Equal(MetaLinkReadiness.Faulted, failure.Readiness);
        Assert.Contains("unexpectedly", failure.Diagnostic, StringComparison.Ordinal);
        Assert.True(Path.IsPathFullyQualified(registeredDll));
        Assert.Equal([registeredDll], loader.Paths);
    }

    private static string CompleteRegisteredDllPath() => Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "registered-meta",
        "Support",
        "oculus-runtime",
        WindowsOvrRuntimeLocator.RuntimeDllName));

    private static Exception CreateFailure(NativeLoadFailure nativeLoadFailure) =>
        nativeLoadFailure switch
        {
            NativeLoadFailure.DllNotFound => new DllNotFoundException("synthetic load failure"),
            NativeLoadFailure.BadImageFormat => new BadImageFormatException("synthetic image failure"),
            NativeLoadFailure.FileLoad => new FileLoadException("synthetic file load failure"),
            NativeLoadFailure.MissingAbiExport => new EntryPointNotFoundException("synthetic ABI probe failure"),
            _ => throw new ArgumentOutOfRangeException(nameof(nativeLoadFailure)),
        };

    private static WindowsOvrRuntimeLocator Locator(
        RecordingRegistry registry,
        RecordingFileSystem fileSystem) =>
        new(new FixedPlatform(true), registry, fileSystem);

    private sealed record FixedPlatform(bool IsWindowsX64) : IOvrRuntimePlatform;

    private sealed class RecordingRegistry(string? value) : IOvrRuntimeRegistry
    {
        public int ReadCount { get; private set; }

        public OvrRegistryHive Hive { get; private set; }

        public OvrRegistryView View { get; private set; }

        public string? SubKeyPath { get; private set; }

        public string? ValueName { get; private set; }

        public string? ReadString(
            OvrRegistryHive hive,
            OvrRegistryView view,
            string subKeyPath,
            string valueName)
        {
            ReadCount++;
            Hive = hive;
            View = view;
            SubKeyPath = subKeyPath;
            ValueName = valueName;
            return value;
        }
    }

    private sealed class RecordingFileSystem : IOvrRuntimeFileSystem
    {
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> FileExistQueries { get; } = [];

        public bool IsPathFullyQualified(string path) =>
            path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';

        public string Combine(string basePath, params string[] relativeSegments) =>
            $"{basePath.TrimEnd('\\')}\\{string.Join('\\', relativeSegments)}";

        public string GetFullPath(string path) => path;

        public bool FileExists(string path)
        {
            FileExistQueries.Add(path);
            return ExistingFiles.Contains(path);
        }
    }

    private sealed class FixedLocator(string fullPath) : IOvrRuntimeLocator
    {
        public bool TryLocate(out string? locatedPath, out OvrRuntimeLoadFailure? failure)
        {
            locatedPath = fullPath;
            failure = null;
            return true;
        }
    }

    public enum NativeLoadFailure
    {
        DllNotFound,
        BadImageFormat,
        FileLoad,
        MissingAbiExport,
    }

    private sealed class RecordingThrowingLoader(Exception exception) : IOvrNativeApiLoader
    {
        public List<string> Paths { get; } = [];

        public IOvrNativeApi Load(string fullPath)
        {
            Paths.Add(fullPath);
            throw exception;
        }
    }
}
