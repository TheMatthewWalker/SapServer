using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Goods Issue posting via BAPI_OUTB_DELIVERY_CONFIRM_DEC. See
/// GoodsIssueModels.cs for why this replaced an earlier BAPI_DELIVERYPROCESSING_EXEC
/// attempt that never worked live.
/// </summary>
internal static class GoodsIssueHelper
{
    internal const string FnOutbDeliveryConfirmDec = "BAPI_OUTB_DELIVERY_CONFIRM_DEC";

    internal static RfcRequest BuildGoodsIssueRequest(GoodsIssueRequest body)
    {
        var deliv = SapPad.Pad(body.DeliveryNumber, 10);

        // Confirmed via the real BAPI Inspector signature (2026-08-28):
        // POST_GI_FLG lives on HEADER_CONTROL (BAPIOBDLVHDRCTRLCON), not
        // HEADER_DATA — the user's original sample code had it on the wrong
        // structure (a real, working call still needs HEADER_CONTROL's own
        // DELIV_NUMB set as the key). HEADER_DATA-DELIV_NUMB is set
        // defensively alongside it, same lesson learned from
        // BAPI_OUTB_DELIVERY_CHANGE needing both HEADER_DATA and
        // HEADER_CONTROL populated with DELIV_NUMB before it stopped
        // rejecting with "Delivery & does not exist" (VL 302).
        var builder = new RfcRequestBuilder(FnOutbDeliveryConfirmDec)
            .StructImport("HEADER_DATA", new { DELIV_NUMB = deliv })
            .StructImport("HEADER_CONTROL", new { DELIV_NUMB = deliv, POST_GI_FLG = "X" });

        // Confirmed live: POST_GI_FLG alone isn't enough -- SAP rejected
        // with "Delivery has not yet been put away / picked (completely)"
        // for every item until ITEM_DATA_SPL rows confirmed each item's
        // picked quantity. See GoodsIssueItem's doc comment.
        foreach (var item in body.Items)
        {
            builder.TableRow("ITEM_DATA_SPL", new
            {
                DELIV_NUMB = deliv,
                DELIV_ITEM = SapPad.Pad(item.ItemNumber, 6),
                QTY_POST   = item.Quantity,
                BASE_UOM   = item.BaseUom ?? "",
            });
        }

        return builder
            .ReadTable("RETURN", "TYPE", "ID", "NUMBER", "MESSAGE", "LOG_NO", "LOG_MSG_NO",
                                  "MESSAGE_V1", "MESSAGE_V2", "MESSAGE_V3", "MESSAGE_V4",
                                  "PARAMETER", "ROW", "FIELD", "SYSTEM")
            .Build();
    }

    internal static GoodsIssueResponse ParseGoodsIssueResponse(RfcResponse response, string deliveryNumber)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        return new GoodsIssueResponse
        {
            DeliveryNumber = deliveryNumber,
            Success        = !ReturnTableHelper.HasBlockingError(
                                 messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))),
            Messages       = messages,
        };
    }
}
