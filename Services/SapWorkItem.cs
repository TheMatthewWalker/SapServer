using SapServer.Models;

namespace SapServer.Services;

/// <summary>
/// A single unit of work queued to an STA worker thread.
/// The TaskCompletionSource bridges the STA thread result back to the awaiting HTTP thread.
/// </summary>
internal sealed class SapWorkItem
{
    public SapWorkItem(
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
