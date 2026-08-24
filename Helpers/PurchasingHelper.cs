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

            // Standard SAP x10 item numbering — 00010, 00020, 00030... —
            // matching what ME21N/ME23N and BAPI_PO_CREATE1's own
            // auto-numbering (when PO_ITEM is left blank) would produce.
            // Deliberately NOT the VBA source's original Format((p - 1),
            // "00000") (sequential 00001, 00002...) — changed on the user's
            // instruction so item numbers here read the same as they would
            // anywhere else in SAP.
            var poItem = ((i + 1) * 10).ToString("D5", CultureInfo.InvariantCulture);
            var hasAcctAssignment = !string.IsNullOrWhiteSpace(item.AcctAssCat);
            var hasMaterial       = !string.IsNullOrWhiteSpace(item.Material);
            var hasNetPrice       = item.NetPrice.HasValue;
            var qty                = Math.Round(item.Quantity, 3);

            var poitemRow = new Dictionary<string, object?>
            {
                ["PO_ITEM"]    = poItem,
                ["SHORT_TEXT"] = item.ShortText,
                ["PLANT"]      = Plant,
                ["QUANTITY"]   = qty,
                ["PO_UNIT"]    = item.Unit,
                ["ITEM_CAT"]   = "0",
                ["ACCTASSCAT"] = item.AcctAssCat ?? ""
            };
            // NET_PRICE/PRICE_UNIT are only sent (and only flagged "X" in
            // POITEMX below) when a price was actually supplied — leaving
            // both out entirely, rather than sending 0, is what lets SAP's
            // own price determination (purchasing info record / condition
            // records) fill the price in during BAPI_PO_CREATE1 instead of
            // us silently posting a free-of-charge line. See PoCreateItem.NetPrice.
            if (hasNetPrice)
            {
                poitemRow["NET_PRICE"]  = Math.Round(item.NetPrice!.Value, 2);
                poitemRow["PRICE_UNIT"] = qty; // matches VBA: POitem(...,"PRICE_UNIT") = Round(inp(p,2),3) — same value as QUANTITY
            }
            if (hasMaterial)       poitemRow["MATERIAL"]    = SapPad.Pad(item.Material, 18);
            if (hasAcctAssignment) poitemRow["MATL_GROUP"]  = item.MaterialGroup ?? "";
            builder.TableRow("POITEM", poitemRow);

            var poitemXRow = new Dictionary<string, object?>
            {
                ["PO_ITEM"]    = poItem,
                ["PLANT"]      = "X",
                ["QUANTITY"]   = "X",
                ["PO_UNIT"]    = "X",
                ["ITEM_CAT"]   = "X",
                ["ACCTASSCAT"] = "X",
                ["SHORT_TEXT"] = "X"
            };
            if (hasNetPrice)        poitemXRow["NET_PRICE"]  = "X";
            if (hasMaterial)        poitemXRow["MATERIAL"]   = "X";
            if (hasAcctAssignment)  poitemXRow["MATL_GROUP"] = "X";
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

    /// <summary>
    /// Reads a PO's price back via BAPI_PO_GETDETAIL1. Originally guessed at
    /// an ITEM_CONDITIONS = 'X' import flag to request the POCOND pricing-
    /// conditions table (the widely-documented shape elsewhere), but that
    /// parameter doesn't exist on this SAP system at all — confirmed via the
    /// Normanton-Nexus BAPI Inspector's real RFC_GET_FUNCTION_INTERFACE dump
    /// of this function's actual interface here (no CONDITIONS-anything
    /// import parameter exists), and it crashed the call outright (fixed
    /// separately in SapStaWorker.ExecuteRfc's null-guards, but the
    /// parameter itself was still just wrong). That same dump confirms
    /// POCOND is a plain TABLE-class parameter with exactly the fields read
    /// below, populated unconditionally alongside POITEM/POSCHEDULE/etc.
    /// whenever PURCHASEORDER is supplied — no flag needed. Still
    /// unconfirmed: whether POCOND is actually non-empty/priced for a real
    /// PO (needs a live PO to check) — PurchasingController.GetPoPrice/
    /// ParsePoPrices return an empty result rather than throwing either way,
    /// and Normanton-Nexus treats a missing price as "SAP hasn't priced this
    /// line" (falls back to "Per SAP condition" on the PO PDF) rather than a
    /// hard failure — see routes/performance.js's create-po route.
    /// </summary>
    internal const string FnPoGetDetail = "BAPI_PO_GETDETAIL1";

    internal static RfcRequest BuildPoGetPriceRequest(string poNumber)
    {
        return new RfcRequestBuilder(FnPoGetDetail)
            .Import("PURCHASEORDER",  SapPad.Pad(poNumber, 10))
            .ReadTable("POCOND", "ITM_NUMBER", "COND_TYPE", "COND_VALUE", "CURRENCY", "COND_UNIT")
            .Build();
    }

    /// <summary>
    /// Keyed by PO item number as a plain integer string with no leading
    /// zeros (e.g. "10", not "00010") — a real RFC_GET_FUNCTION_INTERFACE
    /// dump of this function (via Normanton-Nexus's BAPI Inspector) shows
    /// POCOND's ITM_NUMBER is a 6-digit NUMC field, so it comes back as
    /// "000010", not the 5-digit "00010" BuildPoCreateRequest's x10
    /// numbering assigns. Comparing those as exact strings would never
    /// match — the RFC call itself was succeeding fine (see the SapStaWorker
    /// fix + BuildPoGetPriceRequest's ITEM_CONDITIONS removal), but every
    /// price lookup was silently failing regardless, because the join key
    /// never lined up. Normalizing to a bare integer string here, and
    /// routes/performance.js's buildPoPdfItems doing the same to its own
    /// 5-digit poItemNumber before looking a price up, makes both sides
    /// agree regardless of either one's padding width. PB00 (SAP's standard
    /// gross/net price condition type) is preferred when present; any other
    /// priced condition on the item is used as a fallback so a differently-
    /// configured pricing procedure still surfaces something rather than
    /// nothing. A condition with no positive value is ignored.
    /// </summary>
    internal static Dictionary<string, decimal> ParsePoPrices(RfcResponse response)
    {
        var result = new Dictionary<string, decimal>();
        if (!response.Tables.TryGetValue("POCOND", out var rows))
            return result;

        foreach (var row in rows)
        {
            var itemNumber = row.GetString("ITM_NUMBER");
            var condType   = row.GetString("COND_TYPE");
            var value      = row.GetDecimal("COND_VALUE");
            if (string.IsNullOrWhiteSpace(itemNumber) || value <= 0)
                continue;
            if (!int.TryParse(itemNumber, out var itemNum))
                continue;

            var key = itemNum.ToString();
            if (!result.ContainsKey(key) || condType == "PB00")
                result[key] = value;
        }
        return result;
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
