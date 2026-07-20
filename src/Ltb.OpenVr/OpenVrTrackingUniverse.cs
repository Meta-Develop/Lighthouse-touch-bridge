namespace Ltb.OpenVr;

/// <summary>
/// Selects the OpenVR coordinate frame in which a pose source returns device
/// poses. This contract is independent of the numeric values used by OpenVR.
/// </summary>
public enum OpenVrTrackingUniverse
{
    /// <summary>
    /// Poses are relative to SteamVR's user-configured standing origin. This
    /// remains the default for the legacy calibration, VMT, and
    /// <c>TrackingOverrides</c> paths.
    /// </summary>
    Standing = 0,

    /// <summary>
    /// Poses are in OpenVR raw driver space, without a Standing or Seated
    /// playspace transform. The first-party <c>driver_ltb</c> path uses this
    /// universe when sampling physical Lighthouse trackers and publishes the
    /// composed pose with an identity world-from-driver transform.
    /// </summary>
    RawAndUncalibrated = 1,
}
