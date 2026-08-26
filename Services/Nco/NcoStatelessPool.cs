using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>
/// Executes ordinary, non-transactional RFC calls (the bulk of traffic —
/// everything reached via ISapConnectionPool.ExecuteAsync) directly against
/// NCo's own internal per-destination connection pool
/// (RfcConfigParameters.PoolSize/MaxPoolSize). No dedicated .NET thread, no
/// RfcSessionManager.BeginContext pinning: NCo's pool is already
/// thread-safe, so CreateFunction/Invoke on a destination leases and returns
/// a physical connection internally per call — any number of callers can run
/// concurrently straight from whatever thread is running (an ASP.NET
/// request thread, via Task.Run), bounded only by PoolSize/MaxPoolSize, not
/// by a fixed count of always-alive worker threads the way the COM-era
/// design (and this rebuild's first pass) required.
///
/// One destination is registered per configured SapNco:ServiceAccounts entry
/// (or a single one for the plain ServiceAccount when that list is empty),
/// so per-account credentials/pool sizing stay isolated exactly as
/// NcoConnectionPool's old per-worker account assignment did — just with no
/// dedicated thread behind each one now. Calls round-robin across them.
/// </summary>
internal sealed class NcoStatelessPool
{
    private readonly string[] _destinationNames;
    private readonly SapNcoOptions _options;
    private readonly ILogger _logger;
    private int _nextIndex = -1;

    public NcoStatelessPool(SapNcoOptions options, NcoDestinationRegistry registry, ILogger logger)
    {
        _options = options;
        _logger  = logger;

        var accounts = options.ServiceAccounts.Count > 0
            ? options.ServiceAccounts
            : new[] { options.ServiceAccount };

        _destinationNames = new string[accounts.Count];
        for (int i = 0; i < accounts.Count; i++)
        {
            _destinationNames[i] = $"POOL_{i}";
            registry.Register(_destinationNames[i], accounts[i]);
        }
    }

    public IReadOnlyList<string> DestinationNames => _destinationNames;

    public async Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken ct)
    {
        int index = (int)((uint)Interlocked.Increment(ref _nextIndex) % _destinationNames.Length);
        string name = _destinationNames[index];
        int identity = -(index + 1); // negative range reserved for the stateless pool, distinct from pinned-session slot ids

        try
        {
            return await Task.Run(() => ExecuteOnDestination(name, request, identity), ct).ConfigureAwait(false);
        }
        catch (SapConnectionException ex)
        {
            _logger.LogWarning(ex, "Stateless RFC '{Function}' failed on '{Destination}' — retrying once.",
                request.FunctionName, name);
            await Task.Delay(_options.ReconnectDelayMs, ct).ConfigureAwait(false);
            return await Task.Run(() => ExecuteOnDestination(name, request, identity), ct).ConfigureAwait(false);
        }
    }

    private static RfcResponse ExecuteOnDestination(string destinationName, RfcRequest request, int identity)
    {
        var destination = RfcDestinationManager.GetDestination(destinationName);
        return NcoRfcExecutor.Execute(destination, request, identity);
    }

    /// <summary>Sends a lightweight RFC_PING to every stateless-pool destination — keeps at least one pooled connection per destination warm against SAP's own idle-session timeout.</summary>
    public void PingAll()
    {
        foreach (var name in _destinationNames)
        {
            try
            {
                RfcDestinationManager.GetDestination(name).Ping();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Keep-alive ping failed for stateless SAP destination '{Destination}'.", name);
            }
        }
    }
}
