using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Scraps a whole finished mixing batch directly, via BAPI_GOODSMVT_CREATE
/// (movement 551) — for expired, unstaged Billet tubs (Normanton-Nexus's
/// PROD_SUPERVISOR expiry-scrap action). Distinct from
/// ProductionHelpers.BuildBomScrapRequest (MB11 via BDC), which posts
/// against a material's BOM *components* — this posts once against the mix
/// material itself.
///
/// Reuses StockAdjustmentHelper's exact GM_CODE "06" ("goods movements
/// without reference", the MB1C code path) — the user has confirmed this
/// combination works against real data on this SAP system (movement 711;
/// see StockAdjustmentModels.cs's header comment). Movement 551 hasn't
/// specifically been exercised through this BAPI/GM_CODE before, so treat
/// it the same as any new movement type through BAPI_GOODSMVT_CREATE here —
/// verify via test.http before relying on it for a real inventory
/// write-off, same discipline StockAdjustmentHelper itself was held to.
///
/// No batch/CHARG field anywhere: mix materials are not batch-managed in
/// SAP — all tub/batch-level traceability for mixes lives in
/// Normanton-Nexus only.
///
/// Like StockAdjustmentHelper, this is a real BAPI: the caller must acquire
/// a pinned worker (ISapConnectionPool.AcquireWorker()) and issue an
/// explicit BAPI_TRANSACTION_COMMIT/ROLLBACK on that same worker afterward
/// — see WarehouseController.CreateStockAdjustment for the pattern this
/// mirrors (ProductionController.PostMixingScrap does the same).
/// </summary>
internal static class MixingScrapHelper
{
    internal const string MovementType = "551";

    /// <param name="body">The request as received from Node — StorageLocation may be blank.</param>
    /// <param name="resolvedStorageLocation">
    /// The storage location to actually post against — either body.StorageLocation
    /// verbatim, or the controller's MARC-LGPRO lookup result when that was blank.
    /// Kept as an explicit parameter rather than mutating body so the request DTO
    /// stays a plain immutable record of what Node actually sent.
    /// </param>
    internal static RfcRequest BuildMixingScrapRequest(MixingScrapRequest body, string resolvedStorageLocation)
    {
        var today = DateTime.Now.ToString("yyyyMMdd");

        var builder = new RfcRequestBuilder(StockAdjustmentHelper.FnGoodsMvtCreate)
            .StructImport("GOODSMVT_HEADER", new
            {
                PSTNG_DATE = today,
                DOC_DATE   = today,
                REF_DOC_NO = body.Header.Length > 16 ? body.Header.Substring(0, 16) : body.Header, // net48 lacks string range indexers
            })
            .StructImport("GOODSMVT_CODE", new { GM_CODE = StockAdjustmentHelper.GmCodeOtherGoodsMovement })
            .Import("TESTRUN", body.TestRun ? "X" : "");

        var itemRow = new Dictionary<string, object?>
        {
            ["MATERIAL"]  = SapPad.Pad(body.Material, 18),
            ["PLANT"]     = string.IsNullOrWhiteSpace(body.Plant) ? StockAdjustmentHelper.Plant : body.Plant,
            ["STGE_LOC"]  = resolvedStorageLocation,
            ["MOVE_TYPE"] = MovementType,
            ["ENTRY_QNT"] = body.Quantity,
            ["ENTRY_UOM"] = body.Unit,
        };
        if (!string.IsNullOrWhiteSpace(body.ScrapReason)) itemRow["MOVE_REAS"] = body.ScrapReason;

        builder.TableRow("GOODSMVT_ITEM", itemRow);

        builder
            .ReadParam("MATERIALDOCUMENT")
            .ReadParam("MATDOCUMENTYEAR")
            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }
}
