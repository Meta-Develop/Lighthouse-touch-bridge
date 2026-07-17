using System.Numerics;

namespace Ltb.Core;

/// <summary>
/// A right-handed rigid transform <c>T_parent_child</c> that maps coordinates
/// expressed in a child frame into its parent frame. Translation is measured in
/// meters and <see cref="Rotation"/> is a System.Numerics quaternion in XYZW
/// component order.
/// </summary>
public readonly record struct RigidTransform
{
    private const float MinimumQuaternionLengthSquared = 1e-12f;
    private const float UnitQuaternionTolerance = 1e-4f;

    /// <summary>
    /// Creates a transform and normalizes its rotation. Both quaternion signs
    /// are accepted because <c>q</c> and <c>-q</c> represent the same rotation;
    /// the normalized input sign is preserved so acquisition tests can retain
    /// raw runtime sign flips.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when a component is non-finite or the quaternion has no usable
    /// magnitude.
    /// </exception>
    public RigidTransform(Quaternion rotation, Vector3 translationMeters)
    {
        Rotation = Normalize(rotation);

        if (!IsFinite(translationMeters))
        {
            throw new ArgumentException("Transform translation must contain only finite values.", nameof(translationMeters));
        }

        TranslationMeters = translationMeters;
    }

    /// <summary>The normalized child-to-parent rotation.</summary>
    public Quaternion Rotation { get; }

    /// <summary>The child origin expressed in the parent frame, in meters.</summary>
    public Vector3 TranslationMeters { get; }

    /// <summary>The identity transform.</summary>
    public static RigidTransform Identity { get; } = new(Quaternion.Identity, Vector3.Zero);

    /// <summary>
    /// Indicates whether this value contains a finite, normalized rotation and
    /// finite translation. In particular, <c>default(RigidTransform)</c> is not
    /// a valid transform; use <see cref="Identity"/> instead.
    /// </summary>
    public bool IsValid =>
        IsFinite(Rotation) &&
        MathF.Abs(Rotation.LengthSquared() - 1f) <= UnitQuaternionTolerance &&
        IsFinite(TranslationMeters);

    /// <summary>
    /// Returns the inverse transform <c>T_child_parent</c>.
    /// </summary>
    public RigidTransform Inverse()
    {
        EnsureValid();

        var inverseRotation = Quaternion.Conjugate(Rotation);
        var inverseTranslation = Vector3.Transform(-TranslationMeters, inverseRotation);
        return new RigidTransform(inverseRotation, inverseTranslation);
    }

    /// <summary>
    /// Composes this <c>T_A_B</c> with <paramref name="childTransform"/>
    /// <c>T_B_C</c>, producing <c>T_A_C</c>.
    /// </summary>
    public RigidTransform Compose(RigidTransform childTransform) => this * childTransform;

    /// <summary>
    /// Maps a point from this transform's child frame into its parent frame.
    /// </summary>
    public Vector3 TransformPoint(Vector3 pointInChildFrame)
    {
        EnsureValid();

        if (!IsFinite(pointInChildFrame))
        {
            throw new ArgumentException("Point must contain only finite values.", nameof(pointInChildFrame));
        }

        return Vector3.Transform(pointInChildFrame, Rotation) + TranslationMeters;
    }

    /// <summary>
    /// Composes <paramref name="parentFromChild"/> <c>T_A_B</c> and
    /// <paramref name="childFromGrandchild"/> <c>T_B_C</c> to produce
    /// <c>T_A_C</c>. Composition order is intentionally not commutative.
    /// </summary>
    public static RigidTransform operator *(
        RigidTransform parentFromChild,
        RigidTransform childFromGrandchild)
    {
        parentFromChild.EnsureValid();
        childFromGrandchild.EnsureValid();

        var rotation = parentFromChild.Rotation * childFromGrandchild.Rotation;
        var translation =
            Vector3.Transform(childFromGrandchild.TranslationMeters, parentFromChild.Rotation) +
            parentFromChild.TranslationMeters;

        return new RigidTransform(rotation, translation);
    }

    /// <summary>Deconstructs this value into rotation and translation.</summary>
    public void Deconstruct(out Quaternion rotation, out Vector3 translationMeters)
    {
        rotation = Rotation;
        translationMeters = TranslationMeters;
    }

    private void EnsureValid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                "RigidTransform is invalid. Construct it with a finite non-zero quaternion and finite translation; do not use default(RigidTransform).");
        }
    }

    private static Quaternion Normalize(Quaternion rotation)
    {
        if (!IsFinite(rotation))
        {
            throw new ArgumentException("Transform rotation must contain only finite values.", nameof(rotation));
        }

        var lengthSquared = rotation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < MinimumQuaternionLengthSquared)
        {
            throw new ArgumentException("Transform rotation quaternion must have non-zero magnitude.", nameof(rotation));
        }

        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        return new Quaternion(
            rotation.X * inverseLength,
            rotation.Y * inverseLength,
            rotation.Z * inverseLength,
            rotation.W * inverseLength);
    }

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
