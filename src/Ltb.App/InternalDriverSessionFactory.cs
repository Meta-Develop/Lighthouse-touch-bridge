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
                Path.Combine(applicationRoot, "logs", "internal-driver.jsonl")));
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
    string StructuredLogPath);

internal sealed class ProductionInternalDriverSessionRuntime : IInternalDriverSessionRuntime
{
    private const string ControllerModel = "Quest 2 Touch";
    private static readonly TimeSpan CaptureProgressInterval = TimeSpan.FromMilliseconds(250);
    private readonly InternalDriverSessionOptions _options;
    private readonly InternalDriverResolvedPaths _paths;
    private readonly ISteamVrDriverLifecycle _driverLifecycle;
    private readonly Dictionary<string, TrackerSourceEntry> _trackerSources =
        new(StringComparer.Ordinal);
    private MetaLinkRuntime? _meta;
    private OpenVrSession? _openVr;
    private InternalDriverCaptureEvidence? _leftCaptureEvidence;
    private InternalDriverCaptureEvidence? _rightCaptureEvidence;
    private bool _disposed;

    public ProductionInternalDriverSessionRuntime(
        InternalDriverSessionOptions options,
        InternalDriverResolvedPaths paths)
        : this(options, paths, SteamVrDriverLifecycle.CreateDefault())
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

        EnsureDefaultSettings();
        return new InternalDriverPlatformProbe(
            true,
            "Windows x64 and zero-input LocalApplicationData settings are available.",
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
                _trackerSources.Clear();
                _openVr.Dispose();
                _openVr = null;
                return new InternalDriverRuntimeObservation(
                    SteamVrRunning: false,
                    health.Diagnostic,
                    meta,
                    [],
                    new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal));
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
            return new InternalDriverRuntimeObservation(
                SteamVrRunning: false,
                exception.Message,
                meta,
                [],
                new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal));
        }
    }

    public async ValueTask<InternalDriverProfilePair> ResolveProfilesAsync(
        InternalDriverRuntimeObservation observation,
        InternalDriverProgress progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(progress);
        var serials = observation.TrackerSamples.Keys
            .OrderBy(serial => serial, StringComparer.Ordinal)
            .ToArray();
        if (serials.Length != 2 || string.Equals(serials[0], serials[1], StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Profile resolution requires exactly two distinct observed tracker serials.");
        }

        _leftCaptureEvidence = null;
        _rightCaptureEvidence = null;

        var calibration = new InternalDriverCalibration(_paths.CalibrationProfileStorePath);
        var reusable = FindReusablePair(calibration, serials);
        if (reusable is not null)
        {
            return reusable;
        }

        var leftCapture = await CaptureHandAsync(
            MetaLinkHand.Left,
            serials,
            progress,
            cancellationToken).ConfigureAwait(false);
        var rightCapture = await CaptureHandAsync(
            MetaLinkHand.Right,
            serials,
            progress,
            cancellationToken).ConfigureAwait(false);
        progress(
            InternalDriverSessionState.Association,
            "Associating the two raw trackers from separate per-hand angular-speed captures.",
            "Keep both tracker mounts unchanged while association and calibration finish.");
        var association = TrackerHandAssociator.Associate(
            ToAssociationCapture(leftCapture),
            ToAssociationCapture(rightCapture));
        if (!association.Success)
        {
            throw new InvalidOperationException(
                $"First-party tracker association failed ({association.Status}): {association.Reason}");
        }

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
        var left = Calibrate(
            calibration,
            leftCapture,
            MetaLinkHand.Left,
            association.Left!.TrackerSerial);
        var right = Calibrate(
            calibration,
            rightCapture,
            MetaLinkHand.Right,
            association.Right!.TrackerSerial);
        progress(
            InternalDriverSessionState.SaveProfile,
            "Both first-party results passed validation; exact schema-2 profiles were saved and reloaded.",
            "Keep the physical tracker mounts fixed for profile reuse.");
        return new InternalDriverProfilePair(left, right);
    }

    public IDriverFeed CreateFeed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DriverFeed(new NamedPipeDriverTransportFactory());
    }

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
        _trackerSources.Clear();
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
        var duplicate = candidates
            .GroupBy(device => device.StableDeviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"SteamVR enumerated duplicate physical tracker serial '{duplicate}'.");
        }

        var currentSerials = candidates
            .Select(candidate => candidate.StableDeviceId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _trackerSources.Keys.Where(serial => !currentSerials.Contains(serial)).ToArray())
        {
            _trackerSources.Remove(stale);
        }

        var samples = new Dictionary<string, PoseSourceSample>(StringComparer.Ordinal);
        foreach (var device in candidates)
        {
            if (!_trackerSources.TryGetValue(device.StableDeviceId, out var entry) ||
                entry.TransientIndex != device.TransientDeviceIndex)
            {
                var source = _openVr!.CreateTrackedPoseSource(
                    device,
                    OpenVrTrackingUniverse.RawAndUncalibrated);
                entry = new TrackerSourceEntry(device.TransientDeviceIndex, source);
                _trackerSources[device.StableDeviceId] = entry;
            }

            samples.Add(device.StableDeviceId, entry.Source.ReadPose());
        }

        return samples;
    }

    private static InternalDriverProfilePair? FindReusablePair(
        InternalDriverCalibration calibration,
        IReadOnlyList<string> serials)
    {
        foreach (var (leftSerial, rightSerial) in new[]
                 {
                     (serials[0], serials[1]),
                     (serials[1], serials[0]),
                 })
        {
            var left = calibration.FindReusableProfile(new InternalDriverCalibrationContext(
                MetaLinkHand.Left,
                leftSerial,
                ControllerModel));
            var right = calibration.FindReusableProfile(new InternalDriverCalibrationContext(
                MetaLinkHand.Right,
                rightSerial,
                ControllerModel));
            if (!left.CanReuse || !right.CanReuse)
            {
                continue;
            }

            return new InternalDriverProfilePair(
                ToHandProfile(ProtocolHand.Left, left.Profile!, InternalDriverProfileReadiness.Reused, left.Diagnostic),
                ToHandProfile(ProtocolHand.Right, right.Profile!, InternalDriverProfileReadiness.Reused, right.Diagnostic));
        }

        return null;
    }

    private async ValueTask<GuidedHandCapture> CaptureHandAsync(
        MetaLinkHand hand,
        IReadOnlyList<string> trackerSerials,
        InternalDriverProgress progress,
        CancellationToken cancellationToken)
    {
        var mappedMetaSamples = new InternalDriverMappedMetaSampleFilter(hand);
        var captureEvidence = new InternalDriverCaptureEvidenceTracker(hand);
        var trackerSamples = trackerSerials.ToDictionary(
            serial => serial,
            _ => new List<PoseSourceSample>(),
            StringComparer.Ordinal);
        var lastTrackerTimes = trackerSerials.ToDictionary(
            serial => serial,
            _ => double.NegativeInfinity,
            StringComparer.Ordinal);
        var startedNanoseconds = GetMonotonicNanoseconds();
        var durationNanoseconds = ToNanoseconds(_options.GuidedCaptureDurationPerHand);
        var reportCadence = new InternalDriverCaptureReportCadence(
            CaptureProgressInterval,
            startedNanoseconds);
        InternalDriverRuntimeObservation? latestObservation = null;
        UpdateCaptureEvidence(hand, captureEvidence.Evaluate());
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
            if (captureEvidence.TryAppend(observation.Meta))
            {
                UpdateCaptureEvidence(hand, captureEvidence.Evaluate());
            }

            if (meta.Readiness == MetaLinkReadiness.Ready && meta.Controller is { } controller)
            {
                _ = mappedMetaSamples.TryAppend(controller);
            }

            foreach (var serial in trackerSerials)
            {
                if (observation.TrackerSamples.TryGetValue(serial, out var sample))
                {
                    if (sample.MonotonicHostTimeSeconds > lastTrackerTimes[serial])
                    {
                        trackerSamples[serial].Add(sample);
                        lastTrackerTimes[serial] = sample.MonotonicHostTimeSeconds;
                    }
                }
            }

            if (reportCadence.ShouldReport(GetMonotonicNanoseconds()))
            {
                ReportCaptureEvidence(hand, progress, latestObservation);
            }

            await DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        var finalEvidence = captureEvidence.Evaluate();
        UpdateCaptureEvidence(hand, finalEvidence);
        ReportCaptureEvidence(hand, progress, latestObservation);

        EnsureRotationReady(hand, finalEvidence);

        if (mappedMetaSamples.Samples.Count == 0 ||
            trackerSamples.Values.Any(samples => samples.Count == 0))
        {
            throw new InvalidOperationException(
                $"The {hand} guided capture did not retain Meta and both raw tracker streams.");
        }

        return new GuidedHandCapture(
            hand,
            mappedMetaSamples.Samples.ToArray(),
            trackerSamples);
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

    private static HandMotionCapture ToAssociationCapture(GuidedHandCapture capture)
    {
        var controllerSamples = capture.MetaSamples.Select(ToPoseSample).ToArray();
        var candidates = capture.TrackerSamples.Select(pair =>
            new TrackerAssociationCandidate(
                pair.Key,
                pair.Value.Select(ToTrackerPoseSample).ToArray(),
                pair.Value.Any(sample => sample.IsConnected))).ToArray();
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
        string trackerSerial)
    {
        var context = new InternalDriverCalibrationContext(hand, trackerSerial, ControllerModel);
        var retained = new MetaLinkCalibrationCapture(
            hand,
            trackerSerial,
            capture.MetaSamples,
            capture.TrackerSamples[trackerSerial]);
        var result = calibration.CalibrateAndSave(context, retained);
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

    private static InternalDriverHandProfile ToHandProfile(
        ProtocolHand hand,
        CalibrationProfile profile,
        InternalDriverProfileReadiness readiness,
        string diagnostic) => new(
        hand,
        profile.TrackerSerial,
        profile.TrackerToController.ToRigidTransform(),
        readiness,
        diagnostic)
    {
        Calibration = ToCalibrationEvidence(profile),
    };

    internal static InternalDriverCalibrationEvidence ToCalibrationEvidence(
        CalibrationProfile profile)
    {
        if (profile.SchemaVersion != CalibrationProfileSchema.CurrentVersion ||
            !string.Equals(
                profile.DriverProfile,
                CalibrationDriverProfiles.LtbTouch,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "First-party session evidence requires an exact schema-2 ltb_touch profile.");
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
            profile.CreatedUtc);
    }

    private void EnsureDefaultSettings()
    {
        var expected = new InternalDriverSettings(
            InternalDriverSettingsSchema.CurrentVersion,
            OpenVrPathsDiscovery.Automatic,
            _paths.StagedDriverRoot,
            _paths.CalibrationProfileStorePath);
        var loaded = InternalDriverSettingsFile.TryLoad(_paths.SettingsPath);
        if (loaded.Status == InternalDriverSettingsLoadStatus.NotFound)
        {
            InternalDriverSettingsFile.Save(_paths.SettingsPath, expected);
            return;
        }

        var current = loaded.Settings!;
        if (current.OpenVrPathsDiscovery.Mode != OpenVrPathsDiscoveryMode.Automatic ||
            !string.Equals(current.StagedDriverRoot, expected.StagedDriverRoot, StringComparison.Ordinal) ||
            !string.Equals(
                current.CalibrationProfileStorePath,
                expected.CalibrationProfileStorePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Internal-driver settings must retain automatic OpenVR discovery and the zero-input staged/profile paths.");
        }
    }

    private sealed record TrackerSourceEntry(uint TransientIndex, TrackedPoseSource Source);

    private sealed record GuidedHandCapture(
        MetaLinkHand Hand,
        IReadOnlyList<MetaLinkControllerSnapshot> MetaSamples,
        IReadOnlyDictionary<string, List<PoseSourceSample>> TrackerSamples);
}

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
            var transition = LogTransition.From(snapshot);
            if (_lastTransition == transition)
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
            _lastTransition = transition;
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
        foreach (var candidate in Directory.EnumerateFiles(directory, $"{fileName}.*"))
        {
            var suffix = Path.GetFileName(candidate).AsSpan(fileName.Length + 1);
            if (int.TryParse(suffix, out var index) && index > _retainedFileCount)
            {
                File.Delete(candidate);
            }
        }
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
        bool RestartRequired,
        string Diagnostic,
        string Remediation)
    {
        public static LogTransition From(InternalDriverSessionSnapshot snapshot) => new(
            snapshot.State,
            snapshot.Readiness,
            HandTransition.From(snapshot.Left),
            HandTransition.From(snapshot.Right),
            FeedTransition.From(snapshot.Feed),
            snapshot.Driver,
            snapshot.LighthouseHmd,
            snapshot.RestartRequired,
            snapshot.Diagnostic,
            snapshot.Remediation);
    }
}
