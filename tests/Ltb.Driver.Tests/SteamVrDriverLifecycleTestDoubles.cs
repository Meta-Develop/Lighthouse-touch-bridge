using System.Text.Json;
using System.Text.Json.Nodes;
using Ltb.Driver;

namespace Ltb.Driver.Tests;

internal sealed class FakeSteamVrHostEnvironment : ISteamVrHostEnvironment
{
    public bool IsWindows { get; init; } = true;

    public string? LocalApplicationDataPath { get; init; }

    public string? GetLocalApplicationDataPath() => LocalApplicationDataPath;
}

internal sealed class MemorySteamVrFileSystem : ISteamVrFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readCounts = new(StringComparer.Ordinal);
    private int _replaceCount;

    public int? RefuseReplaceNumber { get; set; }

    public int? CancelReplaceNumber { get; set; }

    public string? ThrowReadPath { get; set; }

    public int? ThrowReadNumber { get; set; }

    public Action<MemorySteamVrFileSystem, string>? BeforeRefusedReplace { get; set; }

    public Action<MemorySteamVrFileSystem, string>? AfterSuccessfulReplace { get; set; }

    public Action<MemorySteamVrFileSystem, string>? BeforeConditionalCommit { get; set; }

    public bool FileExists(string path) => _files.ContainsKey(GetCanonicalPath(path));

    public string GetCanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public ValueTask<string> ReadAllTextAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = GetCanonicalPath(path);
        if (!_files.TryGetValue(canonicalPath, out var text))
        {
            throw new FileNotFoundException("Fake file does not exist.", canonicalPath);
        }

        var readCount = _readCounts.TryGetValue(canonicalPath, out var previousCount)
            ? previousCount + 1
            : 1;
        _readCounts[canonicalPath] = readCount;
        if (readCount == ThrowReadNumber &&
            string.Equals(canonicalPath, ThrowReadPath, StringComparison.Ordinal))
        {
            throw new IOException("Scripted transient read failure.");
        }

        return ValueTask.FromResult(text);
    }

    public ValueTask<bool> TryReplaceTextAtomicallyAsync(
        string path,
        string expectedText,
        string replacementText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var replaceNumber = Interlocked.Increment(ref _replaceCount);
        if (replaceNumber == CancelReplaceNumber)
        {
            throw new OperationCanceledException("Scripted cancellation after registration mutation.");
        }

        var canonicalPath = GetCanonicalPath(path);
        if (replaceNumber == RefuseReplaceNumber)
        {
            BeforeRefusedReplace?.Invoke(this, canonicalPath);
            return ValueTask.FromResult(false);
        }

        if (!_files.TryGetValue(canonicalPath, out var current) ||
            !string.Equals(current, expectedText, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }

        BeforeConditionalCommit?.Invoke(this, canonicalPath);
        if (!_files.TryGetValue(canonicalPath, out current) ||
            !string.Equals(current, expectedText, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(false);
        }

        _files[canonicalPath] = replacementText;
        AfterSuccessfulReplace?.Invoke(this, canonicalPath);
        return ValueTask.FromResult(true);
    }

    public void AddFile(string path, string text = "") =>
        _files.Add(GetCanonicalPath(path), text);

    public string Read(string path) => _files[GetCanonicalPath(path)];

    public void Write(string path, string text) => _files[GetCanonicalPath(path)] = text;
}

internal sealed class FakeVrPathRegRunner : ISteamVrProcessRunner
{
    private readonly MemorySteamVrFileSystem _fileSystem;
    private readonly string _openVrPathsFile;
    private int _callCount;

    public FakeVrPathRegRunner(
        MemorySteamVrFileSystem fileSystem,
        string openVrPathsFile)
    {
        _fileSystem = fileSystem;
        _openVrPathsFile = openVrPathsFile;
    }

    public int? FailCallNumber { get; set; }

    public bool MutateBeforeFailure { get; set; }

    public bool SkipMutation { get; set; }

    public string? AddedPathOverride { get; set; }

    public Action<MemorySteamVrFileSystem, string, string>? AfterMutation { get; set; }

    public List<(string Executable, string Verb, string DriverRoot)> Calls { get; } = [];

    public ValueTask<SteamVrProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(2, arguments.Count);
        var verb = arguments[0];
        var driverRoot = arguments[1];
        Calls.Add((executable, verb, driverRoot));
        var callNumber = Interlocked.Increment(ref _callCount);

        if (!SkipMutation && (callNumber != FailCallNumber || MutateBeforeFailure))
        {
            MutateDrivers(
                verb,
                verb == "adddriver" && AddedPathOverride is not null
                    ? AddedPathOverride
                    : driverRoot);
            AfterMutation?.Invoke(_fileSystem, verb, driverRoot);
        }

        return ValueTask.FromResult(callNumber == FailCallNumber
            ? new SteamVrProcessResult(7, string.Empty, "scripted vrpathreg failure")
            : new SteamVrProcessResult(0, "ok", string.Empty));
    }

    private void MutateDrivers(string verb, string driverRoot)
    {
        var root = JsonNode.Parse(_fileSystem.Read(_openVrPathsFile))!.AsObject();
        if (!root.TryGetPropertyValue("external_drivers", out var driversNode))
        {
            driversNode = new JsonArray();
            root.Add("external_drivers", driversNode);
        }

        var drivers = driversNode!.AsArray();
        if (verb == "adddriver")
        {
            if (!drivers.Any(node => string.Equals(
                    node!.GetValue<string>(),
                    driverRoot,
                    StringComparison.OrdinalIgnoreCase)))
            {
                drivers.Add(driverRoot);
            }
        }
        else if (verb == "removedriver")
        {
            for (var index = drivers.Count - 1; index >= 0; index--)
            {
                if (string.Equals(
                        drivers[index]!.GetValue<string>(),
                        driverRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    drivers.RemoveAt(index);
                }
            }
        }
        else
        {
            throw new InvalidOperationException($"Unexpected vrpathreg verb '{verb}'.");
        }

        _fileSystem.Write(
            _openVrPathsFile,
            root.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
            {
                WriteIndented = true,
            }) + "\n");
    }
}

internal sealed class SteamVrLifecycleFixture : IDisposable
{
    public const string BuildId = "driver_ltb-0.1.0-ipc-1.0";

    public SteamVrLifecycleFixture(
        SteamVrActivateMultipleDriversState setting =
            SteamVrActivateMultipleDriversState.Disabled,
        bool steamVrSectionPresent = true)
    {
        Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ltb-lifecycle-tests"));
        LocalApplicationData = Path.Combine(Root, "current-user", "AppData", "Local");
        RuntimeRoot = Path.Combine(Root, "steam", "runtime");
        ConfigRoot = Path.Combine(Root, "steam", "config");
        StagedDriverRoot = Path.Combine(Root, "package", "driver_ltb");
        OtherDriverRoot = Path.Combine(Root, "drivers", "unrelated");
        OpenVrPathsFile = Path.Combine(
            LocalApplicationData,
            "openvr",
            "openvrpaths.vrpath");
        SettingsFile = Path.Combine(ConfigRoot, "steamvr.vrsettings");
        VrPathRegExecutable = Path.Combine(RuntimeRoot, "bin", "win64", "vrpathreg.exe");
        ManifestFile = Path.Combine(StagedDriverRoot, "driver.vrdrivermanifest");
        BinaryFile = Path.Combine(StagedDriverRoot, "bin", "win64", "driver_ltb.dll");
        BuildIdFile = Path.Combine(StagedDriverRoot, "build-id.txt");

        FileSystem.AddFile(
            OpenVrPathsFile,
            OpenVrJson([OtherDriverRoot]));
        FileSystem.AddFile(VrPathRegExecutable);
        FileSystem.AddFile(SettingsFile, SettingsJson(setting, steamVrSectionPresent));
        FileSystem.AddFile(ManifestFile, "{}");
        FileSystem.AddFile(BinaryFile, "driver bytes");
        FileSystem.AddFile(BuildIdFile, BuildId + "\n");
        ProcessRunner = new FakeVrPathRegRunner(FileSystem, OpenVrPathsFile);
        Lifecycle = new SteamVrDriverLifecycle(
            new FakeSteamVrHostEnvironment
            {
                LocalApplicationDataPath = LocalApplicationData,
            },
            FileSystem,
            ProcessRunner);
    }

    public string Root { get; }

    public string LocalApplicationData { get; }

    public string RuntimeRoot { get; }

    public string ConfigRoot { get; }

    public string StagedDriverRoot { get; }

    public string OtherDriverRoot { get; }

    public string OpenVrPathsFile { get; }

    public string SettingsFile { get; }

    public string VrPathRegExecutable { get; }

    public string ManifestFile { get; }

    public string BinaryFile { get; }

    public string BuildIdFile { get; }

    public MemorySteamVrFileSystem FileSystem { get; } = new();

    public FakeVrPathRegRunner ProcessRunner { get; }

    public SteamVrDriverLifecycle Lifecycle { get; }

    public void Dispose()
    {
        Lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    public IReadOnlyList<string> ExternalDrivers()
    {
        using var document = JsonDocument.Parse(FileSystem.Read(OpenVrPathsFile));
        return document.RootElement
            .GetProperty("external_drivers")
            .EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
    }

    public SteamVrActivateMultipleDriversState ActivateMultipleDrivers()
    {
        var root = JsonNode.Parse(FileSystem.Read(SettingsFile))!.AsObject();
        if (!root.TryGetPropertyValue("steamvr", out var steamVrNode) ||
            steamVrNode is not JsonObject steamVr ||
            !steamVr.TryGetPropertyValue("activateMultipleDrivers", out var activeNode))
        {
            return SteamVrActivateMultipleDriversState.Absent;
        }

        return activeNode!.GetValue<bool>()
            ? SteamVrActivateMultipleDriversState.Enabled
            : SteamVrActivateMultipleDriversState.Disabled;
    }

    public string OpenVrJson(IReadOnlyList<string> externalDrivers)
    {
        var drivers = new JsonArray(
            externalDrivers.Select(path => JsonValue.Create(path)).ToArray());
        var root = new JsonObject
        {
            ["config"] = new JsonArray(ConfigRoot),
            ["external_drivers"] = drivers,
            ["jsonid"] = "vrpathreg",
            ["log"] = new JsonArray(Path.Combine(Root, "steam", "logs")),
            ["runtime"] = new JsonArray(RuntimeRoot),
            ["version"] = 1,
        };
        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
        }) + "\n";
    }

    private static string SettingsJson(
        SteamVrActivateMultipleDriversState setting,
        bool steamVrSectionPresent)
    {
        var root = new JsonObject
        {
            ["dashboard"] = new JsonObject { ["enableDashboard"] = true },
        };
        if (steamVrSectionPresent)
        {
            root["steamvr"] = new JsonObject { ["allowAsyncReprojection"] = true };
        }

        if (setting != SteamVrActivateMultipleDriversState.Absent)
        {
            var steamVr = root["steamvr"] as JsonObject ?? [];
            root["steamvr"] = steamVr;
            steamVr["activateMultipleDrivers"] =
                setting == SteamVrActivateMultipleDriversState.Enabled;
        }

        return root.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            WriteIndented = true,
        }) + "\n";
    }
}
