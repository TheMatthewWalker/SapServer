using SapServer.Configuration;
using SapServer.Models;

namespace SapServer.Services;

/// <summary>Distinguishes an elevated-worker login/logout control item from an ordinary RFC work item.</summary>
internal enum SapControlKind
{
    ElevatedLogon,
    ElevatedLogoff,
}

/// <summary>
/// A single unit of work queued to an STA worker thread.
///
/// Two shapes share this type so both travel through the same
/// per-worker BlockingCollection and are strictly serialized on that
/// worker's STA thread:
///   - An ordinary RFC work item (<see cref="IsControl"/> == false) — the
///     TaskCompletionSource bridges the STA thread's RFC result back to the
///     awaiting HTTP thread, same as always.
///   - A control item (<see cref="IsControl"/> == true) — used only for
///     elevated workers, to log a specific user's SAP credentials in or out
///     on that worker's STA thread without going through ExecuteRfc. See
///     SapStaWorker.LogonElevatedAsync/LogoffElevatedAsync.
/// </summary>
internal sealed class SapWorkItem
{
    public SapWorkItem(
        RfcRequest request,
        TaskCompletionSource<RfcResponse> tcs,
        CancellationToken cancellationToken)
    {
        IsControl         = false;
        Request           = request;
        Tcs               = tcs;
        CancellationToken = cancellationToken;
    }

    public SapWorkItem(
        SapControlKind controlKind,
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
    public SapControlKind             ControlKind   { get; }
    public SapConnectionOptions?      ElevatedCreds { get; }
    public TaskCompletionSource<bool>? ControlTcs   { get; }
}
