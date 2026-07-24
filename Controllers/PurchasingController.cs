using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SapServer.Configuration;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/purchasing")]
public sealed class PurchasingController : SapControllerBase
{
    private readonly SapPoolOptions _poolOptions;

    public PurchasingController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        IOptions<SapPoolOptions> poolOptions,
        ILogger<PurchasingController> logger)
        : base(pool, permissions, logger)
    {
        _poolOptions = poolOptions.Value;
    }

    /// <summary>
    /// Creates a Purchase Order via BAPI_PO_CREATE1 (one PO, one PO item per
    /// request item) — see PurchasingHelper for the VBA source this was
    /// ported from. This is PO creation only; it does not book a goods
    /// receipt / GRNI entry. That step (MIGO) is separate, not-yet-built
    /// work, and is deliberately out of scope here.
    ///
    /// Uses a pinned worker (AcquireWorker/ExecuteOnWorkerAsync) so the
    /// commit/rollback that follows runs on the same SAP session as the
    /// BAPI call, matching the pattern already established in
    /// CostingController.PostFreight for BAPI_ACC_DOCUMENT_POST.
    /// </summary>
    [HttpPost("create-po")]
    [ProducesResponseType(typeof(ApiResponse<PoCreateRow>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> CreatePurchaseOrder(
        [FromBody] PoCreateRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), PurchasingHelper.FnPoCreate, ct);

        var request = PurchasingHelper.BuildPoCreateRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var worker = _pool.AcquireWorker();

        var data = await _pool.ExecuteOnWorkerAsync(worker, request, ct);
        var response = PurchasingHelper.ParsePoCreateResult(data);

        if (!response.Success)
        {
            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
            return BadRequest(ApiResponse<PoCreateRow>.Fail("INVALID_DATA", "Purchase order creation failed. Transaction rolled back.", response));
        }

        await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);
        return Ok(ApiResponse<PoCreateRow>.Ok(response));
    }

    /// <summary>
    /// Posts a goods receipt against a single purchase order item via
    /// transaction MB01 (BDC recording) — see GoodsReceiptHelper for the
    /// exact recording this was built from. BAPI_GOODSMVT_CREATE was tried
    /// first but doesn't work against this SAP system, per the user, hence
    /// the BDC approach instead. A plain BDC call via Z_RFC_CALL_TRANSACTION
    /// commits itself within the transaction it drives, so unlike
    /// CreatePurchaseOrder this doesn't need a pinned worker or an
    /// explicit BAPI_TRANSACTION_COMMIT/ROLLBACK — same as the existing
    /// set-delivery-weight (ZDEL) and consignment (MB1B) BDC endpoints.
    ///
    /// One call per PO item/cost line, per the user — the caller loops this
    /// once per cost line on the shipment's PO, incrementing LineNumber each
    /// time. Reuses ProductionHelpers.ParseBdcResponse (already built for
    /// the MF41/MBST BDC endpoints) rather than a bespoke parser — its
    /// DocumentNumber field is exactly what's needed to store the material
    /// document per cost line for later individual reversal.
    /// </summary>
    [HttpPost("post-goods-receipt")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> PostGoodsReceipt(
        [FromBody] GoodsReceiptRequest body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), GoodsReceiptHelper.TransactionCode, ct);

        var request = GoodsReceiptHelper.BuildGoodsReceiptRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var data = await _pool.ExecuteAsync(request, ct);
        var response = ProductionHelpers.ParseBdcResponse(data);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }

    /// <summary>
    /// Reverses a single goods receipt material document via transaction
    /// MBST — reuses the existing MBST BDC already built for scrap
    /// reversal (ProductionHelpers.BuildMbstRequest / POST
    /// /api/production/scrap/reverse) unchanged. That BDC's account-
    /// assignment confirmation screens (SAPLKACB 0002, plain =ENTE with no
    /// re-entered cost object) already match our scenario: our PO items are
    /// cost-center-assigned exactly like the scrap postings it was built
    /// for, and on a reversal SAP already knows the account assignment from
    /// the original document, so no new fields are needed here — just the
    /// material document number of the one cost line's GR being reversed
    /// (see PostGoodsReceipt/BdcResponse.DocumentNumber).
    /// </summary>
    [HttpPost("reverse-goods-receipt")]
    [ProducesResponseType(typeof(ApiResponse<BdcResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ReverseGoodsReceipt(
        [FromBody] Mf41Request body,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        await CheckPermissionAsync(GetUserId(), ProductionHelpers.FnCreate, ct);

        var request = ProductionHelpers.BuildMbstRequest(body);

        if (dryRun)
            return Ok(ApiResponse<RfcRequest>.Ok(request));

        var data = await _pool.ExecuteAsync(request, ct);
        var response = ProductionHelpers.ParseBdcResponse(data);

        return Ok(ApiResponse<BdcResponse>.Ok(response));
    }

    /// <summary>
    /// Combined, per-user-elevated flow: logon (with the calling user's own
    /// SAP credentials) -> create PO -> commit/rollback -> one goods receipt
    /// (MB01) per PO item -> logoff, all as one atomic unit of work on one
    /// pinned elevated worker slot (see SapConnectionPool.AcquireElevatedWorkerAsync
    /// / ReleaseElevatedWorkerAsync). This is what lets PO creation run under a
    /// real user's own SAP authorization instead of the shared service account,
    /// which doesn't have (and isn't being given) rights to create POs.
    ///
    /// Deliberately does NOT call CheckPermissionAsync. SapServer's own
    /// permission check (PermissionService) queries the portal SQL Server
    /// directly and cannot reliably do so here — that database can't be
    /// reached over the required TLS version from this app (the same
    /// limitation AuthOptions.BypassPermissions exists for). Authorization for
    /// this specific route is enforced twice elsewhere instead: Nexus's own
    /// permission check before it ever calls this endpoint, and — because this
    /// call now genuinely runs as the calling user via their own SAP session —
    /// SAP's own real per-user authorization on BAPI_PO_CREATE1/MB01 itself.
    ///
    /// The elevated worker is always released in a finally block (logging it
    /// back out) regardless of how far the flow got, so a slot can never be
    /// left logged in as one user waiting to be handed to somebody else.
    /// </summary>
    [HttpPost("create-po-and-receipt")]
    [ProducesResponseType(typeof(ApiResponse<CreatePoAndReceiptResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    public async Task<IActionResult> CreatePurchaseOrderAndReceipt(
        [FromBody] CreatePoAndReceiptRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SapUsername) || string.IsNullOrWhiteSpace(body.SapPassword))
            return BadRequest(ApiResponse<CreatePoAndReceiptResponse>.Fail(
                "MISSING_CREDENTIALS", "SAP username and password are required for this elevated action.",
                new CreatePoAndReceiptResponse()));

        if (body.Items.Count == 0)
            return BadRequest(ApiResponse<CreatePoAndReceiptResponse>.Fail(
                "NO_ITEMS", "At least one PO item is required.", new CreatePoAndReceiptResponse()));

        // Same SAP system/client as the service account — only the logon user
        // and password differ, matching one specific portal user's own SAP access.
        var elevatedCreds = new SapConnectionOptions
        {
            System   = _poolOptions.ServiceAccount.System,
            Client   = _poolOptions.ServiceAccount.Client,
            SystemId = _poolOptions.ServiceAccount.SystemId,
            User     = body.SapUsername,
            Password = body.SapPassword,
            Language = _poolOptions.ServiceAccount.Language,
        };

        var poRequest = PurchasingHelper.BuildPoCreateRequest(new PoCreateRequest
        {
            Vendor   = body.Vendor,
            Currency = body.Currency,
            DocDate  = body.DocDate,
            Items    = body.Items.Select(i => new PoCreateItem
            {
                Material          = i.Material,
                ShortText         = i.ShortText,
                Quantity          = i.Quantity,
                Unit              = i.Unit,
                NetPrice          = i.NetPrice,
                DeliveryDate      = i.DeliveryDate,
                AcctAssCat        = i.AcctAssCat,
                MaterialGroup     = i.MaterialGroup,
                GlAccount         = i.GlAccount,
                CostCenterOrOrder = i.CostCenterOrOrder,
            }).ToList(),
        });

        var worker = await _pool.AcquireElevatedWorkerAsync(elevatedCreds, ct);
        try
        {
            var poData     = await _pool.ExecuteOnWorkerAsync(worker, poRequest, ct);
            var poResponse = PurchasingHelper.ParsePoCreateResult(poData);

            if (!poResponse.Success)
            {
                await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiRollback(), ct);
                return BadRequest(ApiResponse<CreatePoAndReceiptResponse>.Fail(
                    "INVALID_DATA", "Purchase order creation failed. Transaction rolled back.",
                    new CreatePoAndReceiptResponse
                    {
                        PurchaseOrder = poResponse.PurchaseOrder,
                        PoSuccess     = false,
                        PoMessages    = poResponse.Messages,
                    }));
            }

            await _pool.ExecuteOnWorkerAsync(worker, CommitHelper.BuildBapiCommit(), ct);

            // PO is committed at this point. Goods receipts are posted one per
            // item next — per the user, each cost line gets its own MB01 call.
            // A single line's GR failing does not roll back the PO (it's
            // already committed and can't be) or stop the remaining lines;
            // each line's outcome is reported individually so the caller
            // (Node) can retry just the failed line(s) later.
            var lineResults = new List<CreatePoAndReceiptLineResult>();
            for (int i = 0; i < body.Items.Count; i++)
            {
                var item       = body.Items[i];
                var lineNumber = i + 1;
                var grRequest  = GoodsReceiptHelper.BuildGoodsReceiptRequest(new GoodsReceiptRequest
                {
                    PurchaseOrder          = poResponse.PurchaseOrder,
                    LineNumber             = lineNumber,
                    Reference              = item.Reference,
                    TrackingNumber         = item.TrackingNumber,
                    AddressCode            = item.AddressCode,
                    ShipmentCompletionDate = item.ShipmentCompletionDate,
                    PostingDate            = item.PostingDate,
                });

                try
                {
                    var grData     = await _pool.ExecuteOnWorkerAsync(worker, grRequest, ct);
                    var grResponse = ProductionHelpers.ParseBdcResponse(grData);
                    lineResults.Add(new CreatePoAndReceiptLineResult
                    {
                        LineNumber     = lineNumber,
                        Success        = true,
                        DocumentNumber = grResponse.DocumentNumber,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Goods receipt failed for PO {Po} line {Line} during create-po-and-receipt.",
                        poResponse.PurchaseOrder, lineNumber);
                    lineResults.Add(new CreatePoAndReceiptLineResult
                    {
                        LineNumber = lineNumber,
                        Success    = false,
                        Error      = ex.Message,
                    });
                }
            }

            return Ok(ApiResponse<CreatePoAndReceiptResponse>.Ok(new CreatePoAndReceiptResponse
            {
                PurchaseOrder = poResponse.PurchaseOrder,
                PoSuccess     = true,
                PoMessages    = poResponse.Messages,
                Lines         = lineResults,
            }));
        }
        finally
        {
            await _pool.ReleaseElevatedWorkerAsync(worker);
        }
    }
}
