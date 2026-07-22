using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Purchase order creation via BAPI_PO_CREATE1. Ported field-for-field from
/// the user's working Excel macro (AT_Dashboard.xlsm, module BAPI_PO, sub
/// CreateSAPPurchaseOrder) — every field name, constant, and table below was
/// checked against that VBA source, not assumed from general SAP knowledge.
/// Do not add/change fields here without re-checking the VBA.
///
/// Not ported (out of scope, not needed for the header/item/schedule/account
/// shape used by CreateSAPPurchaseOrder): POADDRDELIVERY (declared and
/// freetable'd in the VBA but never populated — no delivery-address override
/// is used), and the separate Z_RFC_PRINT_PO_BDC print-PO call.
///
/// This only creates the PO. The goods-receipt/GRNI booking step (MIGO) is
/// a separate, not-yet-built piece of work — see MIGO.bas when that's
/// picked up.
/// </summary>
internal static class PurchasingHelper
{
    internal const string FnPoCreate = "BAPI_PO_CREATE1";

    // Matches POHEADER-COMP_CODE / POITEM-PLANT in the VBA exactly, and
    // reuses the same constants already established for freight posting
    // (CostingHelper.CompanyCode == "0312") — confirms the two are
    // consistent across both the old Excel tooling and this codebase.
    internal const string CompanyCode = CostingHelper.CompanyCode; // "0312"
    internal const string Plant       = CostingHelper.Plant;       // "3012"
    internal const string PurchOrg    = "3012";                    // POHEADER-PURCH_ORG
    internal const string PurGroup    = "386";                     // POHEADER-PUR_GROUP
    internal const string DocType     = "NB";                      // POHEADER-DOC_TYPE (standard PO)

    internal static RfcRequest BuildPoCreateRequest(PoCreateRequest body)
    {
        var docDate = !string.IsNullOrWhiteSpace(body.DocDate)
            ? NormaliseDate(body.DocDate!)
            : DateTime.Now.ToString("yyyyMMdd");

        var builder = new RfcRequestBuilder(FnPoCreate)

            // ========================
            // PO HEADER
            // ========================
            .StructImport("POHEADER", new
            {
                COMP_CODE = CompanyCode,
                DOC_TYPE  = DocType,
                VENDOR    = SapPad.Pad(body.Vendor, 10),
                PURCH_ORG = PurchOrg,
                PUR_GROUP = PurGroup,
                CURRENCY  = body.Currency,
                DOC_DATE  = docDate
            })
            .StructImport("POHEADERX", new
            {
                COMP_CODE = "X",
                DOC_TYPE  = "X",
                VENDOR    = "X",
                PURCH_ORG = "X",
                PUR_GROUP = "X",
                CURRENCY  = "X",
                DOC_DATE  = "X"
            });

        for (var i = 0; i < body.Items.Count; i++)
        {
            var item = body.Items[i];

            // 1-based, 5-digit zero-padded item number — matches the VBA's
            // Format((p - 1), "00000") exactly (sequential 00001, 00002...,
            // NOT the usual SAP x10 item numbering — this is intentional,
            // copied from working code).
            var poItem = (i + 1).ToString("D5", CultureInfo.InvariantCulture);
            var hasAcctAssignment = !string.IsNullOrWhiteSpace(item.AcctAssCat);
            var hasMaterial       = !string.IsNullOrWhiteSpace(item.Material);
            var qty                = Math.Round(item.Quantity, 3);

            var poitemRow = new Dictionary<string, object?>
            {
                ["PO_ITEM"]    = poItem,
                ["SHORT_TEXT"] = item.ShortText,
                ["PLANT"]      = Plant,
                ["QUANTITY"]   = qty,
                ["NET_PRICE"]  = Math.Round(item.NetPrice, 2),
                ["PRICE_UNIT"] = qty, // matches VBA: POitem(...,"PRICE_UNIT") = Round(inp(p,2),3) — same value as QUANTITY
                ["PO_UNIT"]    = item.Unit,
                ["ITEM_CAT"]   = "0",
                ["ACCTASSCAT"] = item.AcctAssCat ?? ""
            };
            if (hasMaterial)       poitemRow["MATERIAL"]    = SapPad.Pad(item.Material, 18);
            if (hasAcctAssignment) poitemRow["MATL_GROUP"]  = item.MaterialGroup ?? "";
            builder.TableRow("POITEM", poitemRow);

            var poitemXRow = new Dictionary<string, object?>
            {
                ["PO_ITEM"]    = poItem,
                ["PLANT"]      = "X",
                ["QUANTITY"]   = "X",
                ["NET_PRICE"]  = "X",
                ["PO_UNIT"]    = "X",
                ["ITEM_CAT"]   = "X",
                ["ACCTASSCAT"] = "X",
                ["SHORT_TEXT"] = "X"
            };
            if (hasMaterial)       poitemXRow["MATERIAL"]   = "X";
            if (hasAcctAssignment) poitemXRow["MATL_GROUP"] = "X";
            builder.TableRow("POITEMX", poitemXRow);

            builder.TableRow("POSCHEDULE", new
            {
                PO_ITEM       = poItem,
                SCHED_LINE    = "0001",
                DELIVERY_DATE = NormaliseDate(item.DeliveryDate),
                QUANTITY      = qty
            });
            builder.TableRow("POSCHEDULEX", new
            {
                PO_ITEM       = poItem,
                SCHED_LINE    = "0001",
                DELIVERY_DATE = "X",
                QUANTITY      = "X"
            });

            // Only populated when ACCTASSCAT is set — matches the VBA's
            // "If Not inp(p, 7) = \"\" Then" guard around POACCOUNT/POACCOUNTX.
            if (hasAcctAssignment)
            {
                var acctRow = new Dictionary<string, object?>
                {
                    ["PO_ITEM"]    = poItem,
                    ["SERIAL_NO"]  = "01",
                    ["QUANTITY"]   = qty,
                    ["GL_ACCOUNT"] = SapPad.Pad(item.GlAccount, 10)
                };
                var acctXRow = new Dictionary<string, object?>
                {
                    ["PO_ITEM"]    = poItem,
                    ["SERIAL_NO"]  = "01",
                    ["QUANTITY"]   = "X",
                    ["GL_ACCOUNT"] = "X"
                };

                if (item.AcctAssCat == "K")
                {
                    acctRow["COSTCENTER"]  = SapPad.Pad(item.CostCenterOrOrder, 10);
                    acctXRow["COSTCENTER"] = "X";
                }
                else if (item.AcctAssCat == "F")
                {
                    acctRow["ORDERID"]  = SapPad.Pad(item.CostCenterOrOrder, 10);
                    acctXRow["ORDERID"] = "X";
                }

                builder.TableRow("POACCOUNT", acctRow);
                builder.TableRow("POACCOUNTX", acctXRow);
            }
        }

        builder
            .ReadParam("EXPPURCHASEORDER")
            .ReadTable("RETURN", "TYPE", "MESSAGE");

        return builder.Build();
    }

    internal static PoCreateRow ParsePoCreateResult(RfcResponse response)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        var poNumber = ReturnTableHelper.GetParam(response, "EXPPURCHASEORDER") ?? "";

        return new PoCreateRow
        {
            PurchaseOrder = poNumber,
            Success       = !string.IsNullOrWhiteSpace(poNumber),
            Messages      = messages
        };
    }

    private static string NormaliseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateTime.Now.ToString("yyyyMMdd");
        if (date.Length == 8 && date.All(char.IsDigit)) return date; // already yyyyMMdd

        if (DateTime.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1))
            return d1.ToString("yyyyMMdd");

        if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2.ToString("yyyyMMdd");

        return date;
    }
}
