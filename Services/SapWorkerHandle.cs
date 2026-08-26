using SapServer.Services.Nco;

namespace SapServer.Services;

/// <summary>
/// Opaque handle to one NcoWorker's dedicated pinned connection, returned by
/// ISapConnectionPool.AcquireWorker/AcquireElevatedWorkerAsync. Kept in this
/// namespace (not SapServer.Services.Nco) so ISapConnectionPool.cs — which
/// lives in the sibling SapServer.Services.Interfaces namespace nested under
/// this one — resolves the bare "SapWorkerHandle" name via C#'s implicit
/// outward namespace lookup, exactly as it did when this type lived at the
/// bottom of the old (COM-era) SapConnectionPool.cs.
/// </summary>
public sealed class SapWorkerHandle
{
    internal NcoWorker Worker { get; }

    internal SapWorkerHandle(NcoWorker worker)
    {
        Worker = worker;
    }
}
