using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ltb.Calibration;
using Ltb.Configuration;
using Ltb.Core;
using Ltb.Driver;
using Ltb.MetaLink;
using Ltb.OpenVr;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>Zero-input production composition for the first-party internal-driver path.</summary>
public static class InternalDriverSessionFactory
{
    public static IInternalDriverSession Create(InternalDriverSessionOptions? options = null)
    {
        options ??= new InternalDriverSessionOptions();
        options.Validate();
        var paths = ResolvePaths(options);
        var runtime = new ProductionInternalDriverSessionRuntime(options, paths);
        var output = new JsonLinesInternalDriverSessionOutput(paths.StructuredLogPath);
        return new InternalDriverSession(runtime, options, output);
    }

    internal static InternalDriverResolvedPaths ResolvePaths(InternalDriverSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var requestedLocalRoot = options.LocalApplicationDataRoot ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(requestedLocalRoot))
        {
            throw new InvalidOperationException(
                "The current user's LocalApplicationData directory is unavailable.");
        }

        var localRoot = CanonicalDirectory(requestedLocalRoot);

        var applicationRoot = CanonicalDirectory(Path.Combine(localRoot, "LighthouseTouchBridge"));
        return new InternalDriverResolvedPaths(
            CanonicalFile(options.SettingsPath ??
                Path.Combine(applicationRoot, "settings", "internal-driver.json")),
            CanonicalFile(options.CalibrationProfileStorePath ??
                Path.Combine(applicationRoot, "profiles", "calibration-profiles.json")),
            CanonicalDirectory(options.StagedDriverRoot ??
                Path.Combine(AppContext.BaseDirectory, "driver_ltb")),
            CanonicalFile(options.StructuredLogPath ??
                Path.Combine(applicationRoot, "logs", "internal-driver.jsonl")),
            CanonicalFile(
                Path.Combine(applicationRoot, "driver", "registration-receipts.json")),
            CanonicalFile(options.TrackerPathObservationStorePath ??
                Path.Combine(
                    applicationRoot,
                    "settings",
                    "tracker-path-observations.json")));
    }

    private static string CanonicalDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        return full == Path.GetPathRoot(full) ? full : Path.TrimEndingDirectorySeparator(full);
    }

    private static string CanonicalFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }
}

internal sealed record InternalDriverResolvedPaths(
    string SettingsPath,
    string CalibrationProfileStorePath,
    string StagedDriverRoot,
    string StructuredLogPath,
    string DriverReceiptStorePath,
    string? TrackerPathObservationStorePath = null)
{
    public string EffectiveTrackerPathObservationStorePath =>
        TrackerPathObservationStorePath ??
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(SettingsPath)
                    ?? throw new InvalidOperationException(
                        "The internal-driver settings path must have a parent directory."),
                "tracker-path-observations.json"));
}

internal sealed class ProductionInternalDriverSessionRuntime :
    IInternalDriverSessionRuntime,
    IInternalDriverTrackerNeutralizationRuntime,
    IInternalDriverTrackerPathObservationRuntime
{
    private const string ControllerModel = "Quest 2 Touch";
    private static readonly TimeSpan CaptureProgressInterval = TimeSpan.FromMilliseconds(250);
    private readonly InternalDriverSessionOptions _options;
    private readonly InternalDriverResolvedPaths _paths;
    private readonly ISteamVrDriverLifecycle _driverLifecycle;
    private readonly InternalDriverTrackerBatchSampler _trackerBatchSampler;
    private readonly IInternalDriverTrackerNeutralizationBackend _trackerNeutralizationBackend;
    private MetaLinkRuntime? _meta;
    private OpenVrSession? _openVr;
    private InternalDriverCaptureEvidence? _leftCaptureEvidence;
    private InternalDriverCaptureEvidence? _rightCaptureEvidence;
    private bool _disposed;

    public ProductionInternalDriverSessionRuntime(
        InternalDriverSessionOptions options,
        InternalDriverResolvedPaths paths)
        : this(options, paths, CreateDefaultLifecycle(paths))
    {
    }

    internal ProductionInternalDriverSessionRuntime(
        InternalDriverSessionOptions options,
        InternalDriverResolvedPaths paths,
        ISteamVrDriverLifecycle driverLifecycle)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _driverLifecycle = driverLifecycle ?? throw new ArgumentNullException(nameof(driverLifecycle));
        _trackerNeutralizationBackend = new SteamVrSettingsTrackerNeutralizationBackend(
            _driverLifecycle,
            Path.Combine(
                Path.GetDirectoryName(_paths.DriverReceiptStorePath)
                    ?? throw new ArgumentException(
                        "The driver receipt store must have a parent directory.",
                        nameof(paths)),
                "tracker-role-recovery.json"));
        _trackerBatchSampler = new InternalDriverTrackerBatchSampler(
            devices => _openVr!.CreateTrackedPoseBatchSource(
                devices,
                OpenVrTrackingUniverse.RawAndUncalibrated,
                predictionOffsetSeconds: 0d));
    }

    public IInternalDriverTrackerNeutralizationBackend TrackerNeutralizationBackend =>
        _trackerNeutralizationBackend;

    private static SteamVrDriverLifecycle CreateDefaultLifecycle(
        InternalDriverResolvedPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return SteamVrDriverLifecycle.CreateDefault(
            new ConfigurationSteamVrDriverReceiptStore(paths.DriverReceiptStorePath));
    }

    public InternalDriverPlatformProbe Probe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            return new InternalDriverPlatformProbe(
                false,
                "The first-party internal driver requires a Windows x64 process.",
                "Run the win-x64 LTB application on the SteamVR host.");
        }

        var settingsPreparation = EnsureDefaultSettings(_paths);
        return new InternalDriverPlatformProbe(
            true,
            settingsPreparation.Diagnostic,
            "No remediation is required.");
    }

    public async ValueTask<InternalDriverRegistration> EnsureDriverAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _driverLifecycle.RegisterAsync(
            _paths.StagedDriverRoot,
            cancellationToken).ConfigureAwait(false);
        var verified = await _driverLifecycle.InspectAsync(
            _paths.StagedDriverRoot,
            cancellationToken).ConfigureAwait(false);
        return new InternalDriverRegistration(
            IsRegistered: true,
            result.Changed,
            result.RestartRequired,
            verified.StagedBuildId,
            result.Diagnostic);
    }

    public InternalDriverRuntimeObservation Observe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _meta ??= new MetaLinkRuntime();
        var meta = _meta.Poll();
        try
        {
            _openVr ??= OpenVrSession.Open();
            var health = _openVr.GetRuntimeHealth();
            if (!health.IsRunning)
            {
                _trackerBatchSampler.Reset();
                _openVr.Dispose();
                _openVr = null;
                return new InternalDriverRuntimeObservation(
                    SteamVrRunning: false,
                    health.Diagnostic,
                    meta,
                    [],
                    new Dictionary<string, PoseSourceSample>(
                        StringComparer.OrdinalIgnoreCase));
            }

            var devices = _openVr.EnumerateDevices();
            var trackers = ReadTrackerSamples(devices);
            return new InternalDriverRuntimeObservation(
                SteamVrRunning: true,
                health.Diagnostic,
                meta,
                devices,
                trackers);
        }
        catch (OpenVrUnavailableException exception)
        {
            _trackerBatchSampler.Reset();
            _openVr?.Dispose();
            _openVr = null;
            return new InternalDriverRuntimeObservation(
                SteamVrRunning: false,
                exception.Message,
                meta,
                [],
                new Dictionary<string, PoseSourceSample>(
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    public async ValueTask<InternalDriverProfilePair> ResolveProfilesAsync(
        InternalDriverRuntimeObservation observation,
        InternalDriverProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(progress);
        var serials = SnapshotConnectedTrackerSerials(observation);
        if (serials.Length < 2)
        {
            throw new InvalidOperationException(
                "Profile resolution requires at least two distinct connected physical tracker serials.");
        }

        _leftCaptureEvidence = null;
        _rightCaptureEvidence = null;

        var calibration = new InternalDriverCalibration(_paths.CalibrationProfileStorePath);
        _ = EnsureDefaultSettings(_paths);
        var authorityGenerationBefore =
            InternalDriverSettingsFile.ComputeGeneration(
                _paths.SettingsPath);
        var configuredSettings = InternalDriverSettingsFile.Load(_paths.SettingsPath);
        var authorityGenerationAfter =
            InternalDriverSettingsFile.ComputeGeneration(
                _paths.SettingsPath);
        if (!string.Equals(
                authorityGenerationBefore,
                authorityGenerationAfter,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Authoritative pre-session settings changed while the manual binding was " +
                "loaded. Refresh before beginning motion capture.");
        }

        var configuredBinding = configuredSettings.ManualTrackerBinding;
        var manualBinding = configuredBinding is null
            ? null
            : new ManualTrackerBinding(
                configuredBinding.LeftTrackerSerial,
                configuredBinding.RightTrackerSerial);
        var manualBindingAuthorityGeneration = manualBinding is null
            ? null
            : authorityGenerationAfter;
        var requestedHands = _options.RequestedCalibrationHands;
        var reusable = FindReusablePair(
            calibration,
            serials,
            explicitRequest: false,
            manualBinding);
        if (requestedHands == InternalDriverCalibrationHandSet.None && reusable is not null)
        {
            return reusable;
        }

        if (requestedHands is InternalDriverCalibrationHandSet.Left or
            InternalDriverCalibrationHandSet.Right)
        {
            return await CalibrateSelectedHandAsync(
                ResolveSelectedHandBase(
                    calibration,
                    reusable,
                    requestedHands,
                    serials),
                requestedHands,
                serials,
                progress,
                manualBinding,
                manualBindingAuthorityGeneration,
                cancellationToken).ConfigureAwait(false);
        }

        var explicitRequest = requestedHands == InternalDriverCalibrationHandSet.Both;
        var leftCapture = await CaptureHandAsync(
            MetaLinkHand.Left,
            serials,
            progress,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var rightCapture = await CaptureHandAsync(
            MetaLinkHand.Right,
            serials,
            progress,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress(
            InternalDriverSessionState.Association,
            manualBinding is null
                ? $"Selecting a unique left/right pair from {serials.Length} raw tracker " +
                  "candidates using separate per-hand angular-speed captures."
                : $"Verifying the authoritative manual pair against motion correlation " +
                  $"without allowing correlation to reassign either hand.",
            manualBinding is null
                ? "Keep both tracker mounts unchanged; unrelated trackers may remain connected, " +
                  "but move only the requested mounted controller."
                : "A mismatch will be reported as an explicit correction choice; the manual " +
                  "pair remains authoritative unless the owner accepts that correction.");
        var verification = TrackerHandAssociator.VerifyManualBinding(
            ToAssociationCapture(leftCapture),
            ToAssociationCapture(rightCapture),
            manualBinding);
        if (manualBinding is null && !verification.AutomaticAssociationAccepted)
        {
            throw new InvalidOperationException(
                $"First-party tracker association failed: {verification.Reason}");
        }

        var selectedLeftSerial = manualBinding?.LeftTrackerSerial ??
            verification.CorrelationResult!.Left!.TrackerSerial;
        var selectedRightSerial = manualBinding?.RightTrackerSerial ??
            verification.CorrelationResult!.Right!.TrackerSerial;
        if (string.IsNullOrWhiteSpace(selectedLeftSerial) ||
            string.IsNullOrWhiteSpace(selectedRightSerial) ||
            string.Equals(
                selectedLeftSerial,
                selectedRightSerial,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Profile resolution did not produce one complete distinct left/right tracker pair.");
        }

        var verificationEvidence = manualBinding is null
            ? null
            : ToManualBindingVerificationEvidence(
                verification,
                manualBindingAuthorityGeneration);
        progress(
            InternalDriverSessionState.TimeAlignment,
            "Estimating per-hand residual lag after preserving Meta clock uncertainty evidence.",
            "No user action is required.");
        progress(
            InternalDriverSessionState.RotationSolve,
            "Solving the tracker-to-controller rotation from the associated captures.",
            "No user action is required.");
        progress(
            InternalDriverSessionState.TranslationAttempt,
            "Attempting translation only when Meta position and motion make it observable.",
            "No position or poor observability will retain a valid rotation-only result.");
        progress(
            InternalDriverSessionState.Validation,
            "Running held-out model selection and per-hand calibration quality gates.",
            "Bad rotation will fail; unavailable or unobservable position may select rotation-only.");
        var profiles = RunTwoHandProfileStoreTransaction(
            _paths.CalibrationProfileStorePath,
            stagedProfileStorePath => CalibratePair(
                new InternalDriverCalibration(stagedProfileStorePath),
                leftCapture,
                rightCapture,
                selectedLeftSerial,
                selectedRightSerial,
                explicitRequest,
                cancellationToken),
            cancellationToken);
        progress(
            InternalDriverSessionState.SaveProfile,
            "Both first-party results passed validation; exact schema-3 profiles were saved and reloaded.",
            "Keep the physical tracker mounts fixed for profile reuse.");
        return profiles with
        {
            ManualBindingVerification = verificationEvidence,
        };
    }

    private async ValueTask<InternalDriverProfilePair> CalibrateSelectedHandAsync(
        SelectedHandCalibrationBase calibrationBase,
        InternalDriverCalibrationHandSet requestedHand,
        IReadOnlyList<string> trackerSerials,
        InternalDriverProgress progress,
        ManualTrackerBinding? manualBinding,
        string? manualBindingAuthorityGeneration,
        CancellationToken cancellationToken)
    {
        var metaHand = requestedHand == InternalDriverCalibrationHandSet.Left
            ? MetaLinkHand.Left
            : MetaLinkHand.Right;
        var preserved = calibrationBase.PreservedOpposite;
        var expectedPreservedSerial = requestedHand == InternalDriverCalibrationHandSet.Left
            ? manualBinding?.RightTrackerSerial
            : manualBinding?.LeftTrackerSerial;
        if (expectedPreservedSerial is not null &&
            !string.Equals(
                preserved.TrackerSerial,
                expectedPreservedSerial,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Selected-hand calibration retained opposite tracker " +
                $"'{preserved.TrackerSerial}', but the authoritative manual binding requires " +
                $"'{expectedPreservedSerial}'. No capture or profile write began.");
        }

        var capture = await CaptureHandAsync(
            metaHand,
            trackerSerials,
            progress,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress(
            InternalDriverSessionState.Association,
            $"Scoring every viable tracker candidate for the requested " +
            $"{metaHand.ToString().ToLowerInvariant()} hand while retaining " +
            $"'{preserved.TrackerSerial}' for the unselected hand.",
            "Move only the requested mounted controller; the retained other-hand tracker remains an ambiguity contender.");
        var association = InternalDriverSingleHandAssociator.Associate(
            ToAssociationCapture(capture),
            preserved.TrackerSerial);
        if (!association.Success)
        {
            if (manualBinding is null)
            {
                throw new InvalidOperationException(
                    $"Selected-hand tracker association failed ({association.Status}): " +
                    association.Reason);
            }
        }

        var selectedTrackerSerial = requestedHand == InternalDriverCalibrationHandSet.Left
            ? manualBinding?.LeftTrackerSerial
            : manualBinding?.RightTrackerSerial;
        selectedTrackerSerial ??= association.Assignment!.TrackerSerial;
        if (!trackerSerials.Contains(
                selectedTrackerSerial,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The authoritative manual tracker '{selectedTrackerSerial}' is absent from " +
                "the current selected-hand candidate roster.");
        }

        InternalDriverManualBindingVerificationEvidence? verificationEvidence = null;
        if (manualBinding is not null)
        {
            var suggestedSelected = association.Assignment?.TrackerSerial;
            var correctionLeft = requestedHand == InternalDriverCalibrationHandSet.Left
                ? suggestedSelected
                : preserved.TrackerSerial;
            var correctionRight = requestedHand == InternalDriverCalibrationHandSet.Right
                ? suggestedSelected
                : preserved.TrackerSerial;
            var agrees = association.Success &&
                string.Equals(
                    selectedTrackerSerial,
                    suggestedSelected,
                    StringComparison.OrdinalIgnoreCase);
            verificationEvidence = new InternalDriverManualBindingVerificationEvidence(
                !association.Success
                    ? InternalDriverManualBindingVerificationState.CorrelationFailed
                    : agrees
                        ? InternalDriverManualBindingVerificationState.Agreement
                        : InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
                manualBinding.LeftTrackerSerial!,
                manualBinding.RightTrackerSerial!,
                !association.Success
                    ? $"Manual binding remains authoritative because selected-hand " +
                      $"correlation could not verify it: {association.Reason}"
                    : agrees
                        ? "Selected-hand motion correlation agrees with the authoritative " +
                          "manual binding."
                        : $"Manual binding remains authoritative; selected-hand motion " +
                          $"correlation suggests left {correctionLeft} and right " +
                          $"{correctionRight} as an explicit correction candidate.",
                agrees || !association.Success ? null : correctionLeft,
                agrees || !association.Success ? null : correctionRight,
                manualBindingAuthorityGeneration);
        }

        progress(
            InternalDriverSessionState.TimeAlignment,
            $"Estimating residual lag for only the requested {metaHand} hand.",
            "The retained opposite-hand profile and lag evidence are unchanged.");
        progress(
            InternalDriverSessionState.RotationSolve,
            $"Solving the requested {metaHand} tracker-to-controller rotation.",
            "No user action is required.");
        progress(
            InternalDriverSessionState.TranslationAttempt,
            $"Attempting requested-{metaHand} translation only when observable.",
            "The retained opposite-hand mount is unchanged.");
        progress(
            InternalDriverSessionState.Validation,
            $"Validating the requested {metaHand} result before one selected-hand commit.",
            "A failure or cancellation leaves canonical profile bytes unchanged.");

        var result = RunSelectedHandProfileStoreTransaction(
            _paths.CalibrationProfileStorePath,
            metaHand,
            stagedProfileStorePath =>
            {
                var selected = Calibrate(
                    new InternalDriverCalibration(stagedProfileStorePath),
                    capture,
                    metaHand,
                    selectedTrackerSerial,
                    explicitRequest: true,
                    replacedTrackerSerial: calibrationBase.ReplacedTrackerSerial);
                return metaHand == MetaLinkHand.Left
                    ? new InternalDriverProfilePair(selected, preserved)
                    : new InternalDriverProfilePair(preserved, selected);
            },
            cancellationToken: cancellationToken);
        progress(
            InternalDriverSessionState.SaveProfile,
            $"The requested {metaHand} profile was atomically replaced; the opposite hand " +
            "and unrelated profile records were preserved.",
            "Keep both physical tracker mounts fixed for profile reuse.");
        return result with
        {
            ManualBindingVerification = verificationEvidence,
        };
    }

    internal SelectedHandCalibrationBase ResolveSelectedHandBase(
        InternalDriverCalibration calibration,
        InternalDriverProfilePair? reusablePair,
        InternalDriverCalibrationHandSet requestedHand,
        IReadOnlyList<string> observedTrackerSerials)
    {
        var selectedMetaHand = requestedHand == InternalDriverCalibrationHandSet.Left
            ? MetaLinkHand.Left
            : MetaLinkHand.Right;
        if (reusablePair is not null)
        {
            return selectedMetaHand == MetaLinkHand.Left
                ? new SelectedHandCalibrationBase(
                    reusablePair.Right,
                    reusablePair.Left.TrackerSerial)
                : new SelectedHandCalibrationBase(
                    reusablePair.Left,
                    reusablePair.Right.TrackerSerial);
        }

        var oppositeMetaHand = selectedMetaHand == MetaLinkHand.Left
            ? MetaLinkHand.Right
            : MetaLinkHand.Left;
        var preserved = FindSingleReusableHand(
            calibration,
            observedTrackerSerials,
            oppositeMetaHand);
        var configuredPrevious = selectedMetaHand == MetaLinkHand.Left
            ? _options.PreviousLeftTrackerSerial
            : _options.PreviousRightTrackerSerial;
        var candidates = calibration.FindCandidateTrackerSerials(selectedMetaHand);
        if (configuredPrevious is not null &&
            !candidates.Contains(configuredPrevious, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Selected-{selectedMetaHand.ToString().ToLowerInvariant()} calibration " +
                $"was asked to replace prior tracker '{configuredPrevious}', but no profile " +
                "with that exact selected-hand key exists. The opposite reusable profile " +
                "was preserved and no capture or canonical write began.");
        }

        return new SelectedHandCalibrationBase(preserved, configuredPrevious);
    }

    private static InternalDriverHandProfile FindSingleReusableHand(
        InternalDriverCalibration calibration,
        IReadOnlyList<string> serials,
        MetaLinkHand hand)
    {
        var matches = serials
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(serial => calibration.FindReusableProfile(
                new InternalDriverCalibrationContext(hand, serial, ControllerModel)))
            .Where(lookup => lookup.CanReuse)
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Selected-hand calibration requires exactly one reusable " +
                $"{hand.ToString().ToLowerInvariant()} opposite-hand profile among the " +
                $"current tracker roster; observed {matches.Length}. No capture or canonical write began.");
        }

        var match = matches[0];
        return ToHandProfile(
            hand == MetaLinkHand.Left ? ProtocolHand.Left : ProtocolHand.Right,
            match.Profile!,
            InternalDriverProfileReadiness.Reused,
            match.Diagnostic);
    }

    internal static string[] SnapshotConnectedTrackerSerials(
        InternalDriverRuntimeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.TrackerSamples
            .Where(pair => pair.Value.IsConnected)
            .Select(pair => InternalDriverTrackerSerial.Require(
                pair.Key,
                nameof(observation.TrackerSamples)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyDictionary<string, PoseSourceSample>
        CanonicalizeTrackerSamples(
            IReadOnlyDictionary<string, PoseSourceSample> trackerSamples)
    {
        ArgumentNullException.ThrowIfNull(trackerSamples);
        var canonical = new Dictionary<string, PoseSourceSample>(
            trackerSamples.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in trackerSamples)
        {
            var serial = InternalDriverTrackerSerial.Require(
                pair.Key,
                nameof(trackerSamples));
            if (!canonical.TryAdd(serial, pair.Value))
            {
                throw new InvalidDataException(
                    $"Tracker samples repeated physical serial '{serial}' using " +
                    "case-variant keys.");
            }
        }

        return canonical;
    }

    public IDriverFeed CreateFeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DriverFeed(new NamedPipeDriverTransportFactory());
    }

    public void RecordSelectedTrackerPaths(
        IReadOnlyList<InternalDriverTrackerPath> trackerPaths,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(trackerPaths);
        InternalDriverTrackerNeutralizationLifecycle.ValidateExactPair(trackerPaths);
        var candidates = trackerPaths
            .Select(path => new TrackerPathObservationCandidate(
                path.TrackerSerial,
                path.DevicePath,
                observedAtUtc))
            .ToArray();
        _ = new TrackerPathObservationStore(
                _paths.EffectiveTrackerPathObservationStorePath)
            .RecordObservations(candidates);
    }

    public InternalDriverProfilePair SaveMountAdjustments(
        InternalDriverProfilePair profiles,
        MountAdjustment left,
        MountAdjustment right)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (!profiles.IsValid ||
            profiles.Left.SourceProfile is not { } expectedLeft ||
            profiles.Right.SourceProfile is not { } expectedRight)
        {
            throw new InvalidOperationException(
                "Mount adjustments require an exact source profile pair.");
        }

        var store = CalibrationProfileFile.LoadStore(_paths.CalibrationProfileStorePath);
        var currentLeft = RequireUnchangedSource(store, expectedLeft);
        var currentRight = RequireUnchangedSource(store, expectedRight);
        var updatedLeft = WithMountAdjustment(currentLeft, left);
        var updatedRight = WithMountAdjustment(currentRight, right);
        var updatedStore = InternalDriverCalibration.ReplaceSelectedProfile(
            store,
            updatedLeft,
            currentLeft.TrackerSerial);
        updatedStore = InternalDriverCalibration.ReplaceSelectedProfile(
            updatedStore,
            updatedRight,
            currentRight.TrackerSerial);
        // SaveStore's atomic replace is the persistence commit boundary. All
        // serialization/source-CAS validation is complete before this call;
        // do not add a post-commit reload that could report failure after the
        // canonical bytes have already changed.
        CalibrationProfileFile.SaveStore(_paths.CalibrationProfileStorePath, updatedStore);
        return new InternalDriverProfilePair(
            ToHandProfile(
                ProtocolHand.Left,
                updatedLeft,
                profiles.Left.Readiness,
                "Saved explicit left-hand mount adjustments."),
            ToHandProfile(
                ProtocolHand.Right,
                updatedRight,
                profiles.Right.Readiness,
                "Saved explicit right-hand mount adjustments."));
    }

    private static CalibrationProfile RequireUnchangedSource(
        CalibrationProfileStore store,
        CalibrationProfile expected)
    {
        var current = store.FindCandidateProfile(expected.TrackerSerial, expected.Hand)
            ?? throw new InvalidDataException(
                $"The {expected.Hand} source profile disappeared before adjustment Save.");
        if (!string.Equals(
                CalibrationProfileJson.SerializeProfile(current),
                CalibrationProfileJson.SerializeProfile(expected),
                StringComparison.Ordinal))
        {
            throw new IOException(
                $"The {expected.Hand} source profile changed after it was loaded; " +
                "adjustment Save was refused.");
        }

        return current;
    }

    private static CalibrationProfile WithMountAdjustment(
        CalibrationProfile source,
        MountAdjustment adjustment) => new(
        CalibrationProfileSchema.CurrentVersion,
        source.ProfileName,
        source.Hand,
        source.ControllerRuntime,
        source.ControllerModel,
        source.ControllerIdentity,
        InternalDriverTrackerSerial.Require(
            source.TrackerSerial,
            nameof(source.TrackerSerial)),
        source.DriverProfile
            ?? throw new InvalidDataException(
                "First-party adjustment persistence requires a driver profile."),
        source.CalibrationPolicy,
        source.SelectedMode,
        source.SelectionReason,
        source.TrackerToController,
        adjustment,
        source.EstimatedLagMilliseconds,
        source.Quality,
        source.CreatedUtc);

    public void ResetMeta()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _meta?.Reset();
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));

    public ulong GetMonotonicNanoseconds()
    {
        var ticks = Stopwatch.GetTimestamp();
        var seconds = ticks / (double)Stopwatch.Frequency;
        return Math.Max(1UL, checked((ulong)Math.Round(seconds * 1_000_000_000d)));
    }

    public ValueTask StopRunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _trackerBatchSampler.Reset();
        _openVr?.Dispose();
        _openVr = null;
        _meta?.Dispose();
        _meta = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopRunAsync(CancellationToken.None).ConfigureAwait(false);
        if (_trackerNeutralizationBackend is IDisposable disposableNeutralization)
        {
            disposableNeutralization.Dispose();
        }
        _driverLifecycle.Dispose();
        _disposed = true;
    }

    private IReadOnlyDictionary<string, PoseSourceSample> ReadTrackerSamples(
        IReadOnlyList<SteamVrDeviceDescriptor> devices)
    {
        var candidates = devices
            .Where(device =>
                device.Category == SteamVrDeviceCategory.GenericTracker &&
                device.Capabilities.HasPosition &&
                device.Capabilities.IsPhysicalPoseSourceEligible &&
                !device.Capabilities.IsVirtualPoseSource)
            .ToArray();
        return _trackerBatchSampler.Read(candidates);
    }

    private static InternalDriverProfilePair? FindReusablePair(
        InternalDriverCalibration calibration,
        IReadOnlyList<string> serials,
        bool explicitRequest,
        ManualTrackerBinding? manualBinding = null)
    {
        var leftProfiles = new List<InternalDriverProfileLookup>();
        var rightProfiles = new List<InternalDriverProfileLookup>();
        foreach (var serial in serials.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var left = calibration.FindReusableProfile(new InternalDriverCalibrationContext(
                MetaLinkHand.Left,
                serial,
                ControllerModel)
            {
                ExplicitRequest = explicitRequest,
            });
            var right = calibration.FindReusableProfile(new InternalDriverCalibrationContext(
                MetaLinkHand.Right,
                serial,
                ControllerModel)
            {
                ExplicitRequest = explicitRequest,
            });

            if (left.CanReuse)
            {
                leftProfiles.Add(left);
            }

            if (right.CanReuse)
            {
                rightProfiles.Add(right);
            }
        }

        var candidates = (
            from left in leftProfiles
            from right in rightProfiles
            where !string.Equals(
                left.Context.TrackerSerial,
                right.Context.TrackerSerial,
                StringComparison.OrdinalIgnoreCase)
            where manualBinding is null ||
                string.Equals(
                    left.Context.TrackerSerial,
                    manualBinding.LeftTrackerSerial,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    right.Context.TrackerSerial,
                    manualBinding.RightTrackerSerial,
                    StringComparison.OrdinalIgnoreCase)
            select new InternalDriverProfilePair(
                ToHandProfile(
                    ProtocolHand.Left,
                    left.Profile!,
                    InternalDriverProfileReadiness.Reused,
                    left.Diagnostic),
                ToHandProfile(
                    ProtocolHand.Right,
                    right.Profile!,
                    InternalDriverProfileReadiness.Reused,
                    right.Diagnostic)))
            .Take(2)
            .ToArray();

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                "Multiple reusable left/right controller-source profile pairs matched the observed trackers.");
        }

        return candidates.SingleOrDefault();
    }

    private async ValueTask<GuidedHandCapture> CaptureHandAsync(
        MetaLinkHand hand,
        IReadOnlyList<string> trackerSerials,
        InternalDriverProgress progress,
        CancellationToken cancellationToken)
    {
        var canonicalTrackerSerials =
            RequireDistinctCanonicalTrackerSerials(trackerSerials);
        var mappedMetaSamples = new InternalDriverMappedMetaSampleFilter(hand);
        var captureEvidence = new InternalDriverCaptureEvidenceTracker(hand);
        var trackerSamples = canonicalTrackerSerials.ToDictionary(
            serial => serial,
            _ => new List<PoseSourceSample>(),
            StringComparer.OrdinalIgnoreCase);
        var lastTrackerTimes = canonicalTrackerSerials.ToDictionary(
            serial => serial,
            _ => double.NegativeInfinity,
            StringComparer.OrdinalIgnoreCase);
        var continuouslyConnected = canonicalTrackerSerials.ToDictionary(
            serial => serial,
            _ => true,
            StringComparer.OrdinalIgnoreCase);
        var startedNanoseconds = GetMonotonicNanoseconds();
        var durationNanoseconds = ToNanoseconds(_options.GuidedCaptureDurationPerHand);
        var reportCadence = new InternalDriverCaptureReportCadence(
            CaptureProgressInterval,
            startedNanoseconds);
        var scheduler = new InternalDriverMonotonicDeadlineScheduler(
            _options.PollInterval,
            startedNanoseconds);
        InternalDriverRuntimeObservation? latestObservation = null;
        UpdateCaptureEvidence(hand, InternalDriverCaptureEvidence.Empty);
        ReportCaptureEvidence(hand, progress, observation: null);
        while (ElapsedNanoseconds(GetMonotonicNanoseconds(), startedNanoseconds) < durationNanoseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = Observe();
            latestObservation = observation;
            if (!observation.SteamVrRunning)
            {
                throw new InvalidOperationException(observation.SteamVrDiagnostic);
            }

            var meta = observation.Meta.ForHand(hand);
            var observedTrackerSamples =
                CanonicalizeTrackerSamples(observation.TrackerSamples);
            _ = captureEvidence.TryAppend(observation.Meta);

            if (meta.Readiness == MetaLinkReadiness.Ready && meta.Controller is { } controller)
            {
                _ = mappedMetaSamples.TryAppend(controller);
            }

            foreach (var serial in canonicalTrackerSerials)
            {
                if (observedTrackerSamples.TryGetValue(serial, out var sample))
                {
                    continuouslyConnected[serial] &= sample.IsConnected;
                    if (sample.MonotonicHostTimeSeconds > lastTrackerTimes[serial])
                    {
                        trackerSamples[serial].Add(sample);
                        lastTrackerTimes[serial] = sample.MonotonicHostTimeSeconds;
                    }
                }
                else
                {
                    continuouslyConnected[serial] = false;
                }
            }

            if (reportCadence.ShouldReport(GetMonotonicNanoseconds()))
            {
                UpdateCaptureEvidence(hand, captureEvidence.Evaluate());
                ReportCaptureEvidence(hand, progress, latestObservation);
            }

            await scheduler.WaitForNextAsync(
                GetMonotonicNanoseconds,
                DelayAsync,
                cancellationToken).ConfigureAwait(false);
        }

        var finalEvidence = captureEvidence.Evaluate();
        UpdateCaptureEvidence(hand, finalEvidence);
        ReportCaptureEvidence(hand, progress, latestObservation);

        EnsureRotationReady(hand, finalEvidence);

        if (mappedMetaSamples.Samples.Count == 0)
        {
            throw new InvalidOperationException(
                $"The {hand} guided capture did not retain a valid Meta controller stream.");
        }

        return new GuidedHandCapture(
            hand,
            mappedMetaSamples.Samples.ToArray(),
            trackerSamples,
            continuouslyConnected);
    }

    private static string[] RequireDistinctCanonicalTrackerSerials(
        IReadOnlyList<string> trackerSerials)
    {
        ArgumentNullException.ThrowIfNull(trackerSerials);
        var canonical = trackerSerials
            .Select(serial => InternalDriverTrackerSerial.Require(
                serial,
                nameof(trackerSerials)))
            .ToArray();
        var duplicate = canonical
            .GroupBy(serial => serial, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Guided capture received duplicate physical tracker serial " +
                $"'{duplicate}' through case-variant candidates.");
        }

        return canonical;
    }

    private static ulong ToNanoseconds(TimeSpan duration)
    {
        var nanoseconds = duration.TotalMilliseconds * 1_000_000d;
        return nanoseconds >= ulong.MaxValue
            ? ulong.MaxValue
            : checked((ulong)Math.Ceiling(nanoseconds));
    }

    private static ulong ElapsedNanoseconds(ulong now, ulong started) =>
        now >= started ? now - started : 0UL;

    internal static void EnsureRotationReady(
        MetaLinkHand hand,
        InternalDriverCaptureEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.RotationReady)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The {hand} guided capture lacks rotation coverage: " +
            $"samples={evidence.SampleCount}, " +
            $"valid_orientation={evidence.OrientationValidityFraction:P0}, " +
            $"axis_coverage={evidence.MotionAxisCoverage:F3}, " +
            $"total_rotation={evidence.TotalRotationDegrees:F1} deg. " +
            "Repeat capture with continuous pitch, yaw, and roll while keeping Meta tracking visible.");
    }

    private void UpdateCaptureEvidence(
        MetaLinkHand hand,
        InternalDriverCaptureEvidence evidence)
    {
        if (hand == MetaLinkHand.Left)
        {
            _leftCaptureEvidence = evidence;
        }
        else if (hand == MetaLinkHand.Right)
        {
            _rightCaptureEvidence = evidence;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }
    }

    private void ReportCaptureEvidence(
        MetaLinkHand hand,
        InternalDriverProgress progress,
        InternalDriverRuntimeObservation? observation) => progress(
        InternalDriverSessionState.Recording,
        $"Capturing strictly monotonic real Meta pose samples for the " +
        $"{hand.ToString().ToLowerInvariant()} hand.",
        "Move only the requested mounted controller through pitch, yaw, roll, and moderate translation.",
        _leftCaptureEvidence,
        _rightCaptureEvidence,
        observation);

    internal static HandMotionCapture ToAssociationCapture(GuidedHandCapture capture)
    {
        var controllerSamples = capture.MetaSamples.Select(ToPoseSample).ToArray();
        var candidates = capture.TrackerSamples.Select(pair =>
            new TrackerAssociationCandidate(
                pair.Key,
                pair.Value.Select(ToTrackerPoseSample).ToArray(),
                capture.ContinuouslyConnected.TryGetValue(pair.Key, out var connected) &&
                connected)).ToArray();
        return new HandMotionCapture(
            MetaLinkCalibrationCapture.ToCalibrationHand(capture.Hand),
            controllerSamples,
            candidates);
    }

    private static TimestampedPoseSample ToPoseSample(MetaLinkControllerSnapshot sample)
    {
        var validity = PoseValidity.None;
        if (sample.Pose.HasValidOrientation && sample.Pose.IsOrientationTracked)
        {
            validity |= PoseValidity.Orientation | PoseValidity.TrackingValid;
        }

        if (sample.Pose.HasValidPosition && sample.Pose.IsPositionTracked)
        {
            validity |= PoseValidity.Position;
        }

        return new TimestampedPoseSample(
            sample.Pose.AppMonotonicTimeSeconds,
            sample.Pose.TrackingOriginFromController,
            validity);
    }

    private static TimestampedPoseSample ToTrackerPoseSample(PoseSourceSample sample)
    {
        var validity = sample.Validity;
        if (!sample.IsConnected)
        {
            validity &= ~PoseValidity.TrackingValid;
        }

        return new TimestampedPoseSample(
            sample.MonotonicHostTimeSeconds,
            sample.Pose,
            validity);
    }

    private static InternalDriverHandProfile Calibrate(
        InternalDriverCalibration calibration,
        GuidedHandCapture capture,
        MetaLinkHand hand,
        string trackerSerial,
        bool explicitRequest,
        string? replacedTrackerSerial = null)
    {
        var canonicalTrackerSerial = InternalDriverTrackerSerial.Require(
            trackerSerial,
            nameof(trackerSerial));
        var context = new InternalDriverCalibrationContext(
            hand,
            canonicalTrackerSerial,
            ControllerModel)
        {
            ExplicitRequest = explicitRequest,
        };
        var matchingTrackerSamples = capture.TrackerSamples
            .Where(pair => string.Equals(
                pair.Key,
                canonicalTrackerSerial,
                StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matchingTrackerSamples.Length != 1)
        {
            throw new InvalidDataException(
                $"Guided capture requires exactly one sample stream for tracker " +
                $"'{canonicalTrackerSerial}', but observed {matchingTrackerSamples.Length}.");
        }

        var retained = new MetaLinkCalibrationCapture(
            hand,
            canonicalTrackerSerial,
            capture.MetaSamples,
            matchingTrackerSamples[0].Value);
        var result = calibration.CalibrateAndSave(
            context,
            retained,
            replacedTrackerSerial: replacedTrackerSerial);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Diagnostic);
        }

        return ToHandProfile(
            hand == MetaLinkHand.Left ? ProtocolHand.Left : ProtocolHand.Right,
            result.Profile!,
            InternalDriverProfileReadiness.Calibrated,
            result.Diagnostic);
    }

    private static InternalDriverProfilePair CalibratePair(
        InternalDriverCalibration calibration,
        GuidedHandCapture leftCapture,
        GuidedHandCapture rightCapture,
        string leftTrackerSerial,
        string rightTrackerSerial,
        bool explicitRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var left = Calibrate(
            calibration,
            leftCapture,
            MetaLinkHand.Left,
            leftTrackerSerial,
            explicitRequest);
        cancellationToken.ThrowIfCancellationRequested();
        var right = Calibrate(
            calibration,
            rightCapture,
            MetaLinkHand.Right,
            rightTrackerSerial,
            explicitRequest);
        cancellationToken.ThrowIfCancellationRequested();
        return new InternalDriverProfilePair(left, right);
    }

    internal static InternalDriverManualBindingVerificationEvidence
        ToManualBindingVerificationEvidence(
            ManualTrackerBindingVerificationResult verification,
            string? authorityGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var authoritative = verification.AuthoritativeBinding ??
            throw new ArgumentException(
                "Manual verification evidence requires an authoritative binding.",
                nameof(verification));
        return new InternalDriverManualBindingVerificationEvidence(
            verification.Status switch
            {
                ManualTrackerBindingVerificationStatus.Agreement =>
                    InternalDriverManualBindingVerificationState.Agreement,
                ManualTrackerBindingVerificationStatus.MismatchCorrectionCandidate =>
                    InternalDriverManualBindingVerificationState.MismatchCorrectionCandidate,
                ManualTrackerBindingVerificationStatus.CorrelationFailed =>
                    InternalDriverManualBindingVerificationState.CorrelationFailed,
                _ => throw new ArgumentException(
                    $"Manual verification status '{verification.Status}' cannot be " +
                    "presented as an accepted manual binding.",
                    nameof(verification)),
            },
            authoritative.LeftTrackerSerial!,
            authoritative.RightTrackerSerial!,
            verification.Reason,
            verification.CorrectionCandidate?.LeftTrackerSerial,
            verification.CorrectionCandidate?.RightTrackerSerial,
            authorityGeneration);
    }

    /// <summary>
    /// Stages a fresh two-hand calibration against a private copy and commits
    /// the complete resulting store only after both hands validate.
    /// </summary>
    internal static TResult RunTwoHandProfileStoreTransaction<TResult>(
        string profileStorePath,
        Func<string, TResult> stageCalibration,
        CancellationToken cancellationToken = default)
        => RunProfileStoreTransaction(
            profileStorePath,
            "two-hand-calibration",
            stageCalibration,
            cancellationToken);

    internal static TResult RunSelectedHandProfileStoreTransaction<TResult>(
        string profileStorePath,
        MetaLinkHand hand,
        Func<string, TResult> stageCalibration,
        Action? commitStarted = null,
        CancellationToken cancellationToken = default)
    {
        if (hand is not MetaLinkHand.Left and not MetaLinkHand.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }

        return RunProfileStoreTransaction(
            profileStorePath,
            $"{hand.ToString().ToLowerInvariant()}-hand-calibration",
            stageCalibration,
            cancellationToken,
            commitStarted);
    }

    private static TResult RunProfileStoreTransaction<TResult>(
        string profileStorePath,
        string stageLabel,
        Func<string, TResult> stageCalibration,
        CancellationToken cancellationToken,
        Action? commitStarted = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileStorePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageLabel);
        ArgumentNullException.ThrowIfNull(stageCalibration);

        var canonicalPath = Path.GetFullPath(profileStorePath);
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new ArgumentException(
                "The calibration profile store must have a parent directory.",
                nameof(profileStorePath));
        var fileName = Path.GetFileName(canonicalPath);
        var stagedPath = Path.Combine(
            directory,
            $".{fileName}.{stageLabel}.{Guid.NewGuid():N}.stage");

        using var commitGate = new InternalDriverProfileStoreCommitGate(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialStore = File.Exists(canonicalPath)
                ? CalibrationProfileFile.LoadStore(canonicalPath)
                : CalibrationProfileStore.Empty;
            CalibrationProfileFile.SaveStore(stagedPath, initialStore);

            var result = stageCalibration(stagedPath);
            cancellationToken.ThrowIfCancellationRequested();
            var completedStore = CalibrationProfileFile.LoadStore(stagedPath);
            cancellationToken.ThrowIfCancellationRequested();
            return commitGate.Commit(() =>
            {
                commitStarted?.Invoke();
                CalibrationProfileFile.SaveStore(canonicalPath, completedStore);
                _ = CalibrationProfileFile.LoadStore(canonicalPath);
                return result;
            });
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
    }

    private static InternalDriverHandProfile ToHandProfile(
        ProtocolHand hand,
        CalibrationProfile profile,
        InternalDriverProfileReadiness readiness,
        string diagnostic) => new(
        hand,
        InternalDriverTrackerSerial.Require(
            profile.TrackerSerial,
            nameof(profile.TrackerSerial)),
        profile.TrackerToController.ToRigidTransform(),
        readiness,
        diagnostic)
    {
        Calibration = ToCalibrationEvidence(profile),
        MountAdjustment = profile.MountAdjustment,
        SourceProfile = profile,
    };

    internal static InternalDriverCalibrationEvidence ToCalibrationEvidence(
        CalibrationProfile profile)
    {
        if (profile.SchemaVersion is not
                CalibrationProfileSchema.DriverProfileVersion and not
                CalibrationProfileSchema.CurrentVersion ||
            !string.Equals(
                profile.DriverProfile,
                CalibrationDriverProfiles.LtbTouch,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "First-party session evidence requires a reusable schema-2 or schema-3 ltb_touch profile.");
        }

        var selectedMode = profile.SelectedMode switch
        {
            ProfileCalibrationMode.RotationOnly => InternalDriverCalibrationMode.RotationOnly,
            ProfileCalibrationMode.FullSixDof => InternalDriverCalibrationMode.FullSixDof,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        return new InternalDriverCalibrationEvidence(
            profile.SchemaVersion,
            selectedMode,
            profile.SelectionReason,
            profile.EstimatedLagMilliseconds,
            new InternalDriverCalibrationQualityEvidence(
                profile.Quality.RotationRmsDegrees,
                profile.Quality.PositionRmsMillimeters,
                profile.Quality.TranslationCondition,
                profile.Quality.InlierRatio),
            profile.CreatedUtc,
            selectedMode == InternalDriverCalibrationMode.FullSixDof
                ? profile.TrackerToController.TranslationMeters.Length() * 1000d
                : null);
    }

    internal static InternalDriverSettingsPreparation EnsureDefaultSettings(
        InternalDriverResolvedPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var expected = new InternalDriverSettings(
            InternalDriverSettingsSchema.CurrentVersion,
            OpenVrPathsDiscovery.Automatic,
            paths.StagedDriverRoot,
            paths.CalibrationProfileStorePath,
            trackerPathObservationStorePath:
                paths.EffectiveTrackerPathObservationStorePath);
        var loaded = InternalDriverSettingsFile.TryLoad(paths.SettingsPath);
        if (loaded.Status == InternalDriverSettingsLoadStatus.NotFound)
        {
            InternalDriverSettingsFile.Save(paths.SettingsPath, expected);
            return new InternalDriverSettingsPreparation(
                InternalDriverSettingsPreparationStatus.Created,
                "Created zero-input internal-driver settings for the current package.");
        }

        var current = loaded.Settings!;
        if (current.OpenVrPathsDiscovery.Mode != OpenVrPathsDiscoveryMode.Automatic ||
            !string.Equals(
                current.CalibrationProfileStorePath,
                expected.CalibrationProfileStorePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Internal-driver settings must retain automatic OpenVR discovery and the zero-input calibration profile path.");
        }

        if (current.TrackerPathObservationStorePath is { } configuredStorePath &&
            !string.Equals(
                configuredStorePath,
                expected.TrackerPathObservationStorePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Internal-driver settings contain a tracker-path observation store path " +
                "that differs from the resolved zero-input path.");
        }

        if (!string.Equals(
                current.StagedDriverRoot,
                expected.StagedDriverRoot,
                StringComparison.Ordinal))
        {
            var relocated = current.WithStagedDriverRoot(expected.StagedDriverRoot);
            InternalDriverSettingsFile.Save(paths.SettingsPath, relocated);
            return new InternalDriverSettingsPreparation(
                InternalDriverSettingsPreparationStatus.StagedDriverRootUpdated,
                "Updated the app-owned staged driver root for the relocated package; " +
                "automatic OpenVR discovery and the calibration profile path were preserved.");
        }

        return new InternalDriverSettingsPreparation(
            InternalDriverSettingsPreparationStatus.Current,
            "Windows x64 and zero-input LocalApplicationData settings are available.");
    }

    internal sealed record GuidedHandCapture(
        MetaLinkHand Hand,
        IReadOnlyList<MetaLinkControllerSnapshot> MetaSamples,
        IReadOnlyDictionary<string, List<PoseSourceSample>> TrackerSamples,
        IReadOnlyDictionary<string, bool> ContinuouslyConnected);

    internal sealed record SelectedHandCalibrationBase(
        InternalDriverHandProfile PreservedOpposite,
        string? ReplacedTrackerSerial);
}

internal enum InternalDriverSettingsPreparationStatus
{
    Current = 0,
    Created,
    StagedDriverRootUpdated,
}

internal sealed record InternalDriverSettingsPreparation(
    InternalDriverSettingsPreparationStatus Status,
    string Diagnostic);

/// <summary>Monotonic, fixed-rate gate for bounded capture progress callbacks.</summary>
internal sealed class InternalDriverCaptureReportCadence
{
    private readonly ulong _intervalNanoseconds;
    private ulong _lastReportNanoseconds;

    public InternalDriverCaptureReportCadence(TimeSpan interval, ulong startedNanoseconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _intervalNanoseconds = checked((ulong)interval.Ticks * 100UL);
        _lastReportNanoseconds = startedNanoseconds;
    }

    public bool ShouldReport(ulong nowNanoseconds)
    {
        if (nowNanoseconds < _lastReportNanoseconds ||
            nowNanoseconds - _lastReportNanoseconds < _intervalNanoseconds)
        {
            return false;
        }

        _lastReportNanoseconds = nowNanoseconds;
        return true;
    }
}

internal sealed class JsonLinesInternalDriverSessionOutput : IInternalDriverSessionOutput
{
    internal const long DefaultMaxFileSizeBytes = 4L * 1024L * 1024L;
    internal const int DefaultRetainedFileCount = 4;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly string _path;
    private readonly long _maxFileSizeBytes;
    private readonly int _retainedFileCount;
    private readonly object _sync = new();
    private StreamWriter _writer;
    private long _currentFileSizeBytes;
    private LogTransition? _lastTransition;
    private bool _disposed;

    public JsonLinesInternalDriverSessionOutput(string path)
        : this(path, DefaultMaxFileSizeBytes, DefaultRetainedFileCount)
    {
    }

    internal JsonLinesInternalDriverSessionOutput(
        string path,
        long maxFileSizeBytes,
        int retainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);
        _path = Path.GetFullPath(path);
        _maxFileSizeBytes = maxFileSizeBytes;
        _retainedFileCount = retainedFileCount;
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? throw new ArgumentException(
            "Structured output path must have a parent directory.",
            nameof(path)));
        PruneExpiredArchives();
        _writer = OpenWriter(_path, out _currentFileSizeBytes);
    }

    private static StreamWriter OpenWriter(string path, out long currentFileSizeBytes)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        currentFileSizeBytes = stream.Length;
        return new StreamWriter(stream, Utf8NoBom, bufferSize: 4096, leaveOpen: false)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
    }

    public void Write(InternalDriverSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lastTransition?.Matches(snapshot) == true)
            {
                return;
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var recordSizeBytes = Utf8NoBom.GetByteCount(json) + 1L;
            if (_currentFileSizeBytes > 0 &&
                _currentFileSizeBytes + recordSizeBytes > _maxFileSizeBytes)
            {
                Rotate();
            }

            _writer.WriteLine(json);
            _currentFileSizeBytes += recordSizeBytes;
            _lastTransition = LogTransition.From(snapshot);
        }
    }

    private void Rotate()
    {
        _writer.Dispose();
        if (_retainedFileCount == 0)
        {
            File.Delete(_path);
        }
        else
        {
            File.Delete(ArchivePath(_retainedFileCount));
            for (var index = _retainedFileCount - 1; index >= 1; index--)
            {
                var source = ArchivePath(index);
                if (File.Exists(source))
                {
                    File.Move(source, ArchivePath(index + 1));
                }
            }

            File.Move(_path, ArchivePath(1));
        }

        _writer = OpenWriter(_path, out _currentFileSizeBytes);
    }

    private void PruneExpiredArchives()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var fileName = Path.GetFileName(_path);
        var archivePrefix = $"{fileName}.";
        foreach (var candidate in Directory.EnumerateFiles(directory, $"{fileName}.*"))
        {
            var candidateFileName = Path.GetFileName(candidate);
            if (!candidateFileName.StartsWith(
                    archivePrefix,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = candidateFileName.AsSpan(archivePrefix.Length);
            if (IsCanonicalArchiveIndex(suffix, out var index) &&
                index > _retainedFileCount)
            {
                File.Delete(candidate);
            }
        }
    }

    private static bool IsCanonicalArchiveIndex(ReadOnlySpan<char> suffix, out int index)
    {
        if (suffix.IsEmpty || suffix[0] == '0')
        {
            index = 0;
            return false;
        }

        foreach (var character in suffix)
        {
            if (character is < '0' or > '9')
            {
                index = 0;
                return false;
            }
        }

        return int.TryParse(suffix, out index);
    }

    private string ArchivePath(int index) => $"{_path}.{index}";

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private readonly record struct HandTransition(
        ProtocolHand Hand,
        string? TrackerSerial,
        bool TrackerConnected,
        bool TrackerTracked,
        MetaLinkReadiness MetaReadiness,
        bool MetaInputsValid,
        InternalDriverProfileReadiness ProfileReadiness,
        bool IsPublishing,
        InternalDriverNeutralReason NeutralReason,
        string Diagnostic,
        InternalDriverCalibrationEvidence? Calibration,
        InternalDriverCaptureEvidence? Capture)
    {
        public static HandTransition From(InternalDriverHandSnapshot hand) => new(
            hand.Hand,
            hand.TrackerSerial,
            hand.TrackerConnected,
            hand.TrackerTracked,
            hand.MetaReadiness,
            hand.MetaInputsValid,
            hand.ProfileReadiness,
            hand.IsPublishing,
            hand.NeutralReason,
            hand.Diagnostic,
            hand.Calibration,
            hand.Capture);
    }

    private readonly record struct FeedTransition(
        DriverFeedReadiness Readiness,
        ProtocolSessionId? SessionId,
        int ReconnectAttempts,
        string? LastError)
    {
        public static FeedTransition From(InternalDriverFeedSnapshot feed) => new(
            feed.Readiness,
            feed.SessionId,
            feed.ReconnectAttempts,
            feed.LastError);
    }

    private sealed record LogTransition(
        InternalDriverSessionState State,
        InternalDriverSessionReadiness Readiness,
        HandTransition Left,
        HandTransition Right,
        FeedTransition Feed,
        InternalDriverDriverEvidence? Driver,
        InternalDriverLighthouseHmdEvidence? LighthouseHmd,
        InternalDriverTrackerNeutralizationSnapshot? TrackerNeutralization,
        InternalDriverManualBindingVerificationEvidence? ManualBindingVerification,
        bool RestartRequired,
        string Diagnostic,
        string Remediation)
    {
        public bool Matches(InternalDriverSessionSnapshot snapshot) =>
            State == snapshot.State &&
            Readiness == snapshot.Readiness &&
            Left == HandTransition.From(snapshot.Left) &&
            Right == HandTransition.From(snapshot.Right) &&
            Feed == FeedTransition.From(snapshot.Feed) &&
            Driver == snapshot.Driver &&
            LighthouseHmd == snapshot.LighthouseHmd &&
            TrackerNeutralization == snapshot.TrackerNeutralization &&
            ManualBindingVerification == snapshot.ManualBindingVerification &&
            RestartRequired == snapshot.RestartRequired &&
            string.Equals(Diagnostic, snapshot.Diagnostic, StringComparison.Ordinal) &&
            string.Equals(Remediation, snapshot.Remediation, StringComparison.Ordinal);

        public static LogTransition From(InternalDriverSessionSnapshot snapshot) => new(
            snapshot.State,
            snapshot.Readiness,
            HandTransition.From(snapshot.Left),
            HandTransition.From(snapshot.Right),
            FeedTransition.From(snapshot.Feed),
            snapshot.Driver,
            snapshot.LighthouseHmd,
            snapshot.TrackerNeutralization,
            snapshot.ManualBindingVerification,
            snapshot.RestartRequired,
            snapshot.Diagnostic,
            snapshot.Remediation);
    }
}
