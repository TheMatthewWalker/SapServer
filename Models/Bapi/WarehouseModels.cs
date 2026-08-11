using System.ComponentModel.DataAnnotations;

namespace SapServer.Models.Bapi;

// ── DisplayStock ─────────────────────────────────────────────────────────────

/// <summary>Optional filters for stock queries. Bound from [FromQuery] parameters.</summary>
public sealed class StockQuery
{
    public string? Material        { get; init; }
    public string? StorageType     { get; init; }
    public string? Bin             { get; init; }
    public string? Batch           { get; init; }
    public string? StorageLocation { get; init; } // LGORT
    public string? StockCategory   { get; init; } // BESTQ
    public int     RowCount        { get; init; } = 9999;
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
