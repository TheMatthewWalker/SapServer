using SapServer.Configuration;
using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>Distinguishes an elevated-worker connect/disconnect control item from an ordinary RFC work item.</summary>
internal enum NcoControlKind
{
    ElevatedLogon,
    ElevatedLogoff,
}

/// <summary>
/// A single unit of work queued to an NcoWorker's dedicated thread. Same
/// two-shape design as the old SapWorkItem (COM path) — an ordinary RFC call
/// or an elevated connect/disconnect control item — both strictly serialized
/// on that worker's own thread so a pinned BeginContext/EndContext session
/// (see NcoWorker) is never touched from two threads at once.
/// </summary>
internal sealed class NcoWorkItem
{
    public NcoWorkItem(
        RfcRequest request,
        TaskCompletionSource<RfcResponse> tcs,
        CancellationToken cancellationToken)
    {
        IsControl         = false;
        Request           = request;
        Tcs               = tcs;
        CancellationToken = cancellationToken;
    }

    public NcoWorkItem(
        NcoControlKind controlKind,
        SapConnectionOptions? elevatedCreds,
        TaskCompletionSource<bool> controlTcs)
    {
        IsControl     = true;
        ControlKind   = controlKind;
        ElevatedCreds = elevatedCreds;
        ControlTcs    = controlTcs;
    }

    public bool IsControl { get; }

    // Populated when IsControl == false
    public RfcRequest?                        Request           { get; }
    public TaskCompletionSource<RfcResponse>? Tcs               { get; }
    public CancellationToken                  CancellationToken { get; }

    // Populated when IsControl == true
    public NcoControlKind              ControlKind   { get; }
    public SapConnectionOptions?       ElevatedCreds { get; }
    public TaskCompletionSource<bool>? ControlTcs    { get; }
}
