using System.Collections.Concurrent;
using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.Gui;
using Ltb.Gui.ViewModels;
using Ltb.Protocol;

namespace Ltb.Gui.Tests;

public sealed class AppMountAdjustmentPortTests
{
    [Fact]
    public async Task ConcreteAdapterMapsLiveApplyAndSaveToBoundAppControl()
    {
        var baseLeft = new RigidTransform(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
            new Vector3(0.1f, 0f, 0f));
        var session = new FakeAppSession(MountSnapshot(baseLeft));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);

        Assert.True(port.CurrentSnapshot.IsAvailable);
        Assert.Equal(0.1f, port.CurrentSnapshot.Left.BaseMount.TranslationMeters.X, 6);

        var trackerSide = new MountAdjustmentTransform(
            new Vector3(0.01f, 0f, 0f),
            Quaternion.Identity);
        var controllerSide = new MountAdjustmentTransform(
            new Vector3(0.2f, 0f, 0f),
            Quaternion.Identity);
        var live = await port.ApplyLiveAsync(new MountAdjustmentLiveApplyRequest(
            Revision: 8,
            MountAdjustmentHand.Left,
            new MountAdjustmentPair(trackerSide, controllerSide)));

        Assert.True(live.Succeeded, live.Diagnostic);
        Assert.Equal(8, live.AcknowledgedRevision);
        var applied = Assert.Single(session.ApplyRequests);
        Assert.Equal(ProtocolHand.Left, applied.Hand);
        Assert.Equal(new Vector3(0.01f, 0f, 0f), applied.Adjustment.TrackerSideAdjustment.TranslationMeters);
        Assert.Equal(new Vector3(0.2f, 0f, 0f), applied.Adjustment.ControllerSideAdjustment.TranslationMeters);
        Assert.Equal(
            0.11f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.X,
            5);
        Assert.Equal(
            0.2f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.Y,
            5);
        Assert.Equal(
            0f,
            live.Snapshot.Left.EffectiveMount.TranslationMeters.Z,
            5);

        var save = await port.SaveAsync(new MountAdjustmentSaveRequest(
            Revision: 9,
            live.Snapshot.Left.AppliedAdjustments,
            MountAdjustmentPair.Identity));

        Assert.True(save.Succeeded, save.Diagnostic);
        Assert.Equal(9, save.Snapshot.Revision);
        Assert.Equal(live.Snapshot.Left.AppliedAdjustments, save.Snapshot.Left.SavedAdjustments);
        Assert.Equal(1, session.SaveCount);
    }

    [Fact]
    public async Task ThrowingObserverDoesNotTurnPersistedSaveIntoFailure()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        MountAdjustmentSnapshot? laterObservation = null;
        port.SnapshotChanged += (_, _) =>
            throw new InvalidOperationException("scripted early GUI observer failure");
        port.SnapshotChanged += (_, snapshot) => laterObservation = snapshot;

        var result = await port.SaveAsync(new MountAdjustmentSaveRequest(
            Revision: 8,
            MountAdjustmentPair.Identity,
            MountAdjustmentPair.Identity));

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Equal(1, session.SaveCount);
        Assert.Equal(8, port.CurrentSnapshot.Revision);
        Assert.Equal(port.CurrentSnapshot, laterObservation);
        Assert.Equal(port.CurrentSnapshot, result.Snapshot);
    }

    [Fact]
    public void ConcreteAdapterMapsRestoreFailureAndExplicitRestoreClear()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        var updates = new List<MountAdjustmentSnapshot>();
        port.SnapshotChanged += (_, snapshot) => updates.Add(snapshot);

        session.PublishSession(InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "restore failed visibly",
                ["restore failed visibly"]),
        });

        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Failure,
            updates[^1].RestoreWarning.Kind);
        Assert.Contains("restore failed visibly", updates[^1].RestoreWarning.Message);

        session.PublishSession(InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.Restored,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "restored exactly",
                Array.Empty<string>()),
        });

        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Clear,
            updates[^1].RestoreWarning.Kind);
    }

    [Fact]
    public async Task ConcurrentMountAndSessionCallbacksPublishInAuthoritativeOrder()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        using var releaseFirstCallback = new ManualResetEventSlim(initialState: false);
        var firstCallbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<MountAdjustmentSnapshot>();
        var blocked = 0;
        port.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Revision == 8 &&
                Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
            {
                firstCallbackEntered.TrySetResult();
                if (!releaseFirstCallback.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("The ordered callback test was not released.");
                }
            }

            updates.Add(snapshot);
        };
        var adjusted = session.CurrentMountAdjustment with { Revision = 8 };
        var mountPublication = Task.Run(() => session.PublishMount(adjusted));
        await firstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var restoreFailed = InternalDriverSessionSnapshot.Initial with
        {
            TrackerNeutralization = new InternalDriverTrackerNeutralizationSnapshot(
                InternalDriverTrackerNeutralizationState.RestoreFailed,
                Array.Empty<InternalDriverTrackerPath>(),
                BackendSnapshotId: null,
                "concurrent restore failure",
                ["concurrent restore failure"]),
        };
        var sessionPublication = Task.Run(() => session.PublishSession(restoreFailed));
        await Task.Delay(25);
        Assert.False(sessionPublication.IsCompleted);

        releaseFirstCallback.Set();
        await Task.WhenAll(mountPublication, sessionPublication)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, updates.Count);
        Assert.Equal(
            MountAdjustmentRestoreWarningUpdateKind.Failure,
            updates[^1].RestoreWarning.Kind);
        Assert.Equal(port.CurrentSnapshot, updates[^1]);
    }

    [Fact]
    public async Task DelayedSaveAcknowledgementCannotOverwriteNewerTeardown()
    {
        var session = new FakeAppSession(MountSnapshot(RigidTransform.Identity));
        var port = new AppMountAdjustmentPort();
        port.Bind(session);
        var delayDispatch = false;
        var delayedDispatches = new ConcurrentQueue<Action>();
        using var viewModel = new MountAdjustmentViewModel(
            port,
            action =>
            {
                if (delayDispatch)
                {
                    delayedDispatches.Enqueue(action);
                }
                else
                {
                    action();
                }
            });
        viewModel.LeftHand.TrackerSide.PositionXMillimeters = 10d;
        Assert.True(viewModel.IsDirty);
        session.BlockNextSave();
        var staleSaveSnapshot = session.CurrentMountAdjustment;

        var save = viewModel.SaveAsync();
        await session.SaveEntered.WaitAsync(TimeSpan.FromSeconds(2));
        delayDispatch = true;
        session.PublishMount(session.CurrentMountAdjustment with
        {
            Revision = 9,
            IsAvailable = false,
        });

        session.CompleteBlockedSave(new InternalDriverMountAdjustmentResult(
            AcknowledgedRevision: 8,
            Succeeded: true,
            "stale save completed",
            staleSaveSnapshot));
        await save.WaitAsync(TimeSpan.FromSeconds(2));
        while (delayedDispatches.TryDequeue(out var dispatch))
        {
            dispatch();
        }

        Assert.Equal(9, port.CurrentSnapshot.Revision);
        Assert.False(port.CurrentSnapshot.IsAvailable);
        Assert.False(viewModel.IsAvailable);
    }

    private static InternalDriverMountAdjustmentSnapshot MountSnapshot(
        RigidTransform baseLeft)
    {
        var left = new InternalDriverMountAdjustmentHandSnapshot(
            baseLeft,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            baseLeft);
        var right = new InternalDriverMountAdjustmentHandSnapshot(
            RigidTransform.Identity,
            MountAdjustment.Identity,
            MountAdjustment.Identity,
            RigidTransform.Identity);
        return new InternalDriverMountAdjustmentSnapshot(7, true, left, right);
    }

    private sealed class FakeAppSession :
        IInternalDriverSession,
        IInternalDriverMountAdjustmentControl
    {
        private InternalDriverSessionSnapshot _session =
            InternalDriverSessionSnapshot.Initial;
        private InternalDriverMountAdjustmentSnapshot _mount;
        private TaskCompletionSource<InternalDriverMountAdjustmentResult>?
            _blockedSave;
        private TaskCompletionSource? _saveEntered;

        public FakeAppSession(InternalDriverMountAdjustmentSnapshot mount)
        {
            _mount = mount;
        }

        public event EventHandler<InternalDriverSessionSnapshot>? SnapshotChanged;

        public event EventHandler<InternalDriverMountAdjustmentSnapshot>?
            MountAdjustmentChanged;

        public List<(ProtocolHand Hand, MountAdjustment Adjustment)> ApplyRequests
        {
            get;
        } = [];

        public int SaveCount { get; private set; }

        public Task SaveEntered => _saveEntered?.Task ??
            throw new InvalidOperationException("No save is blocked.");

        public InternalDriverSessionSnapshot CurrentSnapshot => _session;

        public InternalDriverMountAdjustmentSnapshot CurrentMountAdjustment => _mount;

        public Task RunAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<InternalDriverMountAdjustmentResult> ApplyMountAdjustmentAsync(
            long revision,
            ProtocolHand hand,
            MountAdjustment adjustment,
            CancellationToken cancellationToken = default)
        {
            ApplyRequests.Add((hand, adjustment));
            var selected = hand == ProtocolHand.Left ? _mount.Left : _mount.Right;
            var updated = selected with
            {
                AppliedAdjustments = adjustment,
                EffectiveMount = adjustment.ApplyTo(selected.BaseMount),
            };
            _mount = hand == ProtocolHand.Left
                ? _mount with { Revision = revision, Left = updated }
                : _mount with { Revision = revision, Right = updated };
            MountAdjustmentChanged?.Invoke(this, _mount);
            return ValueTask.FromResult(new InternalDriverMountAdjustmentResult(
                revision,
                true,
                "applied",
                _mount));
        }

        public ValueTask<InternalDriverMountAdjustmentResult> SaveMountAdjustmentsAsync(
            long revision,
            MountAdjustment left,
            MountAdjustment right,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (_blockedSave is not null)
            {
                _saveEntered!.TrySetResult();
                return new ValueTask<InternalDriverMountAdjustmentResult>(
                    _blockedSave.Task);
            }

            _mount = _mount with
            {
                Revision = revision,
                Left = _mount.Left with
                {
                    AppliedAdjustments = left,
                    SavedAdjustments = left,
                    EffectiveMount = left.ApplyTo(_mount.Left.BaseMount),
                },
                Right = _mount.Right with
                {
                    AppliedAdjustments = right,
                    SavedAdjustments = right,
                    EffectiveMount = right.ApplyTo(_mount.Right.BaseMount),
                },
            };
            MountAdjustmentChanged?.Invoke(this, _mount);
            return ValueTask.FromResult(new InternalDriverMountAdjustmentResult(
                revision,
                true,
                "saved",
                _mount));
        }

        public void BlockNextSave()
        {
            _blockedSave =
                new TaskCompletionSource<InternalDriverMountAdjustmentResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            _saveEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void CompleteBlockedSave(InternalDriverMountAdjustmentResult result)
        {
            var completion = _blockedSave ??
                throw new InvalidOperationException("No save is blocked.");
            _blockedSave = null;
            _saveEntered = null;
            completion.TrySetResult(result);
        }

        public void PublishSession(InternalDriverSessionSnapshot snapshot)
        {
            _session = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }

        public void PublishMount(InternalDriverMountAdjustmentSnapshot snapshot)
        {
            _mount = snapshot;
            MountAdjustmentChanged?.Invoke(this, snapshot);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
