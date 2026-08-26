using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Posts the actual components consumed under an approved Normanton-Nexus
/// traceability concession, via BAPI_GOODSMVT_CREATE — bypassing ZF40N's
/// automatic BOM-driven backflush entirely for that job. See
/// GoodsMovementRequest's header comment (ProductionModels.cs) for the full
/// rationale and the "UNCONFIRMED — verify via test.http" caveat this
/// carries, same as every other BAPI_GOODSMVT_CREATE caller in this
/// codebase.
///
/// Reuses StockAdjustmentHelper's exact GM_CODE "06" — the only GM_CODE
/// confirmed working against this SAP system so far.
///
/// Like StockAdjustmentHelper/MixingScrapHelper, this is a real BAPI: the
/// caller must acquire a pinned worker (ISapConnectionPool.AcquireWorker())
/// and issue an explicit BAPI_TRANSACTION_COMMIT/ROLLBACK on that same
/// worker afterward — see ProductionController.PostMixingScrap for the
/// pattern this mirrors.
/// </summary>
internal static class GoodsMovementHelper
{
    internal static RfcRequest BuildGoodsMovementRequest(GoodsMovementRequest body, IReadOnlyDictionary<string, string> resolvedStorageLocations)
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

        foreach (var c in body.Components)
        {
            var sloc = !string.IsNullOrWhiteSpace(c.StorageLocation)
                ? c.StorageLocation
                : resolvedStorageLocations.GetValueOrDefault(c.Material);

            var itemRow = new Dictionary<string, object?>
            {
                ["MATERIAL"]  = (SapPad.Pad(c.Material, 18) ?? "").ToUpperInvariant(),
                ["PLANT"]     = StockAdjustmentHelper.Plant,
                ["MOVE_TYPE"] = body.MovementType,
                ["ENTRY_QNT"] = c.Quantity,
                ["ENTRY_UOM"] = c.Unit,
            };
            if (!string.IsNullOrWhiteSpace(sloc)) itemRow["STGE_LOC"] = sloc;

            builder.TableRow("GOODSMVT_ITEM", itemRow);
        }

        builder
            .ReadParam("MATERIALDOCUMENT")
            .ReadParam("MATDOCUMENTYEAR")
            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }

    internal static GoodsMovementResponse ParseGoodsMovementResponse(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        var matDoc = ReturnTableHelper.GetParam(response, "MATERIALDOCUMENT") ?? "";

        // Same "no material document = no posting happened" success signal
        // StockAdjustmentHelper uses — BAPI_GOODSMVT_CREATE surfaces business
        // errors as TYPE E/A rows in RETURN with Call() still returning true.
        return new GoodsMovementResponse
        {
            MaterialDocument     = matDoc,
            MaterialDocumentYear = ReturnTableHelper.GetParam(response, "MATDOCUMENTYEAR") ?? "",
            Success              = !string.IsNullOrWhiteSpace(matDoc) && !ReturnTableHelper.HasBlockingError(
                                        messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))),
            Messages             = messages,
        };
    }
}
