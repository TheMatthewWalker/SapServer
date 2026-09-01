using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

// ── DisplayStock ─────────────────────────────────────────────────────────────

/// <summary>Optional filters for stock queries. Bound from [FromQuery] parameters.</summary>
public sealed class StockQuery
{
    public string? Material        { get; init; }
    public string? StorageType     { get; init; }
    // Excludes rows matching this storage type (LQUA~LGTYP NE) rather than
    // filtering to it — e.g. the Weekly PTFE Cycle Count excludes bin type
    // 'SA' (sample/quality stock, not real countable inventory). Independent
    // of StorageType above; setting both applies both conditions.
    public string? ExcludeStorageType { get; init; }
    public string? Bin             { get; init; }
    public string? Batch           { get; init; }
    public string? StorageLocation { get; init; } // LGORT
    public string? StockCategory   { get; init; } // BESTQ
    // Not an LQUA field (PRCTR lives on MARC) — applied by WarehouseController
    // as a post-filter once rows are joined against the material→profit-centre
    // lookup, not part of BuildStockRequest's WHERE clause.
    public string? ProfitCentre    { get; init; }
    public int     RowCount        { get; init; } = 9999;
}

// ── IM (inventory-managed-only) stock — MARD ────────────────────────────────
//
// Confirmed against the real SAP system: storage location 1716 (Production
// Count) has no WM/bin concept and does not appear in LQUA at all — use MARD
// (plant/storage-location-level unrestricted stock, LABST) instead, via the
// same ZRFC_READ_TABLES mechanism. No StorageType/Bin fields here — MARD has
// no such concept.
public sealed class ImStockQuery
{
    public string? Material        { get; init; }
    [Required] public string StorageLocation { get; init; } = string.Empty; // LGORT
    public int     RowCount        { get; init; } = 9999;
}

/// <summary>A single row from the MARD table — unrestricted-use stock (LABST) for one material at one plant/storage location.</summary>
public sealed class ImStockRow
{
    public string  Plant           { get; init; } = string.Empty; // WERKS
    public string  StorageLocation { get; init; } = string.Empty; // LGORT
    public string  Material        { get; init; } = string.Empty; // MATNR
    public decimal AvailableQty    { get; init; }                  // LABST
}

/// <summary>A single quant row from the LQUA table.</summary>
public sealed class StockRow
{
    public string  StorageLocation { get; init; } = string.Empty; // LGORT
    public string  StorageType     { get; init; } = string.Empty; // LGTYP
    public string  Bin             { get; init; } = string.Empty; // LGPLA
    public string  Material        { get; init; } = string.Empty; // MATNR
    public decimal AvailableQty    { get; init; }                  // VERME
    public string  Batch           { get; init; } = string.Empty; // CHARG
    public string  StockCategory   { get; init; } = string.Empty; // BESTQ
    public string  SpecialStockInd { get; init; } = string.Empty; // SOBKZ
    public string  SpecialStockNum { get; init; } = string.Empty; // SONUM
    public string  GrDate          { get; init; } = string.Empty; // WDATU, raw yyyyMMdd
    public string  ProfitCentre    { get; init; } = string.Empty; // MARC-PRCTR, looked up by material
}

/// <summary>Total available quantity per material number.</summary>
public sealed class MaterialTotalRow
{
    public string  Material   { get; init; } = string.Empty;
    public decimal TotalQty   { get; init; }
    public int     QuantCount { get; init; }
}

/// <summary>Quant count and total quantity per storage type + bin.</summary>
public sealed class BinSummaryRow
{
    public string  StorageType { get; init; } = string.Empty;
    public string  Bin         { get; init; } = string.Empty;
    public int     QuantCount  { get; init; }
    public decimal TotalQty    { get; init; }
}

// ── CreateTransferOrder ──────────────────────────────────────────────────────

public sealed class CreateTransferOrderRequest
{
    // Required
    public string  StorageLocation    { get; init; } = string.Empty; // I_LGORT
    public string  Material           { get; init; } = string.Empty; // I_MATNR (padded to 18)
    public decimal Quantity           { get; init; }                  // I_ANFME
    public string  SourceType      { get; init; } = string.Empty; // I_VLTYP
    public string  SourceBin          { get; init; } = string.Empty; // I_VLPLA (padded to 10)
    public string  DestinationType { get; init; } = string.Empty; // I_NLTYP
    public string  DestinationBin     { get; init; } = string.Empty; // I_NLPLA (padded to 10)

    // Optional
    public string? Batch                 { get; init; }  // I_CHARG + I_ZEUGN (padded to 10)
    public string? StockCategory         { get; init; }  // I_BESTQ
    public string? SpecialStockIndicator { get; init; }  // I_SOBKZ
    public string? SpecialStockNumber    { get; init; }  // I_SONUM (padded to 16)
}

public sealed class CreateTransferOrderResponse
{
    public string               TransferOrderNumber { get; init; } = string.Empty;
    public bool                 Success             { get; init; }
    public List<SapReturnMessage> Messages          { get; init; } = [];
}


public sealed class TransferOrderWrapper
{
    public List<CreateTransferOrderResponse> Responses { get; init; } = [];
}


// ── ConsignmentMb1b ──────────────────────────────────────────────────────────

public sealed class ConsignmentMb1bRequest
{
    [Required, MinLength(1)] public string  Material        { get; init; } = string.Empty; // MATNR → MSEG-MATNR(01), LTAP-MATNR
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity        { get; init; }                  // ANFME → MSEG-ERFMG(01), RL03T-ANFME
    [Required, MinLength(1)] public string  Header          { get; init; } = string.Empty; // MKPF-BKTXT
    [Required, MinLength(1)] public string  SpecialStockNumber { get; init; } = string.Empty; // LIFNR → MSEGK-LIFNR, RL03T-LSONR
    [Required, MinLength(1)] public string  StorageLocation { get; init; } = string.Empty; // LGORT → RM07M-LGORT, LTAP-LGORT
    [Required, MinLength(1)] public string  SourceType      { get; init; } = string.Empty; // LGTYP → LTAP-VLTYP (non-consign source) / LTAP-NLTYP (consign dest)
    [Required, MinLength(1)] public string  SourceBin       { get; init; } = string.Empty; // LGPLA → LTAP-VLPLA (non-consign source) / LTAP-NLPLA (consign dest)
    [Required, MinLength(1)] public string  DestinationType { get; init; } = string.Empty; // LGTYP → LTAP-NLTYP (non-consign dest) / LTAP-VLTYP (consign source)
    [Required, MinLength(1)] public string  DestinationBin  { get; init; } = string.Empty; // LGPLA → LTAP-NLPLA (non-consign dest) / LTAP-VLPLA (consign source)
                             public string  DeliveryNote    { get; init; } = string.Empty; // RM07M-MTSNR (optional)

    /// <summary>
    /// If true, asks SAP to simulate the MB1B posting (GOODSMVT_HEADER/TESTRUN "X")
    /// without actually creating a material document — the two LT01 legs are
    /// skipped entirely in this case, same reasoning as StockAdjustmentRequest.TestRun:
    /// posting real transfer orders against a simulated MB1B doc would be meaningless.
    /// </summary>
    public bool TestRun { get; init; }
}

public sealed class ConsignmentMb1bResponse
{
    // False if any of the three legs (MB1B goods issue, then the two LT01
    // transfer postings) came back with an SAP error message — the stock
    // never actually moved as intended even though every RFC call
    // succeeded at the protocol level. See WarehouseHelpers.ParseConsignmentResponse.
    public bool   Success               { get; init; }
    public string Mb1bMessage           { get; init; } = string.Empty;
    public string ToNonConsignMessage   { get; init; } = string.Empty;
    public string ToConsignMessage      { get; init; } = string.Empty;
}


// ── Open Transfer Requirements (LTBK/LTBP) ──────────────────────────────────
//
// Backs the "Transfer Requirements (LT04)" tile — replicates the list this
// warehouse team's existing Excel macro (wm_open_tr.xltm, Get_LAGP_LQUA sub)
// has always shown operators: open TRs auto-created by a 131 goods movement,
// ready to be turned into a confirmed TO via LT04. See WarehouseHelpers.
// BuildOpenTransferRequirementsRequest for the exact join this mirrors.

public sealed class OpenTransferRequirementRow
{
    public string  TrNumber         { get; init; } = string.Empty; // LTBP-TBNUM
    public string  Material         { get; init; } = string.Empty; // LTBP-MATNR
    public string  StorageLocation  { get; init; } = string.Empty; // LTBP-LGORT
    public decimal Quantity         { get; init; }                  // LTBP-MENGE
    public string  Uom              { get; init; } = string.Empty; // LTBP-MEINS
    public string  MrpController    { get; init; } = string.Empty; // MARC-DISPO
    public string  DocumentText     { get; init; } = string.Empty; // MKPF-BKTXT
    public string  MaterialDocument { get; init; } = string.Empty; // LTBK-MBLNR
    public string  CreatedBy        { get; init; } = string.Empty; // LTBK-BNAME
    public string  CreatedDate      { get; init; } = string.Empty; // LTBK-BDATU
    public string  CreatedTime      { get; init; } = string.Empty; // LTBK-BZEIT
    public string  MovementType     { get; init; } = string.Empty; // LTBK-BWLVS

    // LTBP-CHARG — a TR is one-to-one with a batch, so surfacing it here
    // means the operator never has to type Pallet/Batch anywhere in the LT04
    // flow (scan, modal, or bulk multi-select); it's simply read off the row.
    public string  Batch            { get; init; } = string.Empty;
}

/// <summary>Optional filters for the open-TR list. Bound from [FromQuery] parameters.</summary>
public sealed class OpenTransferRequirementsQuery
{
    public string? MrpController   { get; init; }
    public string? Material        { get; init; }
    public string? StorageLocation { get; init; } // LGORT
    public string? CreatedBy       { get; init; } // LTBK-BNAME
}

// ── Create LT04 (create + auto-confirm TO from an open TR) ─────────────────
//
// Replicates transaction LT04 exactly as recorded in wm_lt01.xltm's
// ati_code module (create_LT04 function) — see WarehouseHelpers.
// BuildCreateLt04Request for the full screen-by-screen mapping. The
// destination storage type/bin are operator-entered (this warehouse's LT04
// process has never used automatic bin determination), matching the
// existing manual workflow exactly.

public sealed class CreateLt04Request
{
    [Required, MinLength(1)] public string  TrNumber        { get; init; } = string.Empty; // LTBK-TBNUM
    [Required, MinLength(1)] public string  Material        { get; init; } = string.Empty; // quality pre-check (LQUA-MATNR), padded to 18
    [Range(0.001, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
                             public decimal Quantity        { get; init; }                  // LTAPE-ANFME(5)
    [Required, MinLength(1)] public string  DestinationType { get; init; } = string.Empty; // LTAPE-NLTYP(5)
    [Required, MinLength(1)] public string  DestinationBin  { get; init; } = string.Empty; // LTAPE-NLPLA(5), padded to 10
    [Required, MinLength(1)] public string  PalletOrBatch   { get; init; } = string.Empty; // "pnr" — quality pre-check (LQUA-CHARG) + default LTAP-ZEUGN reference, padded to 10
                             public string? Reference       { get; init; }                  // "charge" override for LTAP-ZEUGN — defaults to PalletOrBatch when blank, exactly as recorded
}

// Response: reuses SapServer.Models.Bapi.BdcResponse (ProductionHelpers.
// ParseBdcResponse) exactly like ProductionController.Backflush does — a
// single BDC call with one MESSG result doesn't need its own wrapper type.
// Type == "S" is success, matching create_LT04's own Left(msg,1)="S" check.


// ── Delete TR (LB02) ────────────────────────────────────────────────────────
//
// Replicates wm_open_tr.xlsm's ati_code.delete_tr sub — see
// WarehouseHelpers.BuildDeleteTrRequest/BuildDeleteTrFallbackRequest for the
// full screen-by-screen mapping.

public sealed class DeleteTrRequest
{
    [Required, MinLength(1)] public string TrNumber { get; init; } = string.Empty; // LTBK-TBNUM
}

// Response: reuses BdcResponse, same as CreateLt04Request above — no bespoke
// wrapper needed for a single BDC call with one MESSG result.


// ── TR Cleanup Candidates ────────────────────────────────────────────────────
//
// Backs the "Cleanup Assistant" — an automated version of the judgment call
// wm_open_tr.xlsm's operators have always made by eyeballing the macro's raw
// data columns. See WarehouseHelpers.BuildTrCleanupCandidateRows for the
// three reason conditions this evaluates.

public sealed class TrCleanupCandidateRow
{
    public string   TrNumber        { get; init; } = string.Empty;
    public string   Material        { get; init; } = string.Empty;
    public string   Batch           { get; init; } = string.Empty;
    public string   StorageLocation { get; init; } = string.Empty;
    public decimal  Quantity        { get; init; }
    public string   Uom             { get; init; } = string.Empty;
    public string   MrpController   { get; init; } = string.Empty;

    // "sloc_1710" | "no_stock" | "already_transferred" — a TR can carry more
    // than one reason at once (see WarehouseHelpers.ReasonSloc1710/
    // ReasonNoStock/ReasonAlreadyTransferred).
    public string[] Reasons         { get; init; } = [];
}


// ── SetDeliveryWeight (ZDEL) ─────────────────────────────────────────────────
//
// Records the delivery's actual picked/packed figures back onto LIKP once a
// delivery is marked complete in the pallet builder — the BDC mirrors
// transaction ZDEL exactly as recorded: select the delivery (=SELE), then
// enter BTGEW/NTGEW/GEWEI/ANZPK and save (=SAVE).

public sealed class SetDeliveryWeightRequest
{
    [Required, MinLength(1)] public string  DeliveryNumber { get; init; } = string.Empty; // VBELN → LIKP-VBELN
    [Range(0, double.MaxValue)] public decimal GrossWeight  { get; init; }                 // LIKP-BTGEW
    [Range(0, double.MaxValue)] public decimal NetWeight    { get; init; }                 // LIKP-NTGEW
    [Range(0, int.MaxValue)]    public int     PalletCount  { get; init; }                 // LIKP-ANZPK
}

public sealed class SetDeliveryWeightResponse
{
    public string Message { get; init; } = string.Empty;
}

// ── SetPickedQuantity (VL02N BDC) ────────────────────────────────────────────
//
// Fixes LIPS-PIKMG (picked quantity) for a delivery's batch-split rows so
// BAPI_OUTB_DELIVERY_CONFIRM_DEC's picking-confirmation check passes. No BAPI
// exposes PIKMG directly (WS_DELIVERY_UPDATE/_2, the internal function
// modules VL02N's own screen flow uses, are confirmed NOT RFC-accessible via
// a real RFC_GET_FUNCTION_INTERFACE check) — this is a deliberately narrow,
// last-resort BDC, not a general VL02N replication. Built from a real SHDB
// recording (not the initial ZDELHAND_9-derived guess, which was tried live
// and confirmed wrong — see WarehouseHelpers.BuildSetPickedQuantityRequest
// for the full diagnosis history and the real, fragile, position-indexed
// table-control mechanics this depends on).
//
// CONFIRMED WORKING END TO END live against delivery 0082291409 (real
// 3-way batch split, 400 EA each): this call returned "S VL 311 Delivery
// ... has been saved", and a subsequent BAPI_OUTB_DELIVERY_CONFIRM_DEC
// TestRun (previously blocked with "Delivery has not yet been put away /
// picked (completely)" on every attempt) came back with zero rejections.

public sealed class SetPickedQuantityRequest
{
    [Required, MinLength(1)] public string DeliveryNumber { get; init; } = string.Empty; // LIKP-VBELN (sent unpadded, matching the real recording)

    // SHDB recorded LIKP-BLDAT/KODAT/KOUHR (billing block date/pricing
    // date/pricing time) as echoed values alongside the picking screen, but
    // only BLDAT is confirmed live to actually be a real, settable field in
    // a BDC replay -- KODAT and KOUHR both rejected with a real "Field ...
    // does not exist in dynpro SAPMV50A 1000" (message class 00, number
    // 349) despite SHDB showing them as populated. Left blank skips sending
    // it; supply the delivery's real current LIKP-BLDAT (a small LIKP read,
    // same convention as zdelflag/likp-ablad) if a live call rejects on a
    // missing/stale header date.
    public string? BillingDate { get; init; } // LIKP-BLDAT, format DD.MM.YYYY

    // One quantity per expanded batch-split row, in the EXACT order SAP's own
    // item-overview table control displays them (confirmed for a real 3-way
    // split to be ascending by the sub-item numbers SAP itself assigned during
    // the BAPI_OUTB_DELIVERY_CHANGE split) — maps to LIPSD-PIKMG(02), (03), (04)...
    // This is a raw table-control ROW POSITION, not a batch/item lookup; a wrong
    // order silently posts one batch's quantity onto another's row with no error
    // from SAP at all. See the helper's doc comment for the full caveat.
    public List<decimal> PickedQuantities { get; init; } = [];
}

public sealed class SetPickedQuantityResponse
{
    public string Message { get; init; } = string.Empty;
}
