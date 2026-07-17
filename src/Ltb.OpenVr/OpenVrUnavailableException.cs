namespace Ltb.OpenVr;

public enum OpenVrUnavailableReason
{
    UnsupportedPlatform = 0,
    NativeLibraryUnavailable = 1,
    NativeEntryPointUnavailable = 2,
    NativeLibraryArchitectureMismatch = 3,
    RuntimeInitializationFailed = 4,
}

/// <summary>Bounded failure returned when a live OpenVR session cannot start.</summary>
public sealed class OpenVrUnavailableException : InvalidOperationException
{
    public OpenVrUnavailableException(
        OpenVrUnavailableReason reason,
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

    public OpenVrUnavailableReason Reason { get; }
}
