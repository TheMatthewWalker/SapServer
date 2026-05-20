using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class QualityHelpers
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreateTo    = "L_TO_CREATE_SINGLE";
    internal const string FnBlockStock = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    // Column order must exactly match query_FIELDS registration order below
    internal static readonly string[] LquaColumns =
        ["LGORT", "LGTYP", "LGPLA", "MATNR", "VERME", "CHARG", "BESTQ", "SOBKZ", "SONUM"];

    // ── Stock ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildBlockedStockRequest(StockQuery query)
    {
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  query.RowCount)
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = "LQUA" });

        foreach (var field in LquaColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = "LQUA", FIELDNAME = field });

        builder.WhereCondition($"LQUA~LGNUM EQ '{Warehouse}'");
        builder.WhereCondition($"LQUA~BESTQ EQ 'S'"); // block indicator

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

    internal static StockRow[] ParseBlockedStockRows(RfcResponse response)
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

    // ── Transfer Order ────────────────────────────────────────────────────────

   internal static (CreateTransferOrderRequest primary, CreateTransferOrderRequest secondary)
    PrepTransferOrderRequest(QualityMb1bRequest body, string blockDirection)
    {
        if (blockDirection == "BLOCK")
        {
            var req1 = new CreateTransferOrderRequest
            {
                StorageLocation       = body.StorageLocation,
                Material              = SapPad.Pad(body.Material, 18),
                Quantity              = body.Quantity,
                SourceType            = "922",
                SourceBin             = "BLOCK",
                DestinationType       = body.BinType,
                DestinationBin        = body.Bin,
                StockCategory         = "S",
                Batch                 = SapPad.Pad(body.Batch, 10) ?? "",
                SpecialStockIndicator = body.SpecialStockIndicator ?? "",
                SpecialStockNumber    = SapPad.Pad(body.SpecialStockNumber, 16) ?? ""
            };

            var req2 = new CreateTransferOrderRequest
            {
                StorageLocation       = body.StorageLocation,
                Material              = SapPad.Pad(body.Material, 18),
                Quantity              = body.Quantity,
                SourceType            = body.BinType,
                SourceBin             = body.Bin,
                DestinationType       = "922",
                DestinationBin        = "BLOCK",
                StockCategory         = "",
                Batch                 = SapPad.Pad(body.Batch, 10) ?? "",
                SpecialStockIndicator = body.SpecialStockIndicator ?? "",
                SpecialStockNumber    = SapPad.Pad(body.SpecialStockNumber, 16) ?? ""
            };

            return (req1, req2);
        }

        if (blockDirection == "UNBLOCK")
        {
            var req1 = new CreateTransferOrderRequest
            {
                StorageLocation       = body.StorageLocation,
                Material              = SapPad.Pad(body.Material, 18),
                Quantity              = body.Quantity,
                SourceType            = body.BinType,
                SourceBin             = body.Bin,
                DestinationType       = "922",
                DestinationBin        = "BLOCK",
                StockCategory         = "S",
                Batch                 = SapPad.Pad(body.Batch, 10) ?? "",
                SpecialStockIndicator = body.SpecialStockIndicator ?? "",
                SpecialStockNumber    = SapPad.Pad(body.SpecialStockNumber, 16) ?? ""
            };

            var req2 = new CreateTransferOrderRequest
            {
                StorageLocation       = body.StorageLocation,
                Material              = SapPad.Pad(body.Material, 18),
                Quantity              = body.Quantity,
                SourceType            = "922",
                SourceBin             = "BLOCK",
                DestinationType       = body.BinType,
                DestinationBin        = body.Bin,
                StockCategory         = "",
                Batch                 = SapPad.Pad(body.Batch, 10) ?? "",
                SpecialStockIndicator = body.SpecialStockIndicator ?? "",
                SpecialStockNumber    = SapPad.Pad(body.SpecialStockNumber, 16) ?? ""
            };

            return (req1, req2);
        }

        else
        {
            throw new ArgumentException($"Invalid block direction: {blockDirection}");
        }
    }

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

    internal static RfcRequest BuildMb1bBlockedRequest(QualityMb1bRequest body, string blockDirection) =>
        BdcBuilder.For("MB11")
            .Screen("SAPMM07M", "0400")
                .Field("BDC_OKCODE",    "/00")
                .Field("RM07M-MTSNR",   body.Username ?? "")
                .Field("MKPF-BKTXT",    body.Header ?? "")
                .Field("RM07M-BWARTWA", blockDirection == "BLOCK" ? "344" : "343")
                .Field("RM07M-SOBKZ",   body.SpecialStockIndicator ?? "")
                .Field("RM07M-WERKS",   Plant)
                .Field("RM07M-LGORT",   body.StorageLocation)
                .Field("XFULL",         "X")
                .Field("RM07M-XNAPR",   "X")
                .Field("RM07M-WVERS1",  "X")
            .Screen("SAPMM07M", "0421")
                .Field("BDC_OKCODE",     "=BU")
                .FieldIf(!string.IsNullOrEmpty(body.SpecialStockIndicator), "MSEGK-LIFNR", SapPad.Pad(body.SpecialStockNumber, 10))
                .Field("MSEGK-UMLGO",    body.StorageLocation)
                .Field("MSEG-MATNR(01)", SapPad.Pad(body.Material, 18))
                .Field("MSEG-ERFMG(01)", body.Quantity)
                .FieldIf(!string.IsNullOrEmpty(body.Batch), "MSEG-CHARG(01)", body.Batch)
            .Screen("SAPLKACB", "0002")
                .Field("BDC_OKCODE", "=ENTE")
            .Screen("SAPLKACB", "0002")
                .Field("BDC_OKCODE", "=ENTE")
            //.DebugPrintGrid()
            .Build();

    internal static QualityMb1bResponse ParseQualityResponse(
        RfcResponse mb1b, RfcResponse toNonBlocked, RfcResponse toBlocked) =>
        new QualityMb1bResponse
        {
            Mb1bMessage         = ReturnTableHelper.GetParam(mb1b,         "MESSG") ?? "",
            ToNonBlockedMessage = ReturnTableHelper.GetParam(toNonBlocked, "MESSG") ?? "",
            ToBlockedMessage    = ReturnTableHelper.GetParam(toBlocked,    "MESSG") ?? ""
        };
}
