using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class WarehouseHelpers
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreateTo    = "L_TO_CREATE_SINGLE";
    internal const string FnConsignment = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    // Column order must exactly match query_FIELDS registration order below
    internal static readonly string[] LquaColumns =
        ["LGORT", "LGTYP", "LGPLA", "MATNR", "VERME", "CHARG", "BESTQ", "SOBKZ", "SONUM"];

    // ── Stock ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildStockRequest(StockQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  query.RowCount)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" });

        foreach (var field in LquaColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'");

        if (!string.IsNullOrWhiteSpace(query.Material))
            builder.WhereCondition($"LQUA~MATNR EQ '{SapPad.Pad(query.Material, 18)}'");

        if (!string.IsNullOrWhiteSpace(query.StorageType))
            builder.WhereCondition($"LQUA~LGTYP EQ '{query.StorageType}'");

        if (!string.IsNullOrWhiteSpace(query.Bin))
            builder.WhereCondition($"LQUA~LGPLA EQ '{query.Bin}'");

        if (!string.IsNullOrWhiteSpace(query.Batch))
            builder.WhereCondition($"LQUA~CHARG EQ '{query.Batch}'");

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }

    internal static StockRow[] ParseStockRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|')
            .Where(cols => cols.Length >= LquaColumns.Length)
            .Select(cols => new StockRow
            {
                StorageLocation = cols[0],
                StorageType     = cols[1],
                Bin             = cols[2],
                Material        = cols[3],
                AvailableQty    = decimal.TryParse(cols[4], out var qty) ? qty : 0m,
                Batch           = cols[5],
                StockCategory   = cols[6],
                SpecialStockInd = cols[7],
                SpecialStockNum = cols[8]
            })
            .ToArray();
    }

    internal static MaterialTotalRow[] AggregateByMaterial(StockRow[] rows) =>
        rows
            .GroupBy(r => r.Material)
            .Select(g => new MaterialTotalRow
            {
                Material   = g.Key,
                TotalQty   = g.Sum(r => r.AvailableQty),
                QuantCount = g.Count()
            })
            .OrderBy(r => r.Material)
            .ToArray();

    internal static BinSummaryRow[] AggregateByBin(StockRow[] rows) =>
        rows
            .GroupBy(r => (r.StorageType, r.Bin))
            .Select(g => new BinSummaryRow
            {
                StorageType = g.Key.StorageType,
                Bin         = g.Key.Bin,
                QuantCount  = g.Count(),
                TotalQty    = g.Sum(r => r.AvailableQty)
            })
            .OrderBy(r => r.StorageType).ThenBy(r => r.Bin)
            .ToArray();

    // ── Transfer Order ────────────────────────────────────────────────────────

    internal static RfcRequest BuildTransferOrderRequest(CreateTransferOrderRequest body) =>
        new RfcRequestBuilder(FnCreateTo)
            .Import("I_LGNUM", Warehouse)
            .Import("I_WERKS", Plant)
            .Import("I_LGORT", body.StorageLocation)
            .Import("I_SQUIT", "X")
            .Import("I_BWLVS", "999")
            .Import("I_MATNR", SapPad.Pad(body.Material, 18))
            .Import("I_ANFME", body.Quantity)
            .Import("I_CHARG", SapPad.Pad(body.Batch, 10))
            .Import("I_ZEUGN", SapPad.Pad(body.Batch, 10))
            .Import("I_VLTYP", body.SourceType)
            .Import("I_VLPLA", SapPad.Pad(body.SourceBin, 10))
            .Import("I_BESTQ", body.StockCategory ?? "")
            .Import("I_SOBKZ", body.SpecialStockIndicator ?? "")
            .Import("I_SONUM", SapPad.Pad(body.SpecialStockNumber, 16))
            .Import("I_NLPLA", SapPad.Pad(body.DestinationBin, 10))
            .Import("I_NLTYP", body.DestinationType)
            .ReadParam("E_TANUM")
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .Build();

    internal static CreateTransferOrderResponse ParseTransferOrderResponse(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");
        return new CreateTransferOrderResponse
        {
            TransferOrderNumber = ReturnTableHelper.GetParam(response, "E_TANUM") ?? "",
            Success             = true,
            Messages            = messages
                .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
                .ToList()
        };
    }

    // ── Consignment MB1B ──────────────────────────────────────────────────────

    internal static RfcRequest BuildMb1bRequest(ConsignmentMb1bRequest body) =>
        BdcBuilder.For("MB1B")
            .Screen("SAPMM07M", "0400")
                .Field("BDC_OKCODE",    "/00")
                .Field("RM07M-MTSNR",   body.DeliveryNote ?? "")
                .Field("MKPF-BKTXT",    body.Header ?? "")
                .Field("RM07M-BWARTWA", "411")
                .Field("RM07M-SOBKZ",   "K")
                .Field("RM07M-WERKS",   Plant)
                .Field("RM07M-LGORT",   body.StorageLocation)
                .Field("XFULL",         "")
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE",     "/00")
                .Field("MSEGK-LIFNR",    SapPad.Pad(body.SpecialStockNumber, 10))
                .Field("MSEGK-UMLGO",    body.StorageLocation)
                .Field("MSEG-MATNR(01)", SapPad.Pad(body.Material, 18))
                .Field("MSEG-ERFMG(01)", body.Quantity.ToString())
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE", "=BU")
            .Build();

    internal static RfcRequest BuildToNonConsignRequest(ConsignmentMb1bRequest body) =>
        BdcBuilder.For("LT01")
            .Screen("SAPML03T", "0101")
                .Field("BDC_OKCODE",  "/00")
                .Field("LTAK-LGNUM",  Warehouse)
                .Field("LTAK-BWLVS",  "999")
                .Field("LTAP-MATNR",  SapPad.Pad(body.Material, 18))
                .Field("RL03T-ANFME", body.Quantity.ToString())
                .Field("LTAP-WERKS",  Plant)
                .Field("LTAP-LGORT",  body.StorageLocation)
                .Field("LTAP-ZEUGN",  "")
                .Field("LTAP-CHARG",  "")
                .Field("LTAP-SOBKZ",  "")
                .Field("RL03T-LSONR", "")
            .Screen("SAPML03T", "0102")
                .Field("BDC_OKCODE",  "/00")
                .Field("RL03T-SQUIT", "X")
                .Field("LTAP-VLTYP",  "922")
                .Field("LTAP-VLPLA",  "BLOCK")
                .Field("LTAP-NLTYP",  body.DestinationType)
                .Field("LTAP-NLPLA",  body.DestinationBin)
            .Build();

    internal static RfcRequest BuildToConsignRequest(ConsignmentMb1bRequest body) =>
        BdcBuilder.For("LT01")
            .Screen("SAPML03T", "0101")
                .Field("BDC_OKCODE",  "/00")
                .Field("LTAK-LGNUM",  Warehouse)
                .Field("LTAK-BWLVS",  "999")
                .Field("LTAP-MATNR",  SapPad.Pad(body.Material, 18))
                .Field("RL03T-ANFME", body.Quantity.ToString())
                .Field("LTAP-WERKS",  Plant)
                .Field("LTAP-LGORT",  body.StorageLocation)
                .Field("LTAP-ZEUGN",  "")
                .Field("LTAP-CHARG",  "")
                .Field("LTAP-SOBKZ",  "K")
                .Field("RL03T-LSONR", SapPad.Pad(body.SpecialStockNumber, 16))
            .Screen("SAPML03T", "0102")
                .Field("BDC_OKCODE",  "/00")
                .Field("RL03T-SQUIT", "X")
                .Field("LTAP-VLTYP",  body.SourceType)
                .Field("LTAP-VLPLA",  body.SourceBin)
                .Field("LTAP-NLTYP",  "922")
                .Field("LTAP-NLPLA",  "BLOCK")
            .Build();

    internal static ConsignmentMb1bResponse ParseConsignmentResponse(
        RfcResponse mb1b, RfcResponse toNonConsign, RfcResponse toConsign) =>
        new ConsignmentMb1bResponse
        {
            Mb1bMessage         = ReturnTableHelper.GetParam(mb1b,         "MESSG") ?? "",
            ToNonConsignMessage = ReturnTableHelper.GetParam(toNonConsign, "MESSG") ?? "",
            ToConsignMessage    = ReturnTableHelper.GetParam(toConsign,    "MESSG") ?? ""
        };
}
