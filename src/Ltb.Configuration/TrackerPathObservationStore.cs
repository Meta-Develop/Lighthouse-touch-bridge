using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ltb.Configuration;

/// <summary>
/// Narrow persistence seam used by tracker-path evidence storage. Implementors
/// must make <see cref="WriteAtomic"/> a complete atomic file replacement.
/// </summary>
public interface ITrackerPathObservationStorePersistence
{
    bool Exists(string path);

    byte[] ReadAllBytes(string path);

    void WriteAtomic(string path, string contents);

    void Delete(string path);
}

/// <summary>
/// Versioned, deterministic, fail-closed storage for serial-to-exact-registered-
/// device-path evidence observed during a normal live OpenVR session.
/// </summary>
public sealed class TrackerPathObservationStore
{
    private const string PendingSuffix = ".path-change-pending";
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _pendingPath;
    private readonly ITrackerPathObservationStorePersistence _persistence;

    public TrackerPathObservationStore(string path)
        : this(path, FileTrackerPathObservationStorePersistence.Instance)
    {
    }

    public TrackerPathObservationStore(
        string path,
        ITrackerPathObservationStorePersistence persistence)
    {
        _path = SettingsPathValidation.RequireCanonicalAbsoluteFilePath(
            path,
            nameof(path));
        _pendingPath = _path + PendingSuffix;
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    /// <summary>
    /// Loads a complete immutable, serial-sorted snapshot. Missing storage is
    /// empty. Any pending path-change marker makes all evidence unavailable.
    /// </summary>
    public IReadOnlyList<TrackerPathObservation> LoadAll()
    {
        lock (_sync)
        {
            ThrowIfPending();
            return LoadMain();
        }
    }

    /// <summary>
    /// Looks up one exact canonical identity after applying the serial
    /// canonicalization rule once. Comparison is ordinal.
    /// </summary>
    public TrackerPathObservation? TryLookup(string trackerSerial)
    {
        var canonicalSerial =
            TrackerPathObservationValidation.CanonicalizeTrackerSerial(
                trackerSerial,
                nameof(trackerSerial));
        lock (_sync)
        {
            ThrowIfPending();
            return LoadMain().FirstOrDefault(observation =>
                string.Equals(
                    observation.TrackerSerial,
                    canonicalSerial,
                    StringComparison.Ordinal));
        }
    }

    /// <summary>Records one live candidate using the batch transaction.</summary>
    public TrackerPathObservation RecordObservation(
        TrackerPathObservationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return RecordObservations([candidate]).Single();
    }

    /// <summary>
    /// Validates and records one immutable live batch with exactly one atomic
    /// main-store replacement. Equal or regressing per-serial UTC values fail
    /// without a main-store write.
    /// </summary>
    public IReadOnlyList<TrackerPathObservation> RecordObservations(
        IReadOnlyList<TrackerPathObservationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var candidateSnapshot = ValidateCandidateBatch(candidates);

        lock (_sync)
        {
            var pendingSerials = _persistence.Exists(_pendingPath)
                ? LoadPendingSerials()
                : null;
            var existing = LoadMain();

            if (pendingSerials is not null)
            {
                ValidateReconciliationBatch(
                    existing,
                    candidateSnapshot,
                    pendingSerials);
            }

            var bySerial = existing.ToDictionary(
                observation => observation.TrackerSerial,
                StringComparer.Ordinal);
            var changedPathSerials = new HashSet<string>(StringComparer.Ordinal);

            foreach (var candidate in candidateSnapshot)
            {
                if (bySerial.TryGetValue(
                        candidate.TrackerSerial,
                        out var prior))
                {
                    if (candidate.ObservedAtUtc <= prior.LastObservedUtc)
                    {
                        throw new InvalidOperationException(
                            "Tracker-path evidence UTC must increase strictly for each serial.");
                    }

                    if (string.Equals(
                            candidate.RegisteredDevicePath,
                            prior.RegisteredDevicePath,
                            StringComparison.Ordinal))
                    {
                        bySerial[candidate.TrackerSerial] =
                            new TrackerPathObservation(
                                prior.TrackerSerial,
                                prior.RegisteredDevicePath,
                                candidate.ObservedAtUtc,
                                prior.PathChangeHistory);
                    }
                    else
                    {
                        changedPathSerials.Add(candidate.TrackerSerial);
                        var history = prior.PathChangeHistory
                            .Append(new TrackerPathObservationHistoryEntry(
                                prior.RegisteredDevicePath,
                                prior.LastObservedUtc,
                                candidate.ObservedAtUtc))
                            .TakeLast(
                                TrackerPathObservationSchema.MaximumHistoryEntries)
                            .ToArray();
                        bySerial[candidate.TrackerSerial] =
                            new TrackerPathObservation(
                                prior.TrackerSerial,
                                candidate.RegisteredDevicePath,
                                candidate.ObservedAtUtc,
                                history);
                    }
                }
                else
                {
                    bySerial.Add(
                        candidate.TrackerSerial,
                        new TrackerPathObservation(
                            candidate.TrackerSerial,
                            candidate.RegisteredDevicePath,
                            candidate.ObservedAtUtc));
                }
            }

            var replacement = ValidateSnapshot(bySerial.Values);
            var serialized = Serialize(replacement);
            if (Utf8WithoutBom.GetByteCount(serialized) >
                TrackerPathObservationSchema.MaximumSerializedBytes)
            {
                throw new InvalidDataException(
                    "Tracker-path evidence exceeds the serialized size bound.");
            }

            var createdPending = pendingSerials is null &&
                changedPathSerials.Count > 0;
            if (createdPending)
            {
                WritePendingSerials(changedPathSerials);
            }

            _persistence.WriteAtomic(_path, serialized);
            ValidateReadBack(serialized);

            if (pendingSerials is not null || createdPending)
            {
                _persistence.Delete(_pendingPath);
                if (_persistence.Exists(_pendingPath))
                {
                    throw new IOException(
                        "The tracker-path change guard could not be removed.");
                }
            }

            var updated = candidateSnapshot
                .Select(candidate => replacement.Single(observation =>
                    string.Equals(
                        observation.TrackerSerial,
                        candidate.TrackerSerial,
                        StringComparison.Ordinal)))
                .OrderBy(
                    observation => observation.TrackerSerial,
                    StringComparer.Ordinal)
                .ToArray();
            return new ReadOnlyCollection<TrackerPathObservation>(updated);
        }
    }

    public override string ToString() =>
        $"TrackerPathObservationStore {{ path = redacted, schema_version = " +
        $"{TrackerPathObservationSchema.CurrentVersion} }}";

    private static ReadOnlyCollection<TrackerPathObservationCandidate>
        ValidateCandidateBatch(
            IReadOnlyList<TrackerPathObservationCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            throw new ArgumentException(
                "At least one live tracker-path observation is required.",
                nameof(candidates));
        }

        if (candidates.Count > TrackerPathObservationSchema.MaximumObservations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidates),
                "The live tracker-path observation batch exceeds its bound.");
        }

        var snapshot = candidates.ToArray();
        if (snapshot.Any(candidate => candidate is null))
        {
            throw new ArgumentException(
                "The live tracker-path observation batch must not contain null entries.",
                nameof(candidates));
        }

        if (snapshot
            .GroupBy(candidate => candidate.TrackerSerial, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "The live tracker-path observation batch contains a duplicate canonical serial.",
                nameof(candidates));
        }

        if (snapshot
            .GroupBy(
                candidate => candidate.RegisteredDevicePath,
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new ArgumentException(
                "The live tracker-path observation batch contains a duplicate current path.",
                nameof(candidates));
        }

        return new ReadOnlyCollection<TrackerPathObservationCandidate>(snapshot);
    }

    private static void ValidateReconciliationBatch(
        IReadOnlyList<TrackerPathObservation> existing,
        IReadOnlyList<TrackerPathObservationCandidate> candidates,
        IReadOnlySet<string> pendingSerials)
    {
        var existingBySerial = existing.ToDictionary(
            observation => observation.TrackerSerial,
            StringComparer.Ordinal);
        var candidateBySerial = candidates.ToDictionary(
            candidate => candidate.TrackerSerial,
            StringComparer.Ordinal);

        foreach (var pendingSerial in pendingSerials)
        {
            if (!existingBySerial.TryGetValue(
                    pendingSerial,
                    out var current) ||
                !candidateBySerial.TryGetValue(
                    pendingSerial,
                    out var candidate) ||
                candidate.ObservedAtUtc <= current.LastObservedUtc)
            {
                throw new InvalidOperationException(
                    "Pending tracker-path evidence requires a complete newer live reconciliation batch.");
            }
        }
    }

    private IReadOnlyList<TrackerPathObservation> LoadMain()
    {
        byte[] bytes;
        try
        {
            bytes = _persistence.ReadAllBytes(_path);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<TrackerPathObservation>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<TrackerPathObservation>();
        }

        return DeserializeStore(bytes);
    }

    private static IReadOnlyList<TrackerPathObservation> DeserializeStore(
        byte[] bytes)
    {
        var json = DecodeBounded(bytes, "Tracker-path evidence");
        EnsureNoDuplicateMembers(json, "Tracker-path evidence");

        StoreDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<StoreDto>(json, SerializerOptions)
                ?? throw new InvalidDataException(
                    "Tracker-path evidence JSON must be an object.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                "Tracker-path evidence is not valid strict schema-versioned JSON.");
        }

        if (dto.SchemaVersion != TrackerPathObservationSchema.CurrentVersion)
        {
            throw new InvalidDataException(
                "Tracker-path evidence uses an unsupported schema version.");
        }

        if (dto.Observations is null)
        {
            throw new InvalidDataException(
                "Tracker-path evidence is missing its observation collection.");
        }

        try
        {
            var observations = new List<TrackerPathObservation>(
                dto.Observations.Count);
            var normalizedSerials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var persisted in dto.Observations)
            {
                if (persisted is null)
                {
                    throw new InvalidDataException(
                        "Tracker-path evidence contains a null observation.");
                }

                var canonicalSerial =
                    TrackerPathObservationValidation.CanonicalizeTrackerSerial(
                        persisted.TrackerSerial,
                        "persistedTrackerSerial");
                if (!normalizedSerials.Add(canonicalSerial))
                {
                    throw new InvalidDataException(
                        "Tracker-path evidence contains a duplicate canonical serial.");
                }

                if (!string.Equals(
                        persisted.TrackerSerial,
                        canonicalSerial,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Tracker-path evidence contains a noncanonical serial.");
                }

                if (persisted.PathChangeHistory is null)
                {
                    throw new InvalidDataException(
                        "Tracker-path evidence is missing path-change history.");
                }

                var history = persisted.PathChangeHistory
                    .Select(entry =>
                    {
                        if (entry is null)
                        {
                            throw new InvalidDataException(
                                "Tracker-path evidence contains a null history entry.");
                        }

                        return new TrackerPathObservationHistoryEntry(
                            entry.PriorRegisteredDevicePath,
                            TrackerPathObservationValidation.ParseUtc(
                                entry.PriorLastObservedUtc),
                            TrackerPathObservationValidation.ParseUtc(
                                entry.ReplacementUtc));
                    })
                    .ToArray();
                observations.Add(new TrackerPathObservation(
                    canonicalSerial,
                    persisted.RegisteredDevicePath,
                    TrackerPathObservationValidation.ParseUtc(
                        persisted.LastObservedUtc),
                    history));
            }

            return ValidateSnapshot(observations);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            throw new InvalidDataException(
                "Tracker-path evidence contains invalid redacted observation data.");
        }
    }

    private static ReadOnlyCollection<TrackerPathObservation> ValidateSnapshot(
        IEnumerable<TrackerPathObservation> observations)
    {
        var snapshot = observations
            .OrderBy(
                observation => observation.TrackerSerial,
                StringComparer.Ordinal)
            .ToArray();
        if (snapshot.Length > TrackerPathObservationSchema.MaximumObservations)
        {
            throw new InvalidDataException(
                "Tracker-path evidence exceeds the observation-count bound.");
        }

        if (snapshot
            .GroupBy(
                observation => observation.TrackerSerial,
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new InvalidDataException(
                "Tracker-path evidence contains a duplicate canonical serial.");
        }

        if (snapshot
            .GroupBy(
                observation => observation.RegisteredDevicePath,
                StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
        {
            throw new InvalidDataException(
                "Tracker-path evidence contains a duplicate current path.");
        }

        foreach (var observation in snapshot)
        {
            ValidateHistory(observation);
        }

        return new ReadOnlyCollection<TrackerPathObservation>(snapshot);
    }

    private static void ValidateHistory(TrackerPathObservation observation)
    {
        var history = observation.PathChangeHistory;
        for (var index = 0; index < history.Count; index++)
        {
            var entry = history[index];
            var replacementPath = index + 1 < history.Count
                ? history[index + 1].PriorRegisteredDevicePath
                : observation.RegisteredDevicePath;
            if (string.Equals(
                    entry.PriorRegisteredDevicePath,
                    replacementPath,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Tracker-path evidence contains an invalid no-op path transition.");
            }

            if (entry.ReplacementUtc > observation.LastObservedUtc)
            {
                throw new InvalidDataException(
                    "Tracker-path evidence contains nonchronological history.");
            }

            if (index > 0)
            {
                var priorEntry = history[index - 1];
                if (priorEntry.ReplacementUtc > entry.PriorLastObservedUtc)
                {
                    throw new InvalidDataException(
                        "Tracker-path evidence contains nonchronological history.");
                }
            }
        }
    }

    private static string Serialize(
        IReadOnlyList<TrackerPathObservation> observations)
    {
        var dto = new StoreDto
        {
            SchemaVersion = TrackerPathObservationSchema.CurrentVersion,
            Observations = observations
                .OrderBy(
                    observation => observation.TrackerSerial,
                    StringComparer.Ordinal)
                .Select(observation => new ObservationDto
                {
                    TrackerSerial = observation.TrackerSerial,
                    RegisteredDevicePath = observation.RegisteredDevicePath,
                    LastObservedUtc = TrackerPathObservationValidation.FormatUtc(
                        observation.LastObservedUtc),
                    PathChangeHistory = observation.PathChangeHistory
                        .Select(entry => new HistoryDto
                        {
                            PriorRegisteredDevicePath =
                                entry.PriorRegisteredDevicePath,
                            PriorLastObservedUtc =
                                TrackerPathObservationValidation.FormatUtc(
                                    entry.PriorLastObservedUtc),
                            ReplacementUtc =
                                TrackerPathObservationValidation.FormatUtc(
                                    entry.ReplacementUtc),
                        })
                        .ToArray(),
                })
                .ToArray(),
        };
        return JsonSerializer.Serialize(dto, SerializerOptions) + "\n";
    }

    private void ValidateReadBack(string expected)
    {
        var bytes = _persistence.ReadAllBytes(_path);
        var actual = DecodeBounded(bytes, "Tracker-path evidence");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Tracker-path evidence readback does not match the atomic candidate.");
        }

        _ = DeserializeStore(bytes);
    }

    private void ThrowIfPending()
    {
        if (_persistence.Exists(_pendingPath))
        {
            throw new InvalidDataException(
                "Tracker-path evidence is unavailable while a path change is pending reconciliation.");
        }
    }

    private IReadOnlySet<string> LoadPendingSerials()
    {
        var bytes = _persistence.ReadAllBytes(_pendingPath);
        var json = DecodeBounded(bytes, "Tracker-path change guard");
        EnsureNoDuplicateMembers(json, "Tracker-path change guard");

        PendingDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<PendingDto>(json, SerializerOptions)
                ?? throw new InvalidDataException(
                    "Tracker-path change guard JSON must be an object.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                "Tracker-path change guard is not valid strict schema-versioned JSON.");
        }

        if (dto.SchemaVersion != TrackerPathObservationSchema.CurrentVersion ||
            dto.AffectedTrackerSerials is null ||
            dto.AffectedTrackerSerials.Count is < 1
                or > TrackerPathObservationSchema.MaximumObservations)
        {
            throw new InvalidDataException(
                "Tracker-path change guard contains invalid redacted data.");
        }

        try
        {
            var serials = new HashSet<string>(StringComparer.Ordinal);
            foreach (var persistedSerial in dto.AffectedTrackerSerials)
            {
                var canonical =
                    TrackerPathObservationValidation.CanonicalizeTrackerSerial(
                        persistedSerial,
                        "pendingTrackerSerial");
                if (!string.Equals(
                        persistedSerial,
                        canonical,
                        StringComparison.Ordinal) ||
                    !serials.Add(canonical))
                {
                    throw new InvalidDataException(
                        "Tracker-path change guard contains invalid redacted data.");
                }
            }

            return serials;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException(
                "Tracker-path change guard contains invalid redacted data.");
        }
    }

    private void WritePendingSerials(IReadOnlySet<string> changedPathSerials)
    {
        var dto = new PendingDto
        {
            SchemaVersion = TrackerPathObservationSchema.CurrentVersion,
            AffectedTrackerSerials = changedPathSerials
                .OrderBy(serial => serial, StringComparer.Ordinal)
                .ToArray(),
        };
        var json = JsonSerializer.Serialize(dto, SerializerOptions) + "\n";
        _persistence.WriteAtomic(_pendingPath, json);
    }

    private static string DecodeBounded(byte[] bytes, string description)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > TrackerPathObservationSchema.MaximumSerializedBytes)
        {
            throw new InvalidDataException(
                $"{description} exceeds the serialized size bound.");
        }

        try
        {
            return Utf8WithoutBom.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException(
                $"{description} is not valid UTF-8.");
        }
    }

    private static void EnsureNoDuplicateMembers(
        string json,
        string description)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            ValidateElement(document.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidDataException(
                $"{description} is not valid strict JSON.");
        }

        static void ValidateElement(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            "Tracker-path JSON contains a duplicate member name.");
                    }

                    ValidateElement(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ValidateElement(item);
                }
            }
        }
    }

    private sealed class StoreDto
    {
        [JsonPropertyName("schema_version")]
        [JsonPropertyOrder(0)]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("observations")]
        [JsonPropertyOrder(1)]
        public required IReadOnlyList<ObservationDto?> Observations { get; init; }
    }

    private sealed class ObservationDto
    {
        [JsonPropertyName("tracker_serial")]
        [JsonPropertyOrder(0)]
        public required string TrackerSerial { get; init; }

        [JsonPropertyName("registered_device_path")]
        [JsonPropertyOrder(1)]
        public required string RegisteredDevicePath { get; init; }

        [JsonPropertyName("last_observed_utc")]
        [JsonPropertyOrder(2)]
        public required string LastObservedUtc { get; init; }

        [JsonPropertyName("path_change_history")]
        [JsonPropertyOrder(3)]
        public required IReadOnlyList<HistoryDto?> PathChangeHistory { get; init; }
    }

    private sealed class HistoryDto
    {
        [JsonPropertyName("prior_registered_device_path")]
        [JsonPropertyOrder(0)]
        public required string PriorRegisteredDevicePath { get; init; }

        [JsonPropertyName("prior_last_observed_utc")]
        [JsonPropertyOrder(1)]
        public required string PriorLastObservedUtc { get; init; }

        [JsonPropertyName("replacement_utc")]
        [JsonPropertyOrder(2)]
        public required string ReplacementUtc { get; init; }
    }

    private sealed class PendingDto
    {
        [JsonPropertyName("schema_version")]
        [JsonPropertyOrder(0)]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("affected_tracker_serials")]
        [JsonPropertyOrder(1)]
        public required IReadOnlyList<string> AffectedTrackerSerials { get; init; }
    }

    private sealed class FileTrackerPathObservationStorePersistence :
        ITrackerPathObservationStorePersistence
    {
        internal static FileTrackerPathObservationStorePersistence Instance { get; } =
            new();

        private FileTrackerPathObservationStorePersistence()
        {
        }

        public bool Exists(string path) => File.Exists(path);

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAtomic(string path, string contents) =>
            AtomicFileWriter.Write(path, contents);

        public void Delete(string path) => File.Delete(path);
    }
}
