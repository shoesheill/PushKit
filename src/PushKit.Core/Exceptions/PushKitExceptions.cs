namespace PushKit.Exceptions;

/// <summary>Base exception for PushKit infrastructure errors (config, auth, network). Protocol errors are returned as <c>PushResult</c> values.</summary>
public class PushKitException : Exception
{
    public PushKitException(string message) : base(message) { }
    public PushKitException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when PushKit configuration is invalid or incomplete.</summary>
public sealed class PushKitConfigurationException : PushKitException
{
    public PushKitConfigurationException(string message) : base(message) { }
}

/// <summary>Thrown when OAuth2 / JWT authentication fails (not a protocol-level rejection).</summary>
public sealed class PushKitAuthException : PushKitException
{
    public PushKitAuthException(string message, Exception? inner = null)
        : base(message, inner ?? new Exception(message)) { }
}

/// <summary>Thrown when the HTTP transport itself fails (network, DNS, timeout).</summary>
public sealed class PushKitTransportException : PushKitException
{
    public int? HttpStatusCode { get; }

    public PushKitTransportException(string message, int? httpStatusCode = null, Exception? inner = null)
        : base(message, inner ?? new Exception(message))
    {
        HttpStatusCode = httpStatusCode;
    }
}
