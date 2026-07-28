using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Stock adjustment (711/712) via BAPI_GOODSMVT_CREATE. See
/// StockAdjustmentModels.cs for the full caveat on why this is a *second*
/// attempt at this BAPI in this codebase, and why it's being tested via
/// test.http in isolation before being wired into Node.
/// </summary>
internal static class StockAdjustmentHelper
{
    internal const string FnGoodsMvtCreate = "BAPI_GOODSMVT_CREATE";
    internal const string Plant            = "3012";

    /// <summary>
    /// GOODSMVT_CODE-GM_CODE "06" — "goods movements without reference" —
    /// is the code path BAPI_GOODSMVT_CREATE uses for the family of
    /// movement types normally entered through transaction MB1C ("Other
    /// Goods Receipts"), which is where 711/712 (physical inventory
    /// difference, posted directly without an inventory document) live, and
    /// their blocked-stock (category 'S') counterparts 717/718.
    /// Kept as a named constant rather than inlined so it's obvious what to
    /// change first if testing shows a different GM_CODE is actually needed
    /// on this SAP release.
    /// </summary>
    internal const string GmCodeOtherGoodsMovement = "06";

    /// <summary>
    /// 711/712 don't post against category 'S' (blocked) stock — per the
    /// user, that needs 717 (reduce) / 718 (add back in) instead. Both
    /// pairs are accepted here; StockAdjustmentHelper doesn't itself decide
    /// which one applies for a given request — the caller (Node/frontend)
    /// picks based on direction and StockCategory, same as it already does
    /// for 711 vs 712.
    /// </summary>
    internal static readonly HashSet<string> ExpectedMovementTypes = ["711", "712", "717", "718"];

    internal static RfcRequest BuildStockAdjustmentRequest(StockAdjustmentRequest body)
    {
        var postingDate  = NormaliseDate(body.PostingDate);
        var documentDate = NormaliseDate(body.DocumentDate);

        var builder = new RfcRequestBuilder(FnGoodsMvtCreate)
            .StructImport("GOODSMVT_HEADER", new
            {
                PSTNG_DATE = postingDate,
                DOC_DATE   = documentDate,
                REF_DOC_NO = (body.Reference ?? "").Length > 16 ? body.Reference![..16] : (body.Reference ?? ""),
            })
            .StructImport("GOODSMVT_CODE", new { GM_CODE = GmCodeOtherGoodsMovement })
            .Import("TESTRUN", body.TestRun ? "X" : "");

        var itemRow = new Dictionary<string, object?>
        {
            ["MATERIAL"]  = SapPad.Pad(body.Material, 18),
            ["PLANT"]     = string.IsNullOrWhiteSpace(body.Plant) ? Plant : body.Plant,
            ["STGE_LOC"]  = body.StorageLocation,
            ["MOVE_TYPE"] = body.MovementType,
            ["ENTRY_QNT"] = body.Quantity,
            ["ENTRY_UOM"] = body.Unit,
            ["STGE_TYPE"] = body.StorageType,
            ["STGE_BIN"] = body.StorageBin,
            ["SPEC_STOCK"] = body.SpecialStockIndicator,
            ["VENDOR"] = body.SpecialStockNumber
        };
        if (!string.IsNullOrWhiteSpace(body.Batch))         itemRow["BATCH"]    = SapPad.Pad(body.Batch, 10);
        if (!string.IsNullOrWhiteSpace(body.ValuationType)) itemRow["VAL_TYPE"] = body.ValuationType;

        builder.TableRow("GOODSMVT_ITEM", itemRow);

        builder
            .ReadParam("MATERIALDOCUMENT")
            .ReadParam("MATDOCUMENTYEAR")
            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }

    internal static StockAdjustmentResponse ParseStockAdjustmentResponse(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        var matDoc = ReturnTableHelper.GetParam(response, "MATERIALDOCUMENT") ?? "";

        // BAPI_GOODSMVT_CREATE doesn't reject bad input through an RFC-level
        // failure the way L_TO_CREATE_SINGLE does — it's a real BAPI, so
        // business errors come back as TYPE "E"/"A" rows in RETURN with
        // Call() still returning true. No material document number means no
        // posting happened, regardless of what RETURN says, so that's the
        // primary success signal (mirrors PurchasingHelper.ParsePoCreateResult's
        // EXPPURCHASEORDER-blank-means-failed convention for BAPI_PO_CREATE1).
        return new StockAdjustmentResponse
        {
            MaterialDocument     = matDoc,
            MaterialDocumentYear = ReturnTableHelper.GetParam(response, "MATDOCUMENTYEAR") ?? "",
            Success              = !string.IsNullOrWhiteSpace(matDoc) && !ReturnTableHelper.HasBlockingError(
                                        messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))),
            Messages             = messages,
        };
    }

    private static string NormaliseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (date.Length == 8 && date.All(char.IsDigit)) return date; // already yyyyMMdd

        if (DateTime.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return d1.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return date;
    }
}
