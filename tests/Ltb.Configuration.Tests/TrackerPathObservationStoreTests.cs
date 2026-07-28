using System.Text;
using System.Text.Json;

namespace Ltb.Configuration.Tests;

public sealed class TrackerPathObservationStoreTests
{
    private const string SerialA = "LHR-A";
    private const string SerialB = "LHR-B";
    private const string PathA = "/devices/custom_driver/device-a";
    private const string PathB = "/devices/lighthouse/device-b";

    [Fact]
    public void FirstWriteIsCanonicalSortedUtf8WithoutBomAndNewlineTerminated()
    {
        var root = TemporaryRootPath();
        var storePath = Path.Combine(root, "private", "tracker-paths.json");
        try
        {
            var store = new TrackerPathObservationStore(storePath);

            var recorded = store.RecordObservations(
            [
                Candidate(" lhr-b ", PathB, 1),
                Candidate("lhr-a", PathA, 1),
            ]);

            var bytes = File.ReadAllBytes(storePath);
            var json = Encoding.UTF8.GetString(bytes);
            using var document = JsonDocument.Parse(json);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.EndsWith("\n", json, StringComparison.Ordinal);
            Assert.Equal(
                ["schema_version", "observations"],
                document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name));
            Assert.Equal(
                [SerialA, SerialB],
                document.RootElement
                    .GetProperty("observations")
                    .EnumerateArray()
                    .Select(observation =>
                        observation.GetProperty("tracker_serial").GetString()));
            Assert.All(
                document.RootElement
                    .GetProperty("observations")
                    .EnumerateArray(),
                observation => Assert.Equal(
                    [
                        "tracker_serial",
                        "registered_device_path",
                        "last_observed_utc",
                        "path_change_history",
                    ],
                    observation
                        .EnumerateObject()
                        .Select(property => property.Name)));
            Assert.Equal([SerialA, SerialB], recorded.Select(item => item.TrackerSerial));
            Assert.Equal(PathA, store.TryLookup(" lhr-a ")!.RegisteredDevicePath);
            Assert.Equal(PathB, store.TryLookup("LHR-B")!.RegisteredDevicePath);
            Assert.Null(store.TryLookup("not-present"));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(storePath)!,
                ".tracker-paths.json.*.tmp"));
        }
        finally
        {
            DeleteIfPresent(root);
        }
    }

    [Fact]
    public void MissingMainStoreIsAnImmutableEmptySnapshot()
    {
        var store = new TrackerPathObservationStore(
            Path.Combine(TemporaryRootPath(), "missing", "tracker-paths.json"));

        var loaded = store.LoadAll();

        Assert.Empty(loaded);
        Assert.Null(store.TryLookup("LHR-NOT-PRESENT"));
    }

    [Theory]
    [InlineData("A\u0001")]
    [InlineData("A B")]
    [InlineData("\tA")]
    public void SerialRejectsControlOrRemainingWhitespace(string serial)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new TrackerPathObservationCandidate(serial, PathA, At(1)));
    }

    [Fact]
    public void SerialLengthBoundsAreUtf16CodeUnits()
    {
        Assert.Equal(
            256,
            new TrackerPathObservationCandidate(
                new string('a', 256),
                PathA,
                At(1)).TrackerSerial.Length);
        Assert.ThrowsAny<ArgumentException>(() =>
            new TrackerPathObservationCandidate(
                new string('a', 257),
                PathA,
                At(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/devices/driver")]
    [InlineData("/devices//device")]
    [InlineData("/devices/driver/")]
    [InlineData("/devices/./device")]
    [InlineData("/devices/driver/..")]
    [InlineData("/devices/driver/device/extra")]
    [InlineData("/Devices/driver/device")]
    [InlineData("/devices/driver/device value")]
    [InlineData("/devices/driver/device\t")]
    public void RegisteredPathRejectsNoncanonicalShapes(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new TrackerPathObservationCandidate(SerialA, path, At(1)));
    }

    [Fact]
    public void RegisteredPathAcceptsGeneralExactDriverAndDeviceSegments()
    {
        const string exact = "/devices/Custom.Driver/device%2Fkey";

        var candidate = new TrackerPathObservationCandidate(
            " tracker-x ",
            exact,
            At(1));

        Assert.Equal("TRACKER-X", candidate.TrackerSerial);
        Assert.Equal(exact, candidate.RegisteredDevicePath);
    }

    [Fact]
    public void CandidateRequiresZeroUtcOffset()
    {
        var nonUtc = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.FromHours(9));

        Assert.Throws<ArgumentException>(() =>
            new TrackerPathObservationCandidate(SerialA, PathA, nonUtc));
    }

    [Fact]
    public void SamePathNewerUtcRefreshesWithoutHistory()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(Candidate(SerialA, PathA, 1));

            var refreshed = fixture.Store.RecordObservation(
                Candidate(SerialA, PathA, 2));

            Assert.Equal(At(2), refreshed.LastObservedUtc);
            Assert.Empty(refreshed.PathChangeHistory);
            Assert.Equal(PathA, fixture.Store.TryLookup(SerialA)!.RegisteredDevicePath);
            Assert.False(File.Exists(fixture.StorePath + ".path-change-pending"));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void EqualOrRegressingUtcRejectsWithoutAWrite()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(Candidate(SerialA, PathA, 2));
            var before = File.ReadAllBytes(fixture.StorePath);

            Assert.Throws<InvalidOperationException>(() =>
                fixture.Store.RecordObservation(Candidate(SerialA, PathA, 2)));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Store.RecordObservation(Candidate(SerialA, PathB, 1)));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
            Assert.False(File.Exists(fixture.StorePath + ".path-change-pending"));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void ChangedPathExposesOnlyNewCurrentAndBoundsOldestFirstHistory()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(
                Candidate(SerialA, "/devices/driver/device-0", 1));
            for (var index = 1; index <= 10; index++)
            {
                fixture.Store.RecordObservation(
                    Candidate(
                        SerialA,
                        $"/devices/driver/device-{index}",
                        index + 1));
            }

            var current = fixture.Store.TryLookup(SerialA)!;

            Assert.Equal("/devices/driver/device-10", current.RegisteredDevicePath);
            Assert.Equal(
                8,
                current.PathChangeHistory.Count);
            Assert.Equal(
                "/devices/driver/device-2",
                current.PathChangeHistory[0].PriorRegisteredDevicePath);
            Assert.Equal(
                "/devices/driver/device-9",
                current.PathChangeHistory[^1].PriorRegisteredDevicePath);
            Assert.DoesNotContain(
                fixture.Store.LoadAll(),
                observation =>
                    observation.RegisteredDevicePath ==
                    "/devices/driver/device-0");
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void NonAdjacentPathReuseRecordsAndReloadsExactMeasuredTransitions()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(Candidate(SerialA, PathA, 1));
            fixture.Store.RecordObservation(Candidate(SerialA, PathB, 2));

            var returned = fixture.Store.RecordObservation(
                Candidate(SerialA, PathA, 3));
            var reloaded = new TrackerPathObservationStore(fixture.StorePath)
                .TryLookup(SerialA)!;

            Assert.Equal(PathA, returned.RegisteredDevicePath);
            Assert.Equal(PathA, reloaded.RegisteredDevicePath);
            Assert.Equal(
                [PathA, PathB],
                reloaded.PathChangeHistory.Select(
                    entry => entry.PriorRegisteredDevicePath));
            Assert.Equal(
                [At(2), At(3)],
                reloaded.PathChangeHistory.Select(
                    entry => entry.ReplacementUtc));
            Assert.False(File.Exists(fixture.StorePath + ".path-change-pending"));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void DuplicateBatchEvidenceAndFinalCurrentPathsRejectAtomically()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(Candidate(SerialA, PathA, 1));
            var before = File.ReadAllBytes(fixture.StorePath);

            Assert.Throws<ArgumentException>(() =>
                fixture.Store.RecordObservations(
                [
                    Candidate("lhr-b", PathB, 2),
                    Candidate(" LHR-B ", "/devices/driver/other", 3),
                ]));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
            Assert.Throws<ArgumentException>(() =>
                fixture.Store.RecordObservations(
                [
                    Candidate(SerialB, PathB, 2),
                    Candidate("LHR-C", PathB, 2),
                ]));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
            Assert.Throws<InvalidDataException>(() =>
                fixture.Store.RecordObservation(
                    Candidate(SerialB, PathA, 2)));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void ObservationCountBoundRejectsWithoutMutation()
    {
        var fixture = CreateStore();
        try
        {
            var maximum = Enumerable
                .Range(0, TrackerPathObservationSchema.MaximumObservations)
                .Select(index => Candidate(
                    $"SERIAL-{index:D2}",
                    $"/devices/driver/device-{index:D2}",
                    1))
                .ToArray();
            fixture.Store.RecordObservations(maximum);
            var before = File.ReadAllBytes(fixture.StorePath);

            Assert.Throws<InvalidDataException>(() =>
                fixture.Store.RecordObservation(
                    Candidate(
                        "SERIAL-OVERFLOW",
                        "/devices/driver/device-overflow",
                        2)));
            Assert.Equal(before, File.ReadAllBytes(fixture.StorePath));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidPersistedStores))]
    public void StrictStoreRejectsMalformedAmbiguousOrInvalidPersistedData(
        string json)
    {
        var fixture = CreateStore();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.StorePath)!);
            File.WriteAllText(fixture.StorePath, json);

            var exception = Assert.Throws<InvalidDataException>(() =>
                fixture.Store.LoadAll());

            Assert.DoesNotContain(SerialA, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(PathA, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void OversizedInputFailsBeforeJsonParsing()
    {
        var fixture = CreateStore();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.StorePath)!);
            File.WriteAllBytes(
                fixture.StorePath,
                new byte[TrackerPathObservationSchema.MaximumSerializedBytes + 1]);

            Assert.Throws<InvalidDataException>(() => fixture.Store.LoadAll());
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void ExistingPendingGuardBlocksPublicLoadAndLookup()
    {
        var fixture = CreateStore();
        try
        {
            fixture.Store.RecordObservation(Candidate(SerialA, PathA, 1));
            File.WriteAllText(
                fixture.StorePath + ".path-change-pending",
                """
                {
                  "schema_version": 1,
                  "affected_tracker_serials": [
                    "LHR-A"
                  ]
                }

                """);

            Assert.Throws<InvalidDataException>(() => fixture.Store.LoadAll());
            Assert.Throws<InvalidDataException>(() =>
                fixture.Store.TryLookup(SerialA));
        }
        finally
        {
            DeleteIfPresent(fixture.Root);
        }
    }

    [Fact]
    public void ChangedPathMainWriteFailureRetainsGuardAndLaterLiveBatchReconciles()
    {
        var path = Path.Combine(TemporaryRootPath(), "tracker-paths.json");
        var persistence = new TestPersistence();
        var store = new TrackerPathObservationStore(path, persistence);
        store.RecordObservation(Candidate(SerialA, PathA, 1));
        persistence.FailNextMainWritePath = path;

        Assert.Throws<IOException>(() =>
            store.RecordObservation(Candidate(SerialA, PathB, 2)));
        Assert.True(persistence.Exists(path + ".path-change-pending"));
        Assert.Throws<InvalidDataException>(() => store.TryLookup(SerialA));

        var reconciled = store.RecordObservation(
            Candidate(SerialA, PathB, 3));

        Assert.Equal(PathB, reconciled.RegisteredDevicePath);
        Assert.False(persistence.Exists(path + ".path-change-pending"));
        Assert.Equal(PathB, store.TryLookup(SerialA)!.RegisteredDevicePath);
        Assert.Equal(3, persistence.MainWriteCount);
    }

    [Fact]
    public void GuardRemovalFailureKeepsNewEvidenceUnavailableUntilNewerRefresh()
    {
        var path = Path.Combine(TemporaryRootPath(), "tracker-paths.json");
        var persistence = new TestPersistence();
        var store = new TrackerPathObservationStore(path, persistence);
        store.RecordObservation(Candidate(SerialA, PathA, 1));
        persistence.FailNextDelete = true;

        Assert.Throws<IOException>(() =>
            store.RecordObservation(Candidate(SerialA, PathB, 2)));
        Assert.True(persistence.Exists(path + ".path-change-pending"));
        Assert.Throws<InvalidDataException>(() => store.LoadAll());

        var reconciled = store.RecordObservation(
            Candidate(SerialA, PathB, 3));

        Assert.Equal(At(3), reconciled.LastObservedUtc);
        Assert.False(persistence.Exists(path + ".path-change-pending"));
        Assert.Single(reconciled.PathChangeHistory);
    }

    [Fact]
    public void ReconciliationRequiresEveryAffectedSerialWithNewerEvidence()
    {
        var path = Path.Combine(TemporaryRootPath(), "tracker-paths.json");
        var persistence = new TestPersistence();
        var store = new TrackerPathObservationStore(path, persistence);
        store.RecordObservations(
        [
            Candidate(SerialA, PathA, 1),
            Candidate(SerialB, PathB, 1),
        ]);
        persistence.FailNextMainWritePath = path;
        Assert.Throws<IOException>(() =>
            store.RecordObservations(
            [
                Candidate(SerialA, "/devices/driver/new-a", 2),
                Candidate(SerialB, "/devices/driver/new-b", 2),
            ]));
        var writesBefore = persistence.MainWriteCount;

        Assert.Throws<InvalidOperationException>(() =>
            store.RecordObservation(
                Candidate(SerialA, "/devices/driver/new-a", 3)));

        Assert.Equal(writesBefore, persistence.MainWriteCount);
        Assert.True(persistence.Exists(path + ".path-change-pending"));
    }

    [Fact]
    public void ModelsAndExceptionsRedactExactIdentityValues()
    {
        const string secretSerial = "UNIQUE-PRIVATE-SERIAL";
        const string secretPath = "/devices/private_driver/private_device";
        var candidate = new TrackerPathObservationCandidate(
            secretSerial,
            secretPath,
            At(1));
        var entry = new TrackerPathObservationHistoryEntry(
            secretPath,
            At(1),
            At(2));
        var current = new TrackerPathObservation(
            secretSerial,
            "/devices/private_driver/replacement",
            At(2),
            [entry]);
        var store = new TrackerPathObservationStore(
            Path.Combine(TemporaryRootPath(), "tracker-paths.json"));

        Assert.DoesNotContain(secretSerial, candidate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, candidate.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretSerial, current.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secretPath, current.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tracker-paths.json", store.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidPersistedStores => new()
    {
        "{",
        "null",
        """{"schema_version":2,"observations":[]}""",
        """{"schema_version":1,"observations":[],"unknown":true}""",
        """{"schema_version":1,"schema_version":1,"observations":[]}""",
        """{"schema_version":1,"observations":null}""",
        """{"schema_version":1,"observations":[null]}""",
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "LHR-A",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
              "path_change_history": [],
              "unknown": true
            }
          ]
        }
        """,
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "lhr-a",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
              "path_change_history": []
            }
          ]
        }
        """,
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "LHR-A",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:01Z",
              "path_change_history": []
            }
          ]
        }
        """,
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "LHR-A",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
              "path_change_history": []
            },
            {
              "tracker_serial": "LHR-B",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:01.0000000Z",
              "path_change_history": []
            }
          ]
        }
        """,
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "LHR-A",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:03.0000000Z",
              "path_change_history": [
                {
                  "prior_registered_device_path": "/devices/custom_driver/device-b",
                  "prior_last_observed_utc": "2026-07-28T00:00:02.0000000Z",
                  "replacement_utc": "2026-07-28T00:00:01.0000000Z"
                }
              ]
            }
          ]
        }
        """,
        """
        {
          "schema_version": 1,
          "observations": [
            {
              "tracker_serial": "LHR-A",
              "registered_device_path": "/devices/custom_driver/device-a",
              "last_observed_utc": "2026-07-28T00:00:03.0000000Z",
              "path_change_history": [
                {
                  "prior_registered_device_path": "/devices/custom_driver/device-a",
                  "prior_last_observed_utc": "2026-07-28T00:00:01.0000000Z",
                  "replacement_utc": "2026-07-28T00:00:02.0000000Z"
                }
              ]
            }
          ]
        }
        """,
    };

    private static TrackerPathObservationCandidate Candidate(
        string serial,
        string path,
        int second) =>
        new(serial, path, At(second));

    private static DateTimeOffset At(int second) =>
        new(2026, 7, 28, 0, 0, second, TimeSpan.Zero);

    private static StoreFixture CreateStore()
    {
        var root = TemporaryRootPath();
        var path = Path.Combine(root, "private", "tracker-paths.json");
        return new StoreFixture(
            root,
            path,
            new TrackerPathObservationStore(path));
    }

    private static string TemporaryRootPath() => Path.Combine(
        Path.GetTempPath(),
        $"ltb-tracker-path-observations-{Guid.NewGuid():N}");

    private static void DeleteIfPresent(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record StoreFixture(
        string Root,
        string StorePath,
        TrackerPathObservationStore Store);

    private sealed class TestPersistence :
        ITrackerPathObservationStorePersistence
    {
        private readonly Dictionary<string, byte[]> files =
            new(StringComparer.Ordinal);

        public string? FailNextMainWritePath { get; set; }

        public bool FailNextDelete { get; set; }

        public int MainWriteCount { get; private set; }

        public bool Exists(string path) => files.ContainsKey(path);

        public byte[] ReadAllBytes(string path) =>
            files.TryGetValue(path, out var contents)
                ? contents.ToArray()
                : throw new FileNotFoundException();

        public void WriteAtomic(string path, string contents)
        {
            if (path.EndsWith(
                    ".path-change-pending",
                    StringComparison.Ordinal))
            {
                files[path] = Encoding.UTF8.GetBytes(contents);
                return;
            }

            MainWriteCount++;
            if (string.Equals(
                    FailNextMainWritePath,
                    path,
                    StringComparison.Ordinal))
            {
                FailNextMainWritePath = null;
                throw new IOException("Injected main write failure.");
            }

            files[path] = Encoding.UTF8.GetBytes(contents);
        }

        public void Delete(string path)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                throw new IOException("Injected guard delete failure.");
            }

            files.Remove(path);
        }
    }
}
