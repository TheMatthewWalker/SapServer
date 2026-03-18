using Microsoft.AspNetCore.Mvc;
using SapServer.Exceptions;
using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;
using SapServer.Services.Interfaces;

namespace SapServer.Controllers;

[Route("api/warehouse")]
public sealed class WarehouseController : SapControllerBase
{
    private const string FnReadTables = "ZRFC_READ_TABLES";
    private const string FnCreateTo   = "L_TO_CREATE_SINGLE";
    private const string FnConsignmentDec = "Z_RFC_CALL_TRANSACTION";
    private const string Warehouse    = "312";
    private const string Plant        = "3012";

    // Column order must exactly match query_FIELDS registration order below
    private static readonly string[] LquaColumns =
        ["LGORT", "LGTYP", "LGPLA", "MATNR", "VERME", "CHARG", "BESTQ", "SOBKZ", "SONUM"];

    public WarehouseController(
        ISapConnectionPool pool,
        IPermissionService permissions,
        ILogger<WarehouseController> logger)
        : base(pool, permissions, logger) { }

    // ── GET /api/warehouse/stock ─────────────────────────────────────────────

    [HttpGet("stock")]
    [ProducesResponseType(typeof(ApiResponse<StockRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStock([FromQuery] StockQuery query, CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnReadTables, ct);

        var response = await _pool.ExecuteAsync(BuildStockRequest(query), ct);
        return Ok(ApiResponse<StockRow[]>.Ok(ParseStockRows(response)));
    }

    // ── GET /api/warehouse/stock/totals ──────────────────────────────────────

    [HttpGet("stock/totals")]
    [ProducesResponseType(typeof(ApiResponse<MaterialTotalRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStockTotals([FromQuery] StockQuery query, CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnReadTables, ct);

        var response = await _pool.ExecuteAsync(BuildStockRequest(query), ct);
        var rows     = ParseStockRows(response);

        var totals = rows
            .GroupBy(r => r.Material)
            .Select(g => new MaterialTotalRow
            {
                Material   = g.Key,
                TotalQty   = g.Sum(r => r.AvailableQty),
                QuantCount = g.Count()
            })
            .OrderBy(r => r.Material)
            .ToArray();

        return Ok(ApiResponse<MaterialTotalRow[]>.Ok(totals));
    }

    // ── GET /api/warehouse/stock/bins ────────────────────────────────────────

    [HttpGet("stock/bins")]
    [ProducesResponseType(typeof(ApiResponse<BinSummaryRow[]>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> GetStockBins([FromQuery] StockQuery query, CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnReadTables, ct);

        var response = await _pool.ExecuteAsync(BuildStockRequest(query), ct);
        var rows     = ParseStockRows(response);

        var bins = rows
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

        return Ok(ApiResponse<BinSummaryRow[]>.Ok(bins));
    }

    // ── POST /api/warehouse/transfer-order ───────────────────────────────────

    [HttpPost("transfer-order")]
    [ProducesResponseType(typeof(ApiResponse<CreateTransferOrderResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> CreateTransferOrder(
        [FromBody] CreateTransferOrderRequest body,
        CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnCreateTo, ct);

        var request = new RfcRequestBuilder(FnCreateTo)
            .Import("I_LGNUM", Warehouse)
            .Import("I_WERKS", Plant)
            .Import("I_LGORT", body.StorageLocation)
            .Import("I_SQUIT", "X")
            .Import("I_BWLVS", "999")
            .Import("I_MATNR", SapPad.Pad(body.Material, 18))
            .Import("I_ANFME", body.Quantity)
            .Import("I_CHARG", SapPad.Pad(body.Batch ?? "", 10))
            .Import("I_ZEUGN", SapPad.Pad(body.Batch ?? "", 10))
            .Import("I_VLTYP", body.SourceBinType)
            .Import("I_VLPLA", SapPad.Pad(body.SourceBin, 10))
            .Import("I_BESTQ", body.StockCategory ?? "")
            .Import("I_SOBKZ", body.SpecialStockIndicator ?? "")
            .Import("I_SONUM", SapPad.Pad(body.SpecialStockNumber ?? "", 16))
            .Import("I_NLPLA", SapPad.Pad(body.DestinationBin, 10))
            .Import("I_NLTYP", body.DestinationBinType)
            .ReadParam("E_TANUM")
            .ReadTable("RETURN", "TYPE", "MESSAGE")
            .Build();

        var response = await _pool.ExecuteAsync(request, ct);
        var messages = ReturnTableHelper.ExtractMessages(response, "RETURN");

        if (ReturnTableHelper.HasBlockingError(messages))
        {
            string errorMsg = messages.First(m => m.Type is "E" or "A").Message;
            throw new SapExecutionException(FnCreateTo, errorMsg, errorMsg);
        }

        string toNumber = ReturnTableHelper.GetParam(response, "E_TANUM") ?? "";

        var result = new CreateTransferOrderResponse
        {
            TransferOrderNumber = toNumber,
            Success             = true,
            Messages            = messages
                .Select(m => new SapReturnMessage { Type = m.Type, Message = m.Message })
                .ToList()
        };

        return Ok(ApiResponse<CreateTransferOrderResponse>.Ok(result));
    }

    // ── POST /api/warehouse/consignment-mb1b ─────────────────────────────────

    [HttpPost("consignment-mb1b")]
    [ProducesResponseType(typeof(ApiResponse<ConsignmentMb1bResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 403)]
    public async Task<IActionResult> ConsignmentMb1b(
        [FromBody] ConsignmentMb1bRequest body,
        CancellationToken ct)
    {
        int userId = GetUserId();
        await CheckPermissionAsync(userId, FnConsignmentDec, ct);

        string mat    = SapPad.Pad(body.Material, 18);
        string qty    = body.Quantity.ToString();
        string vendor = SapPad.Pad(body.Vendor, 10);

        // Step 1: MB1B — goods movement 411/K (consignment receipt)
        var mb1bResp = await _pool.ExecuteAsync(
            BdcBuilder.For("MB1B")
                .Screen("SAPMM07M", "0400")
                    .Field("BDC_OKCODE",    "/00")
                    .Field("RM07M-MTSNR",   body.DeliveryNote)
                    .Field("MKPF-BKTXT",    body.Header)
                    .Field("RM07M-BWARTWA", "411")
                    .Field("RM07M-SOBKZ",   "K")
                    .Field("RM07M-WERKS",   Plant)
                    .Field("RM07M-LGORT",   body.StorageLocation)
                    .Field("XFULL",         "")
                .Screen("SAPMM07M", "0421")
                    .Field("BDC_OKCODE",     "/00")
                    .Field("MSEGK-LIFNR",    vendor)
                    .Field("MSEGK-UMLGO",    "1710")
                    .Field("MSEG-MATNR(01)", mat)
                    .Field("MSEG-ERFMG(01)", qty)
                .Screen("SAPMM07M", "0421")
                    .Field("BDC_OKCODE", "=BU")
                .Build(),
            ct);

        // Step 2: LT01 — move non-consignment stock from BLOCK/922 to destination bin
        var toNonConsignResp = await _pool.ExecuteAsync(
            BdcBuilder.For("LT01")
                .Screen("SAPML03T", "0101")
                    .Field("BDC_OKCODE",  "/00")
                    .Field("LTAK-LGNUM",  Warehouse)
                    .Field("LTAK-BWLVS",  "999")
                    .Field("LTAP-MATNR",  mat)
                    .Field("RL03T-ANFME", qty)
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
                .Build(),
            ct);

        // Step 3: LT01 — move consignment stock from source bin back to BLOCK/922
        var toConsignResp = await _pool.ExecuteAsync(
            BdcBuilder.For("LT01")
                .Screen("SAPML03T", "0101")
                    .Field("BDC_OKCODE",  "/00")
                    .Field("LTAK-LGNUM",  Warehouse)
                    .Field("LTAK-BWLVS",  "999")
                    .Field("LTAP-MATNR",  mat)
                    .Field("RL03T-ANFME", qty)
                    .Field("LTAP-WERKS",  Plant)
                    .Field("LTAP-LGORT",  body.StorageLocation)
                    .Field("LTAP-ZEUGN",  "")
                    .Field("LTAP-CHARG",  "")
                    .Field("LTAP-SOBKZ",  "K")
                    .Field("RL03T-LSONR", vendor)
                .Screen("SAPML03T", "0102")
                    .Field("BDC_OKCODE",  "/00")
                    .Field("RL03T-SQUIT", "X")
                    .Field("LTAP-VLTYP",  body.SourceType)
                    .Field("LTAP-VLPLA",  body.SourceBin)
                    .Field("LTAP-NLTYP",  "922")
                    .Field("LTAP-NLPLA",  "BLOCK")
                .Build(),
            ct);

        var result = new ConsignmentMb1bResponse
        {
            Mb1bMessage         = ReturnTableHelper.GetParam(mb1bResp,         "MESSG") ?? "",
            ToNonConsignMessage = ReturnTableHelper.GetParam(toNonConsignResp, "MESSG") ?? "",
            ToConsignMessage    = ReturnTableHelper.GetParam(toConsignResp,    "MESSG") ?? ""
        };

        return Ok(ApiResponse<ConsignmentMb1bResponse>.Ok(result));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static RfcRequest BuildStockRequest(StockQuery query)
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

    private static StockRow[] ParseStockRows(RfcResponse response)
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
}
