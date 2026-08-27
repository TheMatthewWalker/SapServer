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

            builder.TableRow("ITEM_DATA", new
            {
                DELIV_NUMB = deliv,
                DELIV_ITEM = posnr,
                MATERIAL   = string.IsNullOrWhiteSpace(item.Material) ? "" : SapPad.Pad(item.Material, 18),
                DLV_QTY    = item.Quantity,
                BASE_UOM   = item.BaseUom ?? "",
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
