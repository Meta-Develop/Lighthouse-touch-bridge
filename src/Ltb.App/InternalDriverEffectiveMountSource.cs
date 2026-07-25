using Ltb.Core;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>
/// Per-hand effective mount shared by the control plane and publication hot
/// loop. Updating creates one validated immutable snapshot; reading performs
/// one atomic reference load and never locks or allocates.
/// </summary>
internal sealed class InternalDriverEffectiveMountSource
{
    private Snapshot _current;

    public InternalDriverEffectiveMountSource(
        ProtocolHand hand,
        RigidTransform initialMount)
    {
        RequireHand(hand);
        RequireValid(initialMount);
        Hand = hand;
        _current = new Snapshot(initialMount, Generation: 0);
    }

    public ProtocolHand Hand { get; }

    public Snapshot Read() => Volatile.Read(ref _current);

    public Snapshot Update(RigidTransform mount)
    {
        RequireValid(mount);
        while (true)
        {
            var current = Volatile.Read(ref _current);
            var replacement = new Snapshot(
                mount,
                checked(current.Generation + 1));
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _current, replacement, current),
                    current))
            {
                return replacement;
            }
        }
    }

    internal sealed record Snapshot(
        RigidTransform TrackerFromController,
        long Generation);

    private static void RequireValid(RigidTransform mount)
    {
        if (!mount.IsValid)
        {
            throw new ArgumentException(
                "An effective tracker-from-controller mount must be a valid rigid transform.",
                nameof(mount));
        }
    }

    private static void RequireHand(ProtocolHand hand)
    {
        if (hand is not ProtocolHand.Left and not ProtocolHand.Right)
        {
            throw new ArgumentOutOfRangeException(nameof(hand));
        }
    }
}
