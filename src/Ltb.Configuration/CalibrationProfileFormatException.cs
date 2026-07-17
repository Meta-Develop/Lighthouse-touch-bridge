namespace Ltb.Configuration;

/// <summary>Stable categories for calibration profile deserialization failures.</summary>
public enum CalibrationProfileFormatReason
{
    UnsupportedSchemaVersion,
    MalformedJson,
    InvalidProfileData,
}

/// <summary>
/// A typed calibration profile or profile-store format failure. File-system
/// access failures remain separate I/O exceptions.
/// </summary>
public sealed class CalibrationProfileFormatException : FormatException
{
    public CalibrationProfileFormatException(
        CalibrationProfileFormatReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
    }

    public CalibrationProfileFormatReason Reason { get; }
}
