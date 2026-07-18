namespace Ltb.OpenVr;

/// <summary>
/// One SteamVR pose override. The discovered pose-source device path is the
/// JSON property name and the original Touch semantic hand path is its value.
/// </summary>
public sealed record TrackingOverrideBinding
{
    public const string LeftHandPath = "/user/hand/left";
    public const string RightHandPath = "/user/hand/right";

    public TrackingOverrideBinding(
        string poseSourceDevicePath,
        string semanticHandPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(poseSourceDevicePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticHandPath);

        if (poseSourceDevicePath.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            throw new ArgumentException(
                "Pose-source device path must not contain control or whitespace characters.",
                nameof(poseSourceDevicePath));
        }

        const string devicePrefix = "/devices/";
        var devicePathSegments = poseSourceDevicePath.Split('/');
        if (!poseSourceDevicePath.StartsWith(devicePrefix, StringComparison.Ordinal) ||
            poseSourceDevicePath.EndsWith('/') ||
            devicePathSegments.Length < 4 ||
            devicePathSegments.Skip(1).Any(segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Pose-source path must use the OpenVR '/devices/<driver>/<device>' shape.",
                nameof(poseSourceDevicePath));
        }

        if (semanticHandPath is not (LeftHandPath or RightHandPath))
        {
            throw new ArgumentException(
                $"Semantic hand path must be '{LeftHandPath}' or '{RightHandPath}'.",
                nameof(semanticHandPath));
        }

        if (string.Equals(
                poseSourceDevicePath,
                semanticHandPath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The pose-source device path must be distinct from the semantic hand path.",
                nameof(poseSourceDevicePath));
        }

        PoseSourceDevicePath = poseSourceDevicePath;
        SemanticHandPath = semanticHandPath;
    }

    /// <summary>
    /// OpenVR-shaped path supplied by the caller. Shape validation does not
    /// prove provenance; the coordinator must supply the actual stable path
    /// reported by device discovery rather than an assumed example or index.
    /// </summary>
    public string PoseSourceDevicePath { get; }

    public string SemanticHandPath { get; }
}
