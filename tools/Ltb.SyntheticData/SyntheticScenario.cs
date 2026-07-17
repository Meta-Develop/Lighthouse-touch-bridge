namespace Ltb.SyntheticData;

/// <summary>
/// Deterministic motion and measurement presets used by the offline calibration proof.
/// </summary>
public enum SyntheticScenario
{
    Clean,
    Noisy,
    Static,
    SingleAxisRotation,
    PureTranslation,
    TranslationDegenerate,
}
