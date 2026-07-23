using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Goods receipt against a single purchase order item via transaction MB01,
/// using a BDC recording (Z_RFC_CALL_TRANSACTION via BdcBuilder) —
/// BAPI_GOODSMVT_CREATE doesn't work against this SAP system, per the user,
/// who supplied the exact working BDC recording this is built from:
///
///   SAPMM07M 0200: BDC_OKCODE=/00, MKPF-BLDAT, MKPF-BUDAT, RM07M-LFSNR,
///                  MKPF-FRBNR, MKPF-BKTXT, RM07M-BWARTWE=101,
///                  RM07M-EBELN, RM07M-EBELP, RM07M-WERKS=3012, XFULL=X,
///                  RM07M-XNAPR=X, RM07M-WVERS1=X
///   SAPMM07M 0221: BDC_CURSOR=MSEG-ERFMG(01), BDC_OKCODE==SELE
///   SAPMM07M 0221 (final): BDC_OKCODE==BU
///
/// One call = one PO item = one material document, per the user: each cost
/// line on a shipment's PO gets its own separate MB01 posting (RM07M-EBELP
/// selects the specific item), rather than one BDC selecting every line —
/// this sidesteps the multi-page "select every line" problem in the SAP GUI
/// and lets a single cost line be reversed later without touching the
/// others (see PurchasingController.ReverseGoodsReceipt / MBST).
/// </summary>
internal static class GoodsReceiptHelper
{
    internal const string TransactionCode = "MB01";
    internal const string MovementType    = "101";
    internal const string Plant           = "3012";

    /// <summary>SAP renumbers PO items in 10s (10, 20, 30...) regardless of what PurchasingHelper sends at creation — confirmed against a real test PO.</summary>
    internal const int ItemInterval = 10;

    internal static RfcRequest BuildGoodsReceiptRequest(GoodsReceiptRequest body)
    {
        var ebelp = (Math.Max(body.LineNumber, 1) * ItemInterval).ToString("D5", CultureInfo.InvariantCulture);

        return BdcBuilder.For(TransactionCode)
            .Screen("SAPMM07M", "0200")
                .Field("BDC_OKCODE",     "/00")
                .Field("MKPF-BLDAT",     NormaliseDate(body.ShipmentCompletionDate))
                .Field("MKPF-BUDAT",     NormaliseDate(body.PostingDate))
                .Field("RM07M-LFSNR",    body.Reference)
                .Field("MKPF-FRBNR",     body.TrackingNumber)
                .Field("MKPF-BKTXT",     body.AddressCode)
                .Field("RM07M-BWARTWE",  MovementType)
                .Field("RM07M-EBELN",    body.PurchaseOrder)
                .Field("RM07M-EBELP",    ebelp)
                .Field("RM07M-WERKS",    Plant)
                .Field("XFULL",          "X")
                .Field("RM07M-XNAPR",    "X")
                .Field("RM07M-WVERS1",   "X")
            .Screen("SAPMM07M", "0221")
                .Field("BDC_CURSOR", "MSEG-ERFMG(01)")
                .Field("BDC_OKCODE", "=SELE")
            .Screen("SAPMM07M", "0221")
                .Field("BDC_OKCODE", "=BU")
            .Build();
    }

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
