using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>
/// A single RFC call queued to a pinned NcoWorker session's dedicated
/// thread, so a BeginContext/EndContext session (see NcoWorker) is never
/// touched from two threads at once. Connect/disconnect are no longer
/// queued control items — a pinned session connects once at construction
/// (on its own thread, before the queue loop starts accepting work) and
/// disconnects once when its thread exits, so this only ever needs to
/// represent an ordinary RFC call.
/// </summary>
internal sealed class NcoWorkItem
{
    public NcoWorkItem(
        RfcRequest request,
        TaskCompletionSource<RfcResponse> tcs,
        CancellationToken cancellationToken)
    {
        Request           = request;
        Tcs               = tcs;
        CancellationToken = cancellationToken;
    }

    public RfcRequest                        Request           { get; }
    public TaskCompletionSource<RfcResponse> Tcs               { get; }
    public CancellationToken                 CancellationToken { get; }
}
