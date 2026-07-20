using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ltb.Driver;

public sealed class SteamVrDriverLifecycle : ISteamVrDriverLifecycle
{
    public const string DriverManifestRelativePath = "driver.vrdrivermanifest";
    public const string DriverBinaryRelativePath = "bin/win64/driver_ltb.dll";
    public const string DriverBuildIdRelativePath = "build-id.txt";
    private const string SteamVrSectionName = "steamvr";
    private const string ActivateMultipleDriversName = "activateMultipleDrivers";
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerOptions.Default)
    {
        WriteIndented = true,
    };
    private static readonly Regex BuildIdPattern = new(
        @"\Adriver_ltb-[0-9]+\.[0-9]+\.[0-9]+-ipc-[0-9]+\.[0-9]+\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ISteamVrFileSystem _fileSystem;
    private readonly ISteamVrProcessRunner _processRunner;
    private readonly ISteamVrDriverReceiptStore _receiptStore;
    private readonly SteamVrPathDiscovery _pathDiscovery;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Dictionary<string, OwnedRegistration> _ownedRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SteamVrDriverLifecycle(
        ISteamVrHostEnvironment environment,
        ISteamVrFileSystem fileSystem,
        ISteamVrProcessRunner processRunner,
        ISteamVrDriverReceiptStore? receiptStore = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _receiptStore = receiptStore ?? NullSteamVrDriverReceiptStore.Instance;
        _pathDiscovery = new SteamVrPathDiscovery(environment, fileSystem);
    }

    public static SteamVrDriverLifecycle CreateDefault(
        ISteamVrDriverReceiptStore? receiptStore = null) =>
        new(
            new SystemSteamVrHostEnvironment(),
            new SystemSteamVrFileSystem(),
            new SystemSteamVrProcessRunner(),
            receiptStore);

    public ValueTask<SteamVrPaths> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pathDiscovery.DiscoverAsync(cancellationToken);
    }

    public async ValueTask<SteamVrDriverInspection> InspectAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDriverRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var stagedDriver = await ReadStagedDriverAsync(
                stagedDriverRoot,
                cancellationToken).ConfigureAwait(false);
            var openVr = await ReadOpenVrStateAsync(
                paths.OpenVrPathsFile,
                cancellationToken).ConfigureAwait(false);
            var settings = await ReadSettingsStateAsync(
                paths.SettingsFile,
                cancellationToken).ConfigureAwait(false);
            var exactRegistrationCount = openVr.ExternalDrivers.Count(
                path => PathsEqual(path, stagedDriver.CanonicalRoot));
            if (exactRegistrationCount > 1 || HasNonCanonicalEquivalent(
                    openVr.ExternalDrivers,
                    stagedDriver.CanonicalRoot))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                    "The OpenVR path registry contains a duplicate or non-canonical LTB driver path.");
            }

            return new SteamVrDriverInspection(
                paths,
                stagedDriver.CanonicalRoot,
                stagedDriver.BuildId,
                IsRegistered: exactRegistrationCount == 1,
                settings.ActivateMultipleDrivers);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SteamVrDriverLifecycleResult> RegisterAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDriverRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var stagedDriver = await ReadStagedDriverAsync(
                stagedDriverRoot,
                cancellationToken).ConfigureAwait(false);
            var canonicalDriverRoot = stagedDriver.CanonicalRoot;
            var originalOpenVr = await ReadOpenVrStateAsync(
                paths.OpenVrPathsFile,
                cancellationToken).ConfigureAwait(false);
            var originalSettings = await ReadSettingsStateAsync(
                paths.SettingsFile,
                cancellationToken).ConfigureAwait(false);
            var expectedDrivers = originalOpenVr.ExternalDrivers.ToList();
            var exactRegistrationCount = expectedDrivers.Count(
                path => PathsEqual(path, canonicalDriverRoot));
            if (exactRegistrationCount > 1 || HasNonCanonicalEquivalent(
                    expectedDrivers,
                    canonicalDriverRoot))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                    "The OpenVR path registry contains a duplicate or non-canonical LTB driver path.");
            }

            var driverChanged = exactRegistrationCount == 0;
            if (driverChanged)
            {
                expectedDrivers.Add(canonicalDriverRoot);
            }

            var settingChanged = originalSettings.ActivateMultipleDrivers !=
                SteamVrActivateMultipleDriversState.Enabled;
            var ownedSettingsText = originalSettings.Text;
            var settingsRollbackRequired = false;
            try
            {
                if (driverChanged)
                {
                    await RunVrPathRegAsync(
                        paths.VrPathRegExecutable,
                        "adddriver",
                        canonicalDriverRoot,
                        cancellationToken).ConfigureAwait(false);

                    var registered = await ReadOpenVrStateAsync(
                        paths.OpenVrPathsFile,
                        cancellationToken).ConfigureAwait(false);
                    ValidateExternalDrivers(registered.ExternalDrivers, expectedDrivers);
                }

                if (settingChanged)
                {
                    var replacement = SetActivateMultipleDrivers(
                        originalSettings.Text,
                        SteamVrActivateMultipleDriversState.Enabled,
                        originalSettings.SteamVrSectionWasPresent);
                    ownedSettingsText = replacement;
                    settingsRollbackRequired = true;
                    var replaced = await _fileSystem.TryReplaceTextAtomicallyAsync(
                        paths.SettingsFile,
                        originalSettings.Text,
                        replacement,
                        cancellationToken).ConfigureAwait(false);
                    if (!replaced)
                    {
                        settingsRollbackRequired = false;
                        throw Failure(
                            SteamVrDriverDiagnosticCode.ConcurrentModification,
                            "steamvr.vrsettings changed before activateMultipleDrivers could be enabled.");
                    }

                    var enabled = await ReadSettingsStateAsync(
                        paths.SettingsFile,
                        cancellationToken).ConfigureAwait(false);
                    ownedSettingsText = enabled.Text;
                    if (enabled.ActivateMultipleDrivers !=
                        SteamVrActivateMultipleDriversState.Enabled)
                    {
                        throw Failure(
                            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                            "activateMultipleDrivers was not true after the atomic settings update.");
                    }
                }

                var verifiedOpenVr = await ReadOpenVrStateAsync(
                    paths.OpenVrPathsFile,
                    cancellationToken).ConfigureAwait(false);
                ValidateExternalDrivers(verifiedOpenVr.ExternalDrivers, expectedDrivers);
                var verifiedSettings = await ReadSettingsStateAsync(
                    paths.SettingsFile,
                    cancellationToken).ConfigureAwait(false);
                if (verifiedSettings.ActivateMultipleDrivers !=
                    SteamVrActivateMultipleDriversState.Enabled)
                {
                    throw Failure(
                        SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                        "activateMultipleDrivers did not remain true through final verification.");
                }

                ownedSettingsText = verifiedSettings.Text;

                if (!_ownedRegistrations.TryGetValue(
                        canonicalDriverRoot,
                        out var ownedRegistration) ||
                    ownedRegistration.Removed)
                {
                    var receipt = (ownedRegistration is null
                            ? TryLoadPersistedReceipt(canonicalDriverRoot)
                            : null) ??
                        new SteamVrDriverRegistrationReceipt(
                            canonicalDriverRoot,
                            originalSettings.ActivateMultipleDrivers,
                            settingChanged,
                            originalSettings.SteamVrSectionWasPresent,
                            Guid.NewGuid());
                    ownedRegistration = new OwnedRegistration(receipt, Removed: false);
                    _ownedRegistrations[canonicalDriverRoot] = ownedRegistration;
                }

                _receiptStore.Save(ownedRegistration.Receipt);

                var changed = driverChanged || settingChanged;
                return new SteamVrDriverLifecycleResult(
                    changed,
                    RestartRequired: changed,
                    changed
                        ? SteamVrDriverReadiness.RestartRequired
                        : SteamVrDriverReadiness.RuntimeVerificationRequired,
                    changed
                        ? $"driver_ltb build '{stagedDriver.BuildId}' is registered; restart SteamVR, then verify the loaded build and two controllers."
                        : $"driver_ltb build '{stagedDriver.BuildId}' is already registered with activateMultipleDrivers enabled; verify the loaded build and two controllers.",
                    paths,
                    ownedRegistration.Receipt);
            }
            catch (Exception failure)
            {
                await ThrowAfterRollbackAsync(
                    failure,
                    paths,
                    canonicalDriverRoot,
                    driverChanged
                        ? ExternalDriverRollback.RemoveTarget
                        : ExternalDriverRollback.None,
                    originalSettings.Text,
                    ownedSettingsText,
                    settingsRollbackRequired,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<SteamVrDriverLifecycleResult> RemoveAsync(
        SteamVrDriverRegistrationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var paths = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var canonicalDriverRoot = _fileSystem.GetCanonicalPath(receipt.CanonicalDriverRoot);
            if (!PathsEqual(canonicalDriverRoot, receipt.CanonicalDriverRoot))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RemovalOwnershipLost,
                    "The registration receipt does not contain the canonical LTB driver path.");
            }

            if (!_ownedRegistrations.TryGetValue(
                    canonicalDriverRoot,
                    out var ownedRegistration) &&
                TryLoadPersistedReceipt(canonicalDriverRoot) is { } persistedReceipt)
            {
                ownedRegistration = new OwnedRegistration(persistedReceipt, Removed: false);
                _ownedRegistrations[canonicalDriverRoot] = ownedRegistration;
            }

            if (ownedRegistration is null || ownedRegistration.Receipt != receipt)
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RemovalOwnershipLost,
                    "The registration receipt was not issued by this lifecycle, is not in " +
                    "LTB's persisted registration ownership, or is stale.");
            }

            var originalOpenVr = await ReadOpenVrStateAsync(
                paths.OpenVrPathsFile,
                cancellationToken).ConfigureAwait(false);
            var originalSettings = await ReadSettingsStateAsync(
                paths.SettingsFile,
                cancellationToken).ConfigureAwait(false);
            var registrationCount = originalOpenVr.ExternalDrivers.Count(
                path => PathsEqual(path, canonicalDriverRoot));
            if (registrationCount > 1 || HasNonCanonicalEquivalent(
                    originalOpenVr.ExternalDrivers,
                    canonicalDriverRoot))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RemovalOwnershipLost,
                    "The LTB registration is duplicate or no longer represented by its exact canonical path.");
            }

            var settingNeedsRestore = false;
            if (receipt.ActivateMultipleDriversChanged)
            {
                settingNeedsRestore = originalSettings.ActivateMultipleDrivers ==
                    SteamVrActivateMultipleDriversState.Enabled;
                if (!settingNeedsRestore &&
                    originalSettings.ActivateMultipleDrivers != receipt.PriorActivateMultipleDrivers)
                {
                    throw Failure(
                        SteamVrDriverDiagnosticCode.RemovalOwnershipLost,
                        "activateMultipleDrivers changed after LTB registration; removal refused to overwrite it.");
                }
            }

            var driverChanged = registrationCount == 1;
            var expectedDrivers = originalOpenVr.ExternalDrivers
                .Where(path => !PathsEqual(path, canonicalDriverRoot))
                .ToArray();
            var ownedSettingsText = originalSettings.Text;
            var settingsRollbackRequired = false;
            try
            {
                if (driverChanged)
                {
                    await RunVrPathRegAsync(
                        paths.VrPathRegExecutable,
                        "removedriver",
                        canonicalDriverRoot,
                        cancellationToken).ConfigureAwait(false);

                    var removed = await ReadOpenVrStateAsync(
                        paths.OpenVrPathsFile,
                        cancellationToken).ConfigureAwait(false);
                    ValidateExternalDrivers(removed.ExternalDrivers, expectedDrivers);
                }

                if (settingNeedsRestore)
                {
                    var replacement = SetActivateMultipleDrivers(
                        originalSettings.Text,
                        receipt.PriorActivateMultipleDrivers,
                        receipt.SteamVrSectionWasPresent);
                    ownedSettingsText = replacement;
                    settingsRollbackRequired = true;
                    var replaced = await _fileSystem.TryReplaceTextAtomicallyAsync(
                        paths.SettingsFile,
                        originalSettings.Text,
                        replacement,
                        cancellationToken).ConfigureAwait(false);
                    if (!replaced)
                    {
                        settingsRollbackRequired = false;
                        throw Failure(
                            SteamVrDriverDiagnosticCode.ConcurrentModification,
                            "steamvr.vrsettings changed before the prior activateMultipleDrivers state could be restored.");
                    }

                    var restored = await ReadSettingsStateAsync(
                        paths.SettingsFile,
                        cancellationToken).ConfigureAwait(false);
                    ownedSettingsText = restored.Text;
                    if (restored.ActivateMultipleDrivers != receipt.PriorActivateMultipleDrivers)
                    {
                        throw Failure(
                            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                            "The prior activateMultipleDrivers presence/value was not restored.");
                    }
                }

                var verifiedOpenVr = await ReadOpenVrStateAsync(
                    paths.OpenVrPathsFile,
                    cancellationToken).ConfigureAwait(false);
                ValidateExternalDrivers(verifiedOpenVr.ExternalDrivers, expectedDrivers);
                var verifiedSettings = await ReadSettingsStateAsync(
                    paths.SettingsFile,
                    cancellationToken).ConfigureAwait(false);
                if (receipt.ActivateMultipleDriversChanged &&
                    verifiedSettings.ActivateMultipleDrivers != receipt.PriorActivateMultipleDrivers)
                {
                    throw Failure(
                        SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                        "The prior activateMultipleDrivers state did not remain restored.");
                }

                ownedSettingsText = verifiedSettings.Text;

                var changed = driverChanged || settingNeedsRestore;
                _receiptStore.Delete(canonicalDriverRoot);
                _ownedRegistrations[canonicalDriverRoot] = ownedRegistration with
                {
                    Removed = true,
                };
                return new SteamVrDriverLifecycleResult(
                    changed,
                    RestartRequired: changed,
                    changed
                        ? SteamVrDriverReadiness.RestartRequired
                        : SteamVrDriverReadiness.NotRegistered,
                    changed
                        ? "driver_ltb was removed without changing unrelated drivers; restart SteamVR."
                        : "driver_ltb is already removed and its owned settings state is restored.",
                    paths,
                    receipt);
            }
            catch (Exception failure)
            {
                await ThrowAfterRollbackAsync(
                    failure,
                    paths,
                    canonicalDriverRoot,
                    driverChanged
                        ? ExternalDriverRollback.AddTarget
                        : ExternalDriverRollback.None,
                    originalSettings.Text,
                    ownedSettingsText,
                    settingsRollbackRequired,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ownedRegistrations.Clear();
        _operationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask<StagedDriver> ReadStagedDriverAsync(
        string stagedDriverRoot,
        CancellationToken cancellationToken)
    {
        var canonicalDriverRoot = _fileSystem.GetCanonicalPath(stagedDriverRoot);
        var manifest = _fileSystem.GetCanonicalPath(
            Path.Combine(canonicalDriverRoot, DriverManifestRelativePath));
        if (!_fileSystem.FileExists(manifest))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.StagedManifestMissing,
                $"The staged driver root has no '{DriverManifestRelativePath}': '{canonicalDriverRoot}'.");
        }

        var binary = _fileSystem.GetCanonicalPath(
            Path.Combine(canonicalDriverRoot, DriverBinaryRelativePath));
        if (!_fileSystem.FileExists(binary))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.StagedBinaryMissing,
                $"The staged driver root has no '{DriverBinaryRelativePath}': '{canonicalDriverRoot}'.");
        }

        var buildIdFile = _fileSystem.GetCanonicalPath(
            Path.Combine(canonicalDriverRoot, DriverBuildIdRelativePath));
        if (!_fileSystem.FileExists(buildIdFile))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.StagedBuildIdMissing,
                $"The staged driver root has no '{DriverBuildIdRelativePath}': '{canonicalDriverRoot}'.");
        }

        string buildIdText;
        try
        {
            buildIdText = await _fileSystem.ReadAllTextAsync(
                buildIdFile,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException &&
            (exception is IOException or UnauthorizedAccessException))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.StagedBuildIdInvalid,
                $"The staged driver build identity could not be read: '{buildIdFile}'.",
                exception);
        }

        var buildId = RemoveSingleLineEnding(buildIdText);
        if (!BuildIdPattern.IsMatch(buildId))
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.StagedBuildIdInvalid,
                $"The staged driver build identity in '{buildIdFile}' is blank or malformed.");
        }

        return new StagedDriver(canonicalDriverRoot, buildId);
    }

    private static string RemoveSingleLineEnding(string text)
    {
        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return text[..^2];
        }

        return text.EndsWith('\n') ? text[..^1] : text;
    }

    private async ValueTask RunVrPathRegAsync(
        string executable,
        string verb,
        string canonicalDriverRoot,
        CancellationToken cancellationToken)
    {
        SteamVrProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                executable,
                [verb, canonicalDriverRoot],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.ProcessFailed,
                $"vrpathreg {verb} could not execute: {exception.Message}",
                exception);
        }

        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw Failure(
                SteamVrDriverDiagnosticCode.ProcessFailed,
                $"vrpathreg {verb} exited with code {result.ExitCode}: {detail}");
        }
    }

    private async ValueTask<OpenVrState> ReadOpenVrStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The root is not an object.");
            }

            if (!root.TryGetProperty("external_drivers", out var drivers))
            {
                return new OpenVrState(text, Array.Empty<string>());
            }

            if (drivers.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("external_drivers is not an array.");
            }

            var paths = new List<string>();
            foreach (var driver in drivers.EnumerateArray())
            {
                if (driver.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(driver.GetString()))
                {
                    throw new JsonException("external_drivers contains a non-path value.");
                }

                paths.Add(driver.GetString()!);
            }

            return new OpenVrState(text, paths);
        }
        catch (JsonException exception)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                $"OpenVR path registry '{path}' has invalid external_drivers state.",
                exception);
        }
    }

    private async ValueTask<SettingsState> ReadSettingsStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var text = await _fileSystem.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return ParseSettingsState(text, path);
    }

    private static SettingsState ParseSettingsState(string text, string path)
    {
        try
        {
            var node = JsonNode.Parse(
                text,
                nodeOptions: null,
                DocumentOptions) as JsonObject ?? throw new JsonException(
                    "The settings root is not an object.");
            if (!node.TryGetPropertyValue(SteamVrSectionName, out var steamVrNode))
            {
                return new SettingsState(
                    text,
                    SteamVrActivateMultipleDriversState.Absent,
                    SteamVrSectionWasPresent: false);
            }

            if (steamVrNode is not JsonObject steamVr)
            {
                throw new JsonException("steamvr is not an object.");
            }

            if (!steamVr.TryGetPropertyValue(ActivateMultipleDriversName, out var activeNode))
            {
                return new SettingsState(
                    text,
                    SteamVrActivateMultipleDriversState.Absent,
                    SteamVrSectionWasPresent: true);
            }

            if (activeNode is not JsonValue value || !value.TryGetValue<bool>(out var active))
            {
                throw new JsonException("activateMultipleDrivers is not a Boolean.");
            }

            return new SettingsState(
                text,
                active
                    ? SteamVrActivateMultipleDriversState.Enabled
                    : SteamVrActivateMultipleDriversState.Disabled,
                SteamVrSectionWasPresent: true);
        }
        catch (JsonException exception)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.SettingsInvalid,
                $"SteamVR settings '{path}' have an invalid steamvr.activateMultipleDrivers value.",
                exception);
        }
    }

    private static string SetActivateMultipleDrivers(
        string text,
        SteamVrActivateMultipleDriversState state,
        bool steamVrSectionWasPresent)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, DocumentOptions) as JsonObject ??
                throw new JsonException("The settings root is not an object.");
        }
        catch (JsonException exception)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.SettingsInvalid,
                "SteamVR settings could not be parsed for an owned update.",
                exception);
        }

        JsonObject steamVr;
        if (root.TryGetPropertyValue(SteamVrSectionName, out var steamVrNode))
        {
            steamVr = steamVrNode as JsonObject ?? throw Failure(
                SteamVrDriverDiagnosticCode.SettingsInvalid,
                "SteamVR settings property 'steamvr' must be an object.");
        }
        else
        {
            steamVr = [];
            root.Add(SteamVrSectionName, steamVr);
        }

        switch (state)
        {
            case SteamVrActivateMultipleDriversState.Enabled:
                steamVr[ActivateMultipleDriversName] = true;
                break;
            case SteamVrActivateMultipleDriversState.Disabled:
                steamVr[ActivateMultipleDriversName] = false;
                break;
            case SteamVrActivateMultipleDriversState.Absent:
                _ = steamVr.Remove(ActivateMultipleDriversName);
                if (!steamVrSectionWasPresent && steamVr.Count == 0)
                {
                    _ = root.Remove(SteamVrSectionName);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        return root.ToJsonString(SerializerOptions) + "\n";
    }

    private async ValueTask ThrowAfterRollbackAsync(
        Exception failure,
        SteamVrPaths paths,
        string canonicalDriverRoot,
        ExternalDriverRollback externalDriverRollback,
        string originalSettingsText,
        string ownedSettingsText,
        bool settingsRollbackRequired,
        CancellationToken cancellationToken)
    {
        var rollbackFailures = new List<string>();
        if (settingsRollbackRequired)
        {
            await RestoreSettingsMutationAsync(
                paths.SettingsFile,
                originalSettingsText,
                ownedSettingsText,
                rollbackFailures,
                cancellationToken).ConfigureAwait(false);
        }
        await RestoreExternalDriverMutationAsync(
            paths.OpenVrPathsFile,
            canonicalDriverRoot,
            externalDriverRollback,
            rollbackFailures,
            cancellationToken).ConfigureAwait(false);

        if (rollbackFailures.Count > 0)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.RollbackFailed,
                $"{failure.Message} Rollback was incomplete: {string.Join(" ", rollbackFailures)}",
                failure);
        }

        if (failure is SteamVrDriverLifecycleException lifecycleFailure)
        {
            throw lifecycleFailure;
        }

        if (failure is OperationCanceledException)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        throw Failure(
            SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
            failure.Message,
            failure);
    }

    private async ValueTask RestoreExternalDriverMutationAsync(
        string path,
        string canonicalDriverRoot,
        ExternalDriverRollback rollback,
        ICollection<string> rollbackFailures,
        CancellationToken cancellationToken)
    {
        if (rollback == ExternalDriverRollback.None)
        {
            return;
        }

        try
        {
            var current = await ReadOpenVrStateAsync(path, cancellationToken).ConfigureAwait(false);
            var unrelatedDrivers = current.ExternalDrivers
                .Where(driver => !IsCanonicalEquivalent(driver, canonicalDriverRoot))
                .ToArray();
            var shouldBePresent = rollback == ExternalDriverRollback.AddTarget;
            var targetIsPresent = current.ExternalDrivers.Count(
                driver => PathsEqual(driver, canonicalDriverRoot)) == 1 &&
                !HasNonCanonicalEquivalent(current.ExternalDrivers, canonicalDriverRoot);
            var targetIsAbsent = current.ExternalDrivers.All(
                driver => !IsCanonicalEquivalent(driver, canonicalDriverRoot));
            if ((shouldBePresent && targetIsPresent) || (!shouldBePresent && targetIsAbsent))
            {
                return;
            }

            var replacement = SetExternalDriverRegistration(
                current.Text,
                canonicalDriverRoot,
                shouldBePresent);
            var replaced = await _fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                current.Text,
                replacement,
                cancellationToken).ConfigureAwait(false);
            if (!replaced)
            {
                rollbackFailures.Add(
                    "openvrpaths.vrpath changed in the final compare/commit window.");
                return;
            }

            var restored = await ReadOpenVrStateAsync(path, cancellationToken).ConfigureAwait(false);
            var restoredUnrelatedDrivers = restored.ExternalDrivers
                .Where(driver => !IsCanonicalEquivalent(driver, canonicalDriverRoot))
                .ToArray();
            if (!restoredUnrelatedDrivers.SequenceEqual(
                    unrelatedDrivers,
                    StringComparer.OrdinalIgnoreCase))
            {
                rollbackFailures.Add(
                    "openvrpaths.vrpath rollback changed an unrelated external driver.");
                return;
            }

            var restoredTargetIsPresent = restored.ExternalDrivers.Count(
                driver => PathsEqual(driver, canonicalDriverRoot)) == 1 &&
                !HasNonCanonicalEquivalent(restored.ExternalDrivers, canonicalDriverRoot);
            var restoredTargetIsAbsent = restored.ExternalDrivers.All(
                driver => !IsCanonicalEquivalent(driver, canonicalDriverRoot));
            if ((shouldBePresent && !restoredTargetIsPresent) ||
                (!shouldBePresent && !restoredTargetIsAbsent))
            {
                rollbackFailures.Add(
                    "openvrpaths.vrpath did not restore only the canonical LTB registration.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            rollbackFailures.Add($"openvrpaths.vrpath rollback failed: {exception.Message}");
        }
    }

    private string SetExternalDriverRegistration(
        string text,
        string canonicalDriverRoot,
        bool shouldBePresent)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text, nodeOptions: null, DocumentOptions) as JsonObject ??
                throw new JsonException("The OpenVR path registry root is not an object.");
        }
        catch (JsonException exception)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                "OpenVR path registry could not be parsed for targeted rollback.",
                exception);
        }

        JsonArray drivers;
        if (root.TryGetPropertyValue("external_drivers", out var driversNode))
        {
            drivers = driversNode as JsonArray ?? throw Failure(
                SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                "OpenVR path registry property 'external_drivers' must be an array.");
        }
        else
        {
            if (!shouldBePresent)
            {
                return text;
            }

            drivers = [];
            root.Add("external_drivers", drivers);
        }

        for (var index = drivers.Count - 1; index >= 0; index--)
        {
            if (drivers[index] is not JsonValue value ||
                !value.TryGetValue<string>(out var driverPath) ||
                string.IsNullOrWhiteSpace(driverPath))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.OpenVrPathsInvalid,
                    "OpenVR path registry contains a non-path external driver.");
            }

            if (IsCanonicalEquivalent(driverPath, canonicalDriverRoot))
            {
                drivers.RemoveAt(index);
            }
        }

        if (shouldBePresent)
        {
            drivers.Add(canonicalDriverRoot);
        }

        return root.ToJsonString(SerializerOptions) + "\n";
    }

    private async ValueTask RestoreSettingsMutationAsync(
        string path,
        string originalText,
        string ownedText,
        ICollection<string> rollbackFailures,
        CancellationToken cancellationToken)
    {
        try
        {
            var original = ParseSettingsState(originalText, path);
            var owned = ParseSettingsState(ownedText, path);
            var current = await ReadSettingsStateAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Text, originalText, StringComparison.Ordinal))
            {
                return;
            }

            if (current.ActivateMultipleDrivers == original.ActivateMultipleDrivers)
            {
                return;
            }

            if (current.ActivateMultipleDrivers != owned.ActivateMultipleDrivers)
            {
                rollbackFailures.Add(
                    "steamvr.vrsettings activateMultipleDrivers no longer matched LTB's owned value.");
                return;
            }

            var replacement = SetActivateMultipleDrivers(
                current.Text,
                original.ActivateMultipleDrivers,
                original.SteamVrSectionWasPresent);
            var restored = await _fileSystem.TryReplaceTextAtomicallyAsync(
                path,
                current.Text,
                replacement,
                cancellationToken).ConfigureAwait(false);
            if (!restored)
            {
                rollbackFailures.Add(
                    "steamvr.vrsettings changed in the final compare/commit window.");
                return;
            }

            var verified = await ReadSettingsStateAsync(path, cancellationToken).ConfigureAwait(false);
            if (verified.ActivateMultipleDrivers != original.ActivateMultipleDrivers)
            {
                rollbackFailures.Add(
                    "steamvr.vrsettings did not restore the prior activateMultipleDrivers state.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            rollbackFailures.Add($"steamvr.vrsettings rollback failed: {exception.Message}");
        }
    }

    private bool HasNonCanonicalEquivalent(
        IEnumerable<string> registeredPaths,
        string canonicalDriverRoot)
    {
        foreach (var path in registeredPaths)
        {
            if (PathsEqual(path, canonicalDriverRoot))
            {
                continue;
            }

            try
            {
                if (PathsEqual(_fileSystem.GetCanonicalPath(path), canonicalDriverRoot))
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An unrelated malformed path is preserved. vrpathreg remains the authority for it.
            }
        }

        return false;
    }

    private bool IsCanonicalEquivalent(string path, string canonicalDriverRoot)
    {
        if (PathsEqual(path, canonicalDriverRoot))
        {
            return true;
        }

        try
        {
            return PathsEqual(_fileSystem.GetCanonicalPath(path), canonicalDriverRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ValidateExternalDrivers(
        IReadOnlyList<string> actual,
        IReadOnlyList<string> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw Failure(
                SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                "External-driver verification found an unexpected registration count.");
        }

        for (var index = 0; index < actual.Count; index++)
        {
            if (!PathsEqual(actual[index], expected[index]))
            {
                throw Failure(
                    SteamVrDriverDiagnosticCode.RegistrationVerificationFailed,
                    $"External-driver verification mismatch at index {index}: " +
                    $"expected '{expected[index]}', found '{actual[index]}'.");
            }
        }
    }

    /// <summary>
    /// Loads a durable LTB-issued receipt for the exact canonical driver root.
    /// A stored receipt whose recorded root is not that canonical root is
    /// ignored so a corrupted or foreign entry can never grant removal.
    /// </summary>
    private SteamVrDriverRegistrationReceipt? TryLoadPersistedReceipt(string canonicalDriverRoot)
    {
        var receipt = _receiptStore.TryLoad(canonicalDriverRoot);
        return receipt is not null &&
            PathsEqual(receipt.CanonicalDriverRoot, canonicalDriverRoot)
                ? receipt
                : null;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static SteamVrDriverLifecycleException Failure(
        SteamVrDriverDiagnosticCode code,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new SteamVrDriverLifecycleException(code, message)
            : new SteamVrDriverLifecycleException(code, message, innerException);

    private sealed record OpenVrState(string Text, IReadOnlyList<string> ExternalDrivers);

    private sealed record SettingsState(
        string Text,
        SteamVrActivateMultipleDriversState ActivateMultipleDrivers,
        bool SteamVrSectionWasPresent);

    private sealed record OwnedRegistration(
        SteamVrDriverRegistrationReceipt Receipt,
        bool Removed);

    private sealed record StagedDriver(string CanonicalRoot, string BuildId);

    private enum ExternalDriverRollback
    {
        None = 0,
        RemoveTarget,
        AddTarget,
    }
}
