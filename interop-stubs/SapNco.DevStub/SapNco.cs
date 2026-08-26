// DEV/CI-ONLY STUB — NOT THE REAL SAP NCO ASSEMBLIES.
//
// The SAP NCo rebuild spike (Services/Nco/*, see CLAUDE.md's "SAP NCo Spike"
// section) references the SAP.Middleware.Connector namespace normally shipped
// as sapnco.dll + sapnco_utils.dll, downloaded from the SAP Support Portal
// under a licensed S-user — see libs/README (or ask about "Place the SAP NCo
// assemblies"). Those files are license-specific and deliberately not
// checked into source control, exactly like libs/Interop.SAPFunctions64.dll.
//
// Any environment without them — a fresh dev machine that hasn't downloaded
// NCo yet, or this sandbox, which can never have a licensed SAP NCo install —
// cannot compile SapServer.csproj at all without some stand-in for these
// types. This supplies just enough of SAP.Middleware.Connector's shape for
// Services/Nco/*.cs to compile — nothing in it does anything real, and every
// member throws if actually invoked.
//
// IMPORTANT: this shape is written from memory of the public SAP NCo 3.0/3.1
// API (RfcDestinationManager / IDestinationConfiguration / RfcConfigParameters
// / RfcDestination / RfcRepository / IRfcFunction / IRfcTable / IRfcStructure),
// NOT verified against the real assemblies (no real SAP NCo install exists in
// this environment to check it against). When the real sapnco.dll/
// sapnco_utils.dll are dropped into libs/ (see SapServer.csproj's
// Condition="Exists(...)" — the real DLLs always win when present), expect to
// need small signature fixes in Services/Nco/*.cs if any of this drifted from
// the actual API. Keep that code isolated behind INcoRfcService specifically
// so those fixes stay contained to one file.

namespace SAP.Middleware.Connector;

public sealed class RfcConfigParameters : System.Collections.Specialized.NameValueCollection
{
    public const string Name = "NAME";
    public const string AppServerHost = "ASHOST";
    public const string SystemNumber = "SYSNR";
    public const string User = "USER";
    public const string Password = "PASSWD";
    public const string Client = "CLIENT";
    public const string Language = "LANG";
    public const string PoolSize = "POOL_SIZE";
    public const string MaxPoolSize = "MAX_POOL_SIZE";
    public const string IdleTimeout = "IDLE_TIMEOUT";
    public const string SystemID = "SYSID";
    public const string MessageServerHost = "MSHOST";
    public const string Group = "GROUP";
}

public interface IDestinationConfiguration
{
    RfcConfigParameters GetParameters(string destinationName);
    bool ChangeEventsSupported();
    event RfcDestinationManager.ConfigurationChangeHandler? ConfigurationChanged;
}

public static class RfcDestinationManager
{
    public delegate void ConfigurationChangeHandler(ref RfcDestinationManager.EventArgs e);

    public sealed class EventArgs
    {
        public string? DestinationName { get; set; }
    }

    public static void RegisterDestinationConfiguration(IDestinationConfiguration configuration) =>
        throw NotSupported();

    public static RfcDestination GetDestination(string name) => throw NotSupported();

    private static NotSupportedException NotSupported() => new(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");
}

public class RfcDestination
{
    public string Name { get; init; } = string.Empty;
    public RfcRepository Repository => throw new NotSupportedException(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");
    public void Ping() => throw new NotSupportedException(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");
}

/// <summary>
/// Pins the calling thread to one physical pooled connection for a
/// destination between BeginContext/EndContext — required for a stateful
/// multi-call sequence (e.g. a create-BAPI followed by
/// BAPI_TRANSACTION_COMMIT/ROLLBACK) to land on the same SAP session's LUW.
/// Without this, each Invoke() may be served by any pooled connection, and a
/// commit could silently apply to the wrong session. See NcoWorker, which
/// calls BeginContext once for its whole lifetime (mirroring how SapStaWorker
/// held one persistent COM session for its lifetime) rather than per-call.
/// </summary>
public static class RfcSessionManager
{
    public static void BeginContext(RfcDestination destination) => throw new NotSupportedException(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");

    public static void EndContext(RfcDestination destination) => throw new NotSupportedException(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");
}

public class RfcRepository
{
    public IRfcFunction CreateFunction(string name) => throw new NotSupportedException(
        "SapNco.DevStub — no real SAP NCo connection is available in this environment.");
}

public interface IRfcFunction
{
    void SetValue(string name, object value);
    object GetValue(string name);
    string GetString(string name);
    IRfcTable GetTable(string name);
    IRfcStructure GetStructure(string name);
    void Invoke(RfcDestination destination);
}

public interface IRfcTable
{
    int RowCount { get; }
    IRfcStructure this[int index] { get; }
    void Clear();
    IRfcStructure Append();
}

public interface IRfcStructure
{
    void SetValue(string name, object value);
    object GetValue(string name);
    string GetString(string name);
}

public class RfcBaseException : Exception
{
    public RfcBaseException() { }
    public RfcBaseException(string message) : base(message) { }
    public RfcBaseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Transport-level failure — connection lost, host unreachable, etc.</summary>
public class RfcCommunicationException : RfcBaseException
{
    public RfcCommunicationException(string message) : base(message) { }
}

/// <summary>The ABAP runtime itself failed (dump, short dump) during the call.</summary>
public class RfcAbapRuntimeException : RfcBaseException
{
    public RfcAbapRuntimeException(string message) : base(message) { }
}

/// <summary>A business-level ABAP exception explicitly raised by the function module.</summary>
public class RfcAbapBaseException : RfcBaseException
{
    public string Key { get; init; } = string.Empty;
    public RfcAbapBaseException(string key, string message) : base(message) => Key = key;
}

/// <summary>The destination/connection is not in a state that allows this operation.</summary>
public class RfcInvalidStateException : RfcBaseException
{
    public RfcInvalidStateException(string message) : base(message) { }
}
