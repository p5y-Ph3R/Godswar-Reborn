namespace Godswar.Server.Networking.Secure;

internal class SecureTransportException : IOException
{
    public SecureTransportException(string message)
        : base(message)
    {
    }

    public SecureTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class SecureIngressQueueOverflowException :
    SecureTransportException
{
    public SecureIngressQueueOverflowException()
        : base("The secure legacy ingress queue exceeded its bounded admission deadline.")
    {
    }
}
