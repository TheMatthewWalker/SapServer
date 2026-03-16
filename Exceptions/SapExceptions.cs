namespace SapServer.Exceptions;

/// <summary>Base class for all SAP-related exceptions.</summary>
public class SapException : Exception
{
    protected SapException(string message) : base(message) { }
    protected SapException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a connection to SAP cannot be established or has been lost.</summary>
public sealed class SapConnectionException : SapException
{
    public int SlotId { get; }

    public SapConnectionException(int slotId, string message)
        : base(message) => SlotId = slotId;

    public SapConnectionException(int slotId, string message, Exception inner)
        : base(message, inner) => SlotId = slotId;
}

/// <summary>Thrown when an RFC function call fails at the SAP level.</summary>
public sealed class SapExecutionException : SapException
{
    public string FunctionName { get; }

    /// <summary>The message returned in SAP's RETURN/BAPIRETURN table, if available.</summary>
    public string? SapMessage { get; }

    public SapExecutionException(string functionName, string message, string? sapMessage = null)
        : base(message)
    {
        FunctionName = functionName;
        SapMessage   = sapMessage;
    }
}

/// <summary>Thrown when all pool workers are at capacity and cannot accept new work.</summary>
public sealed class PoolExhaustedException : SapException
{
    public PoolExhaustedException(string message) : base(message) { }
}

/// <summary>Thrown when the authenticated user lacks permission to call the requested RFC function.</summary>
public sealed class SapPermissionException : SapException
{
    public string FunctionName { get; }

    public SapPermissionException(string functionName)
        : base($"Permission denied for RFC function '{functionName}'.")
        => FunctionName = functionName;
}
