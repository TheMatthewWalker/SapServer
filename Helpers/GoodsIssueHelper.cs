using System.Globalization;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

/// <summary>
/// Goods Issue posting via BAPI_DELIVERYPROCESSING_EXEC. See
/// GoodsIssueModels.cs for the full caveat on why REQUEST's field set is a
/// deliberately minimal starting point pending live confirmation, and why
/// RETURN-parsing here (unlike ZDELFLAG's ET_MESSAGE) is low-risk — it's a
/// standard BAPIRET2 table.
/// </summary>
internal static class GoodsIssueHelper
{
    internal const string FnDeliveryProcessingExec = "BAPI_DELIVERYPROCESSING_EXEC";

    internal static RfcRequest BuildGoodsIssueRequest(GoodsIssueRequest body)
    {
        var deliveryDate   = NormaliseDate(body.DeliveryDate);
        var goodsIssueDate = NormaliseDate(body.GoodsIssueDate);
        var checkMode      = body.CheckMode || body.TestRun;

        var builder = new RfcRequestBuilder(FnDeliveryProcessingExec)
            .StructImport("DELIVERY_EXTEND", new
            {
                DELIVERY_NUMBER      = SapPad.Pad(body.DeliveryNumber, 10),
                NEW_DELIVERY_ALLOWED = body.NewDeliveryAllowed ? "X" : "",
            })
            .StructImport("TECHN_CONTROL", new
            {
                DEBUG_FLG         = body.Debug ? "X" : "",
                SENDER_SYSTEM     = "",
                PROCESS_GUID      = "",
                ERROR_TOLERANCE   = "",
                CHECK_MODE        = checkMode ? "X" : "",
                IDOCNUM           = "",
                APOTRGUID         = "",
                SPE_SCENARIO_FLAG = "",
                // Deliberately left off (not "X") for phase 0 so the first
                // live tests are synchronous/observable in one round-trip —
                // see GoodsIssueModels.cs's header comment.
                POST_ASYNC        = "",
            });

        // REQUEST: one header-only row for phase 0 (see GoodsIssueModels.cs's
        // caveat) — DOCUMENT_NUMB identifies the delivery, the two dates are
        // the only other fields we have reason to believe matter yet.
        builder.TableRow("REQUEST", new
        {
            DOCUMENT_NUMB    = SapPad.Pad(body.DeliveryNumber, 10),
            DELIVERY_DATE    = deliveryDate,
            GOODS_ISSUE_DATE = goodsIssueDate,
        });

        return builder
            .ReadTable("RETURN", "TYPE", "ID", "NUMBER", "MESSAGE", "LOG_NO", "LOG_MSG_NO",
                                  "MESSAGE_V1", "MESSAGE_V2", "MESSAGE_V3", "MESSAGE_V4",
                                  "PARAMETER", "ROW", "FIELD", "SYSTEM")
            .ReadTable("CREATEDITEMS", "DOCUMENT_NUMB", "DOCUMENT_ITEM", "MATERIAL",
                                        "QUANTITY_SALES_UOM", "SALES_UNIT")
            .Build();
    }

    internal static GoodsIssueResponse ParseGoodsIssueResponse(RfcResponse response, string deliveryNumber)
    {
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN")
            .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
            .ToList();

        var createdCount = response.Tables.TryGetValue("CREATEDITEMS", out var rows) ? rows.Count : 0;

        return new GoodsIssueResponse
        {
            DeliveryNumber   = deliveryNumber,
            Success          = !ReturnTableHelper.HasBlockingError(
                                    messages.Select(m => new ReturnTableHelper.SapMessage(m.Type, m.Message))),
            Messages         = messages,
            CreatedItemCount = createdCount,
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
