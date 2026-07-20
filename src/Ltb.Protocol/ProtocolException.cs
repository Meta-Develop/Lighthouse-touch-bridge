namespace Ltb.Protocol;

public sealed class ProtocolException : FormatException
{
    public ProtocolException(string message)
        : base(message)
    {
    }
}
