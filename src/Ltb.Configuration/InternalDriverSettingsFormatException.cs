namespace Ltb.Configuration;

/// <summary>Stable settings deserialization failure categories.</summary>
public enum InternalDriverSettingsFormatReason
{
    UnsupportedSchemaVersion,
    MalformedJson,
    InvalidSettingsData,
}

/// <summary>A typed internal-driver settings format failure.</summary>
public sealed class InternalDriverSettingsFormatException : FormatException
{
    public InternalDriverSettingsFormatException(
        InternalDriverSettingsFormatReason reason,
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

    public InternalDriverSettingsFormatReason Reason { get; }
}
