using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Delivery item-quantity correction via BAPI_OUTB_DELIVERY_CHANGE. See
/// DeliveryChangeModels.cs for the full caveat on the header structures
/// being left essentially empty (item-only change) and on CHG_DELQTY being
/// what actually triggers the quantity update.
/// </summary>
internal static class DeliveryChangeHelper
{
    internal const string FnOutbDeliveryChange = "BAPI_OUTB_DELIVERY_CHANGE";

    internal static RfcRequest BuildDeliveryChangeRequest(DeliveryChangeRequest body)
    {
        var deliv = SapPad.Pad(body.DeliveryNumber, 10);

        var builder = new RfcRequestBuilder(FnOutbDeliveryChange)
            // Confirmed via the real BAPI Inspector signature (Normanton-
            // Nexus, 2026-08-28): HEADER_DATA (BAPIOBDLVHDRCHG) is a real,
            // separate import parameter from HEADER_CONTROL -- previously
            // left entirely unset, on an unconfirmed guess that
            // HEADER_CONTROL's key field alone was enough. Testing whether
            // that blank HEADER_DATA-DELIV_NUMB is the cause of VL 302
            // ("Delivery & does not exist") once quantity-consistency
            // (VLBAPI 004/VL 268) was already resolved.
            .StructImport("HEADER_DATA", new { DELIV_NUMB = deliv })
            .StructImport("HEADER_CONTROL", new { DELIV_NUMB = deliv })
            .StructImport("TECHN_CONTROL", new { DEBUG_FLG = "" });

        foreach (var item in body.Items)
        {
            var posnr = SapPad.Pad(item.ItemNumber, 6);

            builder.TableRow("ITEM_CONTROL", new
            {
                DELIV_NUMB = deliv,
                DELIV_ITEM = posnr,
                CHG_DELQTY = "X",
            });

            // SALES_UNIT/DLV_QTY_IMUNIT/SALES_UNIT_ISO/BASE_UOM_ISO are all
            // required or SAP rejects the whole call with VLBAPI 004
            // ("quantity consistency check") — confirmed live, see this
            // class's header comment. DLV_QTY_IMUNIT (quantity in base UoM)
            // is derived as a straight copy of Quantity, only correct when
            // SalesUnit/BaseUom are the same unit — the common case, but see
            // DeliveryChangeItem.SalesUnit's doc comment if that's ever not
            // true for a real material. ISO codes default to the plain unit
            // text — see SalesUnitIso/BaseUomIso's doc comments for why
            // that's confirmed correct for "EA" specifically but not a
            // universal rule.
            var baseUom   = item.BaseUom ?? "";
            var salesUnit = string.IsNullOrWhiteSpace(item.SalesUnit) ? baseUom : item.SalesUnit;
            var salesUnitIso = string.IsNullOrWhiteSpace(item.SalesUnitIso) ? salesUnit : item.SalesUnitIso;
            var baseUomIso   = string.IsNullOrWhiteSpace(item.BaseUomIso) ? baseUom : item.BaseUomIso;
            // Confirmed live: VL 268 ("Conversion factors 0:0 are zero, not
            // defined mathematically") fires when these are left unset —
            // default to 1:1, correct whenever SalesUnit equals BaseUom
            // (the common case, and confirmed real LIPS-UMVKZ/UMVKN for the
            // delivery this was diagnosed against). See
            // DeliveryChangeItem.FactUnitNom's doc comment.
            var factUnitNom   = item.FactUnitNom   ?? 1m;
            var factUnitDenom = item.FactUnitDenom ?? 1m;

            builder.TableRow("ITEM_DATA", new
            {
                DELIV_NUMB      = deliv,
                DELIV_ITEM      = posnr,
                MATERIAL        = string.IsNullOrWhiteSpace(item.Material) ? "" : SapPad.Pad(item.Material, 18),
                DLV_QTY         = item.Quantity,
                DLV_QTY_IMUNIT  = item.Quantity,
                SALES_UNIT      = salesUnit,
                SALES_UNIT_ISO  = salesUnitIso,
                BASE_UOM        = baseUom,
                BASE_UOM_ISO    = baseUomIso,
                FACT_UNIT_NOM   = factUnitNom,
                FACT_UNIT_DENOM = factUnitDenom,
                CONV_FACT       = (double)(factUnitNom / factUnitDenom),
            });
        }

        return builder
            .ReadTable("RETURN", "TYPE", "ID", "NUMBER", "MESSAGE", "LOG_NO", "LOG_MSG_NO",
                                  "MESSAGE_V1", "MESSAGE_V2", "MESSAGE_V3", "MESSAGE_V4",
                                  "PARAMETER", "ROW", "FIELD", "SYSTEM")
            .Build();
    }

    internal static DeliveryChangeResponse ParseDeliveryChangeResponse(RfcResponse response, string deliveryNumber)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        return new DeliveryChangeResponse
        {
            DeliveryNumber = deliveryNumber,
            Success        = !ReturnTableHelper.HasBlockingError(
                                 messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))),
            Messages       = messages,
        };
    }
}
