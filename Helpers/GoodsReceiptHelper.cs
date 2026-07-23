using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Goods receipt against a purchase order via transaction MB01, using a BDC
/// recording (Z_RFC_CALL_TRANSACTION via BdcBuilder) — BAPI_GOODSMVT_CREATE
/// was tried first but doesn't work against this SAP system, per the user,
/// who supplied the exact BDC recording this is built from:
///
///   SAPMM07M 0200: BDC_OKCODE=/00, MKPF-BLDAT, MKPF-BUDAT, RM07M-LFSNR,
///                  MKPF-FRBNR, MKPF-BKTXT, RM07M-BWARTWE=101,
///                  RM07M-EBELN, RM07M-WERKS=3012, XFULL=X,
///                  RM07M-XNAPR=X, RM07M-WVERS1=X
///   SAPMM07M 0221 (once per PO item, nn = 01, 02, ...): BDC_CURSOR=MSEG-ERFMG(nn), BDC_OKCODE==SELE
///   SAPMM07M 0221 (final): BDC_OKCODE==BU
///
/// Movement type (101) and plant (3012) are fixed per the recording, not
/// caller-supplied.
/// </summary>
internal static class GoodsReceiptHelper
{
    internal const string TransactionCode = "MB01";
    internal const string MovementType    = "101";
    internal const string Plant           = "3012";

    internal static RfcRequest BuildGoodsReceiptRequest(GoodsReceiptRequest body)
    {
        var builder = BdcBuilder.For(TransactionCode)
            .Screen("SAPMM07M", "0200")
                .Field("BDC_OKCODE",     "/00")
                .Field("MKPF-BLDAT",     NormaliseDate(body.ShipmentCompletionDate))
                .Field("MKPF-BUDAT",     NormaliseDate(body.PostingDate))
                .Field("RM07M-LFSNR",    body.Reference)
                .Field("MKPF-FRBNR",     body.TrackingNumber)
                .Field("MKPF-BKTXT",     body.AddressCode)
                .Field("RM07M-BWARTWE",  MovementType)
                .Field("RM07M-EBELN",    body.PurchaseOrder)
                .Field("RM07M-WERKS",    Plant)
                .Field("XFULL",          "X")
                .Field("RM07M-XNAPR",    "X")
                .Field("RM07M-WVERS1",   "X");

        // One "select this line" screen per PO item, exactly as recorded —
        // MSEG-ERFMG(01), MSEG-ERFMG(02), etc.
        var itemCount = Math.Max(body.ItemCount, 1);
        for (var i = 1; i <= itemCount; i++)
        {
            var fieldName = $"MSEG-ERFMG({i:D2})";
            builder
                .Screen("SAPMM07M", "0221")
                    .Field("BDC_CURSOR", fieldName)
                    .Field("BDC_OKCODE", "=SELE");
        }

        builder
            .Screen("SAPMM07M", "0221")
                .Field("BDC_OKCODE", "=BU");

        return builder.Build();
    }

    internal static GoodsReceiptResponse ParseGoodsReceiptResponse(RfcResponse response) =>
        new GoodsReceiptResponse
        {
            Message = ReturnTableHelper.GetParam(response, "MESSG") ?? ""
        };

    private static string NormaliseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateTime.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        if (date.Contains('.')) return date; // already dd.MM.yyyy, as recorded

        if (date.Length == 8 && date.All(char.IsDigit) &&
            DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return d1.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        return date;
    }
}
