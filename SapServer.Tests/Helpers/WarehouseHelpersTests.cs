using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class WarehouseHelpersTests
{
    private static RfcResponse StockResponse(params string[] dataRows)
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["WA"] = "header|row|skipped" } };
        rows.AddRange(dataRows.Select(r => new Dictionary<string, object?> { ["WA"] = r }));
        return new RfcResponse { Tables = new() { ["data_display"] = rows } };
    }

    [Fact]
    public void ParseStockRows_maps_LQUA_columns_in_order()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|12.5|BATCH1|F|Q|SO123|20260110");

        var rows = WarehouseHelpers.ParseStockRows(response);

        Assert.Single(rows);
        Assert.Equal("1710", rows[0].StorageLocation);
        Assert.Equal("SA", rows[0].StorageType);
        Assert.Equal("BIN-001", rows[0].Bin);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal(12.5m, rows[0].AvailableQty);
        Assert.Equal("BATCH1", rows[0].Batch);
        Assert.Equal("20260110", rows[0].GrDate);
    }

    [Fact]
    public void ParseStockRows_unparsable_quantity_defaults_to_zero_rather_than_throwing()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|not-a-number|BATCH1|F|Q|SO123|20260110");
        var rows = WarehouseHelpers.ParseStockRows(response);
        Assert.Equal(0m, rows[0].AvailableQty);
    }

    [Fact]
    public void ParseStockRows_looks_up_ProfitCentre_by_normalised_material_when_a_dictionary_is_supplied()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|12.5|BATCH1|F|Q|SO123|20260110");
        var profitCentres = new Dictionary<string, string> { ["30005R"] = "9912" };

        var rows = WarehouseHelpers.ParseStockRows(response, profitCentres);

        Assert.Equal("9912", rows[0].ProfitCentre);
    }

    [Fact]
    public void ParseStockRows_ProfitCentre_is_empty_when_no_dictionary_is_supplied_or_material_is_unmatched()
    {
        var response = StockResponse("1710|SA|BIN-001|30005R|12.5|BATCH1|F|Q|SO123|20260110");

        Assert.Equal("", WarehouseHelpers.ParseStockRows(response)[0].ProfitCentre);
        Assert.Equal("", WarehouseHelpers.ParseStockRows(response, new Dictionary<string, string>())[0].ProfitCentre);
    }

    [Fact]
    public void AggregateByMaterial_sums_quantity_and_counts_quants_per_material_sorted_alphabetically()
    {
        var stock = new[]
        {
            new StockRow { Material = "30006R", AvailableQty = 5m },
            new StockRow { Material = "30005R", AvailableQty = 10m },
            new StockRow { Material = "30005R", AvailableQty = 2.5m },
        };

        var totals = WarehouseHelpers.AggregateByMaterial(stock);

        Assert.Equal(2, totals.Length);
        Assert.Equal("30005R", totals[0].Material); // alphabetical, not insertion order
        Assert.Equal(12.5m, totals[0].TotalQty);
        Assert.Equal(2, totals[0].QuantCount);
        Assert.Equal("30006R", totals[1].Material);
    }

    [Fact]
    public void AggregateByBin_groups_by_storage_type_and_bin_together_not_bin_alone()
    {
        var stock = new[]
        {
            new StockRow { StorageType = "SA", Bin = "BIN-001", AvailableQty = 5m },
            new StockRow { StorageType = "SB", Bin = "BIN-001", AvailableQty = 3m }, // same bin, different type
        };

        var bins = WarehouseHelpers.AggregateByBin(stock);

        Assert.Equal(2, bins.Length); // must NOT collapse into one "BIN-001" row
    }

    [Fact]
    public void ParseTransferOrderResponse_reads_the_TO_number_and_RETURN_messages()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["E_TANUM"] = "0000001234" },
            Tables = new()
            {
                ["RETURN"] = new()
                {
                    new() { ["TYPE"] = "S", ["MESSAGE"] = "Transfer order 1234 created" },
                },
            },
        };

        var result = WarehouseHelpers.ParseTransferOrderResponse(response);

        Assert.Equal("0000001234", result.TransferOrderNumber);
        Assert.True(result.Success);
        Assert.Single(result.Messages);
        Assert.Equal("S", result.Messages[0].Type);
    }

    // MB1B is now BAPI_GOODSMVT_CREATE — success/failure comes from
    // MATERIALDOCUMENT + a RETURN table, not a BDC MESSG param.
    private static RfcResponse GoodsMvtResponse(string? materialDocument, params (string Type, string Message)[] returnRows) => new()
    {
        Parameters = materialDocument is null ? new() : new() { ["MATERIALDOCUMENT"] = materialDocument },
        Tables = new()
        {
            ["RETURN"] = returnRows.Select(r => new Dictionary<string, object?> { ["TYPE"] = r.Type, ["MESSAGE"] = r.Message }).ToList(),
        },
    };

    // The two LT01 legs are now L_TO_CREATE_SINGLE — same E_TANUM + RETURN
    // shape as ParseTransferOrderResponse's own test above.
    private static RfcResponse ToLegResponse(string? tanum, params (string Type, string Message)[] returnRows) => new()
    {
        Parameters = tanum is null ? new() : new() { ["E_TANUM"] = tanum },
        Tables = new()
        {
            ["RETURN"] = returnRows.Select(r => new Dictionary<string, object?> { ["TYPE"] = r.Type, ["MESSAGE"] = r.Message }).ToList(),
        },
    };

    [Fact]
    public void ParseConsignmentResponse_succeeds_when_all_three_legs_report_success()
    {
        var result = WarehouseHelpers.ParseConsignmentResponse(
            GoodsMvtResponse("4973814993", ("S", "Document posted")),
            ToLegResponse("0000504057", ("S", "Transfer order created")),
            ToLegResponse("0000504058", ("S", "Transfer order created")));

        Assert.True(result.Success);
        Assert.Equal("S Document 4973814993 posted", result.Mb1bMessage);
        Assert.Equal("S Transfer order 0000504057 created", result.ToNonConsignMessage);
    }

    // Regression test: previously ParseConsignmentResponse only kept the raw
    // MESSG text and never looked at the message type, so a rejected MB1B
    // (deficit stock, missing authorization, etc.) was indistinguishable
    // from a successful one to WarehouseController.ConsignmentMb1b — the
    // consignment stock never actually left SAP even though the endpoint
    // reported success. Still holds after the BAPI_GOODSMVT_CREATE switch:
    // no MATERIALDOCUMENT means no posting happened, regardless of RETURN.
    [Fact]
    public void ParseConsignmentResponse_fails_when_the_MB1B_leg_reports_an_SAP_error()
    {
        var result = WarehouseHelpers.ParseConsignmentResponse(
            GoodsMvtResponse(null, ("E", "Deficit of SL stock 5 PC : 30005R 1000 SA B02")),
            ToLegResponse("0000504057", ("S", "Transfer order created")),
            ToLegResponse("0000504058", ("S", "Transfer order created")));

        Assert.False(result.Success);
        Assert.Equal("E Deficit of SL stock 5 PC : 30005R 1000 SA B02", result.Mb1bMessage);
    }

    [Fact]
    public void ParseConsignmentResponse_fails_when_either_LT01_leg_reports_an_SAP_error()
    {
        var toNonConsignFails = WarehouseHelpers.ParseConsignmentResponse(
            GoodsMvtResponse("4973814993", ("S", "Document posted")),
            ToLegResponse(null, ("E", "Bin does not exist")),
            ToLegResponse("0000504058", ("S", "Transfer order created")));
        Assert.False(toNonConsignFails.Success);

        var toConsignFails = WarehouseHelpers.ParseConsignmentResponse(
            GoodsMvtResponse("4973814993", ("S", "Document posted")),
            ToLegResponse("0000504057", ("S", "Transfer order created")),
            ToLegResponse(null, ("E", "Bin does not exist")));
        Assert.False(toConsignFails.Success);
    }

    [Fact]
    public void ParseMb1bOnly_reflects_just_the_MB1B_leg_for_the_TestRun_path()
    {
        var result = WarehouseHelpers.ParseMb1bOnly(GoodsMvtResponse(null, ("E", "TESTRUN — no document created")));

        Assert.False(result.Success);
        Assert.Equal("E TESTRUN — no document created", result.Mb1bMessage);
        Assert.Equal("", result.ToNonConsignMessage);
        Assert.Equal("", result.ToConsignMessage);
    }

    [Fact]
    public void BinExists_is_false_when_only_the_SAP_header_row_comes_back()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGPLA" } } }, // header only, no real hit
        };
        Assert.False(WarehouseHelpers.BinExists(response));
    }

    [Fact]
    public void BinExists_is_true_when_a_real_row_follows_the_header()
    {
        var response = StockResponse("BIN-001");
        Assert.True(WarehouseHelpers.BinExists(response));
    }

    [Fact]
    public void IsQualityBlocked_is_true_only_for_stock_category_Q()
    {
        Assert.True(WarehouseHelpers.IsQualityBlocked(StockResponse("Q")));
        Assert.False(WarehouseHelpers.IsQualityBlocked(StockResponse("F")));
        Assert.False(WarehouseHelpers.IsQualityBlocked(new RfcResponse()));
    }

    [Fact]
    public void BuildStockRequest_only_adds_optional_WHERE_conditions_that_were_actually_supplied()
    {
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { Material = "30005R" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~LGNUM EQ '312'", whereText);
        Assert.Contains("LQUA~MATNR", whereText);
        Assert.DoesNotContain("LGTYP", whereText);
        Assert.DoesNotContain("LGPLA", whereText);
    }

    [Fact]
    public void BuildStockRequest_plain_material_uses_padded_exact_match_EQ()
    {
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { Material = "30005R" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~MATNR EQ '30005R'", whereText);
        Assert.DoesNotContain("LIKE", whereText);
    }

    // Wildcard search is opt-in — only kicks in when the caller actually types
    // a '%' or '_' (native SQL wildcards — this Z-RFC runs a LIKE, not an ABAP
    // CP comparison) — so a plain material search elsewhere keeps behaving
    // exactly as before (see the EQ test above).
    [Theory]
    [InlineData("tshv%",  "LQUA~MATNR LIKE 'TSHV%'")]   // starts with
    [InlineData("%tshv",  "LQUA~MATNR LIKE '%TSHV'")]   // ends with
    [InlineData("%tshv%", "LQUA~MATNR LIKE '%TSHV%'")]  // contains
    [InlineData("tsh_v",  "LQUA~MATNR LIKE 'TSH_V'")]   // single-char wildcard
    public void BuildStockRequest_material_with_a_wildcard_uses_LIKE_pattern_match_unpadded(string material, string expectedCondition)
    {
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { Material = material });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains(expectedCondition, whereText);
        Assert.DoesNotContain("MATNR EQ", whereText);
    }

    // ── IM stock (MARD) — Production Count, storage location 1716 ───────────────

    [Fact]
    public void BuildImStockRequest_filters_by_plant_and_storage_location()
    {
        var request = WarehouseHelpers.BuildImStockRequest(new ImStockQuery { StorageLocation = "1716" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MARD~WERKS EQ '3012'", whereText);
        Assert.Contains("MARD~LGORT EQ '1716'", whereText);
        Assert.DoesNotContain("MATNR", whereText);
    }

    [Fact]
    public void BuildImStockRequest_adds_material_condition_when_supplied()
    {
        var request = WarehouseHelpers.BuildImStockRequest(new ImStockQuery { StorageLocation = "1716", Material = "30005R" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MARD~MATNR", whereText);
    }

    [Fact]
    public void ParseImStockRows_maps_MARD_columns_in_order()
    {
        var response = StockResponse("3012|1716|30005R|42.5");

        var rows = WarehouseHelpers.ParseImStockRows(response);

        Assert.Single(rows);
        Assert.Equal("3012", rows[0].Plant);
        Assert.Equal("1716", rows[0].StorageLocation);
        Assert.Equal("30005R", rows[0].Material);
        Assert.Equal(42.5m, rows[0].AvailableQty);
    }

    [Fact]
    public void ParseImStockRows_unparsable_quantity_defaults_to_zero_rather_than_throwing()
    {
        var response = StockResponse("3012|1716|30005R|not-a-number");
        var rows = WarehouseHelpers.ParseImStockRows(response);
        Assert.Equal(0m, rows[0].AvailableQty);
    }

    [Fact]
    public void ParseImStockRows_skips_the_SAP_header_row()
    {
        var response = StockResponse(); // no data rows, header only
        Assert.Empty(WarehouseHelpers.ParseImStockRows(response));
    }

    [Fact]
    public void BuildStockRequest_adds_an_LGTYP_NE_condition_for_ExcludeStorageType()
    {
        // Weekly PTFE Cycle Count excludes bin type 'SA' (sample/quality
        // stock) from the comparison — StockQuery.ExcludeStorageType.
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { ExcludeStorageType = "SA" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~LGTYP NE 'SA'", whereText);
    }

    [Fact]
    public void BuildStockRequest_StorageType_and_ExcludeStorageType_are_independent()
    {
        var request = WarehouseHelpers.BuildStockRequest(new StockQuery { StorageType = "PTF", ExcludeStorageType = "SA" });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~LGTYP EQ 'PTF'", whereText);
        Assert.Contains("LQUA~LGTYP NE 'SA'", whereText);
    }

    // ── Bin → storage type lookup ────────────────────────────────────────────

    [Fact]
    public void BuildBinStorageTypeLookupRequest_filters_LAGP_by_bin_only_not_storage_type()
    {
        var request = WarehouseHelpers.BuildBinStorageTypeLookupRequest("0000000123");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LAGP~LGNUM EQ '312'", whereText);
        Assert.Contains("LAGP~LGPLA EQ '0000000123'", whereText);
        Assert.DoesNotContain("LGTYP EQ", whereText); // unlike BuildBinCheckRequest, no storage-type filter

        var fields = request.InputTablesItems["query_FIELDS"];
        Assert.Contains(fields, f => f["TABNAME"]!.Equals("LAGP") && f["FIELDNAME"]!.Equals("LGTYP"));
    }

    [Fact]
    public void ParseBinStorageTypeRows_skips_header_dedupes_and_sorts()
    {
        var response = StockResponse("SA", "SB", "SA"); // duplicate SA on purpose
        var types = WarehouseHelpers.ParseBinStorageTypeRows(response);

        Assert.Equal(["SA", "SB"], types);
    }

    [Fact]
    public void ParseBinStorageTypeRows_is_empty_when_only_the_SAP_header_row_comes_back()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "LGTYP" } } },
        };
        Assert.Empty(WarehouseHelpers.ParseBinStorageTypeRows(response));
    }

    // ── Extended open-TR search ──────────────────────────────────────────────

    [Fact]
    public void BuildOpenTransferRequirementsRequest_adds_no_optional_filters_when_none_supplied()
    {
        var request = WarehouseHelpers.BuildOpenTransferRequirementsRequest(new OpenTransferRequirementsQuery());
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.DoesNotContain("DISPO", whereText);
        Assert.DoesNotContain("MATNR", whereText);
        Assert.DoesNotContain("LGORT", whereText);
        Assert.DoesNotContain("BNAME", whereText);
    }

    [Fact]
    public void BuildOpenTransferRequirementsRequest_adds_material_and_createdby_filters_when_supplied()
    {
        // StorageLocation is deliberately NOT filtered in the WHERE clause
        // (dropped from BuildOpenTransferRequirementsRequest) — the query
        // model still carries it, but it's not applied to the SAP-side
        // filter here.
        var request = WarehouseHelpers.BuildOpenTransferRequirementsRequest(new OpenTransferRequirementsQuery
        {
            MrpController = "101", Material = "30005R", StorageLocation = "1710", CreatedBy = "jsmith",
        });
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("MARC~DISPO EQ '101'", whereText);
        Assert.Contains("LTBP~MATNR", whereText);
        Assert.DoesNotContain("LGORT", whereText);
        Assert.Contains("LTBK~BNAME EQ 'JSMITH'", whereText); // SAP usernames are uppercase
    }

    [Fact]
    public void BuildOpenTransferRequirementsRequest_registers_CHARG_appended_after_the_existing_columns()
    {
        var request = WarehouseHelpers.BuildOpenTransferRequirementsRequest(new OpenTransferRequirementsQuery());
        var fields = request.InputTablesItems["query_FIELDS"];

        Assert.Contains(fields, f => f["TABNAME"]!.Equals("LTBP") && f["FIELDNAME"]!.Equals("CHARG"));
        Assert.Equal("CHARG", fields[fields.Count - 1]["FIELDNAME"]); // last-registered, matching OpenTrColumns' appended position (net48 lacks the ^ index operator)
    }

    [Fact]
    public void ParseOpenTransferRequirementRows_maps_batch_from_the_appended_CHARG_column()
    {
        var response = StockResponse(
            "101|4500001234|30005R|1710|12.5|EA|Doc text|5000001111|JSMITH|01.01.26|08:00:00|SA|BIN-01|999|0000123456");

        var rows = WarehouseHelpers.ParseOpenTransferRequirementRows(response);

        Assert.Single(rows);
        Assert.Equal("4500001234", rows[0].TrNumber);
        Assert.Equal("0000123456", rows[0].Batch);
    }

    [Fact]
    public void ParseOpenTransferRequirementRows_parses_a_European_comma_decimal_quantity_correctly()
    {
        // Confirmed for real against a live SAP system: this parser used a
        // bare decimal.TryParse(cols[4], out var qty), which uses the current
        // culture's NumberStyles.Number (AllowThousands) - under en-US/en-GB
        // that treats ',' as a thousands separator, so a genuine SAP value of
        // "300,000" (European convention: comma is the decimal point, meaning
        // 300.000 = 300) parsed as three hundred THOUSAND instead of three
        // hundred - exactly the reported symptom ("300,000 EA should be
        // parsed to 300 EA"). Fixed by routing through the shared,
        // culture-independent RfcRowExtensions.ParseSapDecimal.
        var response = StockResponse(
            "101|4500001234|30005R|1710|300,000|EA|Doc text|5000001111|JSMITH|01.01.26|08:00:00|SA|BIN-01|999|0000123456");

        var rows = WarehouseHelpers.ParseOpenTransferRequirementRows(response);

        Assert.Single(rows);
        Assert.Equal(300m, rows[0].Quantity);
    }

    // ── Delete TR (LB02) ─────────────────────────────────────────────────────

    [Fact]
    public void BuildDeleteTrRequest_matches_the_recorded_LB02_screen_sequence()
    {
        var request = WarehouseHelpers.BuildDeleteTrRequest("4500001234");

        Assert.Equal("LB02", request.ImportParameters["TRANCODE"]);
        Assert.Equal("S", request.ImportParameters["UPDMODE"]);

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Equal(9, rows.Count); // 3 screens + 6 fields

        Assert.Equal("SAPML02B", rows[0]["PROGRAM"]); Assert.Equal("0100", rows[0]["DYNPRO"]);
        Assert.Equal("/00", rows[1]["FVAL"]);
        Assert.Equal("LTBK-LGNUM", rows[2]["FNAM"]); Assert.Equal("312", rows[2]["FVAL"]);
        Assert.Equal("LTBK-TBNUM", rows[3]["FNAM"]); Assert.Equal("4500001234", rows[3]["FVAL"]);
        Assert.Equal("LTBP-TBPOS", rows[4]["FNAM"]); Assert.Equal("", rows[4]["FVAL"]);

        Assert.Equal("SAPML02B", rows[5]["PROGRAM"]); Assert.Equal("1103", rows[5]["DYNPRO"]);
        Assert.Equal("BDC_OKCODE", rows[6]["FNAM"]); Assert.Equal("=DLK", rows[6]["FVAL"]);

        Assert.Equal("SAPLSPO1", rows[7]["PROGRAM"]); Assert.Equal("0400", rows[7]["DYNPRO"]);
        Assert.Equal("BDC_OKCODE", rows[8]["FNAM"]); Assert.Equal("=YES", rows[8]["FVAL"]);
    }

    [Fact]
    public void IsDeleteTrItemBlocked_is_true_only_for_the_exact_E_L2_019_triple()
    {
        Assert.True(WarehouseHelpers.IsDeleteTrItemBlocked(
            new BdcResponse { Type = "E", MessageClass = "L2", MessageNumber = "019" }));

        Assert.False(WarehouseHelpers.IsDeleteTrItemBlocked(
            new BdcResponse { Type = "S", MessageClass = "L2", MessageNumber = "019" }));
        Assert.False(WarehouseHelpers.IsDeleteTrItemBlocked(
            new BdcResponse { Type = "E", MessageClass = "L1", MessageNumber = "019" }));
        Assert.False(WarehouseHelpers.IsDeleteTrItemBlocked(
            new BdcResponse { Type = "E", MessageClass = "L2", MessageNumber = "020" }));
    }

    [Fact]
    public void BuildTrExistsRequest_filters_LTBP_by_TR_number()
    {
        var request = WarehouseHelpers.BuildTrExistsRequest("4500001234");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LTBP~LGNUM EQ '312'", whereText);
        Assert.Contains("LTBP~TBNUM EQ '4500001234'", whereText);
    }

    [Fact]
    public void TrStillExists_is_false_when_only_the_SAP_header_row_comes_back()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "TBNUM" } } }, // header only, no real hit
        };
        Assert.False(WarehouseHelpers.TrStillExists(response));
    }

    [Fact]
    public void TrStillExists_is_true_when_a_real_row_follows_the_header()
    {
        var response = StockResponse("4500001234"); // header + one real row
        Assert.True(WarehouseHelpers.TrStillExists(response));
    }

    // ── TR cleanup candidates ────────────────────────────────────────────────

    [Fact]
    public void ParseTrCleanupBaseRows_maps_batch_and_unrestricted_qty_including_blank_as_null()
    {
        var response = StockResponse(
            "101|4500001111|30005R|1710|10|EA|0000111111|5",
            "101|4500002222|30006R|0001|20|EA|0000222222|");

        var rows = WarehouseHelpers.ParseTrCleanupBaseRows(response);

        Assert.Equal(2, rows.Length);
        Assert.Equal("0000111111", rows[0].Batch);
        Assert.Equal(5m, rows[0].UnrestrictedQty);
        Assert.Null(rows[1].UnrestrictedQty); // blank CLABS parses to null, not 0
    }

    [Fact]
    public void BuildTrCleanupLquaByBatchRequest_uses_IN_opt_and_value_list_not_a_literal_IN_clause()
    {
        var request = WarehouseHelpers.BuildTrCleanupLquaByBatchRequest(["123", "123", "456"]); // dupe on purpose
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));

        Assert.Contains("LQUA~CHARG IN opt", whereText);
        Assert.DoesNotContain("IN (", whereText); // ZRFC_READ_TABLES doesn't support a literal SQL IN(...)

        var values = request.InputTablesItems["value_list"];
        Assert.Equal(2, values.Count); // deduped
        Assert.Equal("I", values[0]["SIGN"]);
        Assert.Equal("EQ", values[0]["OPTION"]);
        Assert.Equal("0000000123", values[0]["LOW"]); // padded to 10, numeric
    }

    [Fact]
    public void ParseAlreadyTransferredBatches_flags_only_non_901_bins_with_positive_quantity()
    {
        var response = StockResponse(
            "0000111111|902|BIN-A|5",   // non-901, qty>0 -> flagged
            "0000222222|901|BIN-B|5",   // still in 901 -> not flagged
            "0000333333|902|BIN-C|0");  // non-901 but zero qty -> not flagged

        var transferred = WarehouseHelpers.ParseAlreadyTransferredBatches(response);

        Assert.Contains("111111", transferred); // TrimStart('0') convention
        Assert.DoesNotContain("222222", transferred);
        Assert.DoesNotContain("333333", transferred);
    }

    [Fact]
    public void BuildTrCleanupCandidateRows_flags_all_three_reasons_independently_and_combines_without_duplication()
    {
        var baseRows = new[]
        {
            new WarehouseHelpers.TrCleanupBaseRow { TrNumber = "TR1", StorageLocation = "1710", Batch = "1", UnrestrictedQty = 5m },   // sloc only
            new WarehouseHelpers.TrCleanupBaseRow { TrNumber = "TR2", StorageLocation = "0001", Batch = "2", UnrestrictedQty = 0m },   // no-stock only
            new WarehouseHelpers.TrCleanupBaseRow { TrNumber = "TR3", StorageLocation = "1710", Batch = "3", UnrestrictedQty = 0m },   // sloc + no-stock (multi-reason)
            new WarehouseHelpers.TrCleanupBaseRow { TrNumber = "TR4", StorageLocation = "0001", Batch = "4", UnrestrictedQty = 5m },   // no reason -> excluded
        };
        var lqua = StockResponse("0000000001|902|BIN-X|5"); // batch "1" (TR1) currently sits in a non-901 bin -> TR1 becomes multi-reason too; TR4's batch "4" has no LQUA row, so it stays excluded
        var candidates = WarehouseHelpers.BuildTrCleanupCandidateRows(baseRows, lqua);

        Assert.Equal(3, candidates.Length); // TR4 excluded — matches no reason
        Assert.DoesNotContain(candidates, c => c.TrNumber == "TR4");

        var tr1 = candidates.Single(c => c.TrNumber == "TR1");
        Assert.Contains(WarehouseHelpers.ReasonSloc1710, tr1.Reasons);
        Assert.Contains(WarehouseHelpers.ReasonAlreadyTransferred, tr1.Reasons);
        Assert.DoesNotContain(WarehouseHelpers.ReasonNoStock, tr1.Reasons);

        var tr3 = candidates.Single(c => c.TrNumber == "TR3");
        Assert.Contains(WarehouseHelpers.ReasonSloc1710, tr3.Reasons);
        Assert.Contains(WarehouseHelpers.ReasonNoStock, tr3.Reasons);
        Assert.Equal(2, tr3.Reasons.Length); // no duplication
    }

    [Fact]
    public void BuildTrCleanupCandidateRows_handles_a_null_lqua_response_as_no_batches_transferred()
    {
        var baseRows = new[]
        {
            new WarehouseHelpers.TrCleanupBaseRow { TrNumber = "TR1", StorageLocation = "1710", Batch = "1", UnrestrictedQty = 5m },
        };

        var candidates = WarehouseHelpers.BuildTrCleanupCandidateRows(baseRows, null);

        Assert.Single(candidates);
        Assert.DoesNotContain(WarehouseHelpers.ReasonAlreadyTransferred, candidates[0].Reasons);
    }

    [Fact]
    public void BuildZdelRequest_formats_weights_as_comma_decimal_not_a_plain_ToString()
    {
        // Regression test for a real, confirmed-live SAP rejection: this
        // used to call body.GrossWeight.ToString() directly (culture-
        // dependent, not even routed through BdcBuilder's own
        // InvariantCulture decimal formatting) and SAP's real screen mask
        // rejected it outright ("Input must be in the format
        // ___.___.___.__~,___") -- this system's screens want comma-decimal,
        // matching every other real quantity value seen from it this
        // session (e.g. "300,000", "1.297,000").
        var request = WarehouseHelpers.BuildZdelRequest(new SetDeliveryWeightRequest
        {
            DeliveryNumber = "0082291409", GrossWeight = 12m, NetWeight = 11.5m, PalletCount = 3,
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Equal("12,000", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIKP-BTGEW")["FVAL"]);
        Assert.Equal("11,500", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIKP-NTGEW")["FVAL"]);
        Assert.Equal("3", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIKP-ANZPK")["FVAL"]);
    }

    [Fact]
    public void BuildZdelRequest_pads_VBELN_on_screen_1_but_leaves_it_unpadded_on_screen_2()
    {
        // Regression test for a real, confirmed-live SAP rejection (CH 004,
        // "table does not contain an entry"): a real SHDB recording showed
        // screen 1's LIKP-VBELN as zero-padded ("0082291409") but screen 2's
        // as NOT padded ("82291409") -- SAP's own UI re-displays the
        // already-selected document in its natural form on the second
        // screen, and sending the padded form there (as this code
        // previously did on both screens identically) broke the custom
        // ZDEL program's own internal lookup.
        var request = WarehouseHelpers.BuildZdelRequest(new SetDeliveryWeightRequest
        {
            DeliveryNumber = "0082291409", GrossWeight = 12m, NetWeight = 12m, PalletCount = 3,
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        var vbelnRows = rows.Where(r => r.GetValueOrDefault("FNAM") as string == "LIKP-VBELN").ToList();
        Assert.Equal(2, vbelnRows.Count);
        Assert.Equal("0082291409", vbelnRows[0]["FVAL"]); // screen 1 (=SELE) -- padded
        Assert.Equal("82291409", vbelnRows[1]["FVAL"]);   // screen 2 (=SAVE) -- NOT padded
    }

    [Fact]
    public void BuildSetPickedQuantityRequest_sends_VBELN_unpadded_matching_the_real_recording()
    {
        // Confirmed via a real SHDB recording of delivery 0082291409:
        // VL02N's own initial screen wants VBELN unpadded, unlike the padded
        // convention BAPI/other BDC calls in this codebase use.
        var request = WarehouseHelpers.BuildSetPickedQuantityRequest(new SetPickedQuantityRequest
        {
            DeliveryNumber = "0082291409", PickedQuantities = [400m],
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        var vbelnRows = rows.Where(r => r.GetValueOrDefault("FNAM") as string == "LIKP-VBELN").ToList();
        Assert.All(vbelnRows, r => Assert.Equal("82291409", r["FVAL"]));
    }

    [Fact]
    public void BuildSetPickedQuantityRequest_uses_the_real_okcodes_CHPL_T01_then_SICH_T()
    {
        var request = WarehouseHelpers.BuildSetPickedQuantityRequest(new SetPickedQuantityRequest
        {
            DeliveryNumber = "0082291409", PickedQuantities = [400m, 400m, 400m],
        });

        var okcodes = request.InputTablesItems["BDCTABLE"]
            .Where(r => r.GetValueOrDefault("FNAM") as string == "BDC_OKCODE")
            .Select(r => r["FVAL"] as string).ToList();
        Assert.Equal(new[] { "/00", "=CHPL_T01", "=SICH_T" }, okcodes);
    }

    [Fact]
    public void BuildSetPickedQuantityRequest_sets_one_LIPSD_PIKMG_row_per_quantity_starting_at_index_02()
    {
        // Real, confirmed-live table-control mechanics: row 1 is the parent
        // item's own (already-zero) aggregate row; the batch-split rows
        // start at index 2 -- fragile-by-construction, flagged directly by
        // the user, since this addresses a screen ROW POSITION, not a
        // batch/item number.
        var request = WarehouseHelpers.BuildSetPickedQuantityRequest(new SetPickedQuantityRequest
        {
            DeliveryNumber = "0082291409", PickedQuantities = [400m, 400m, 400m],
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.Equal("400,000", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIPSD-PIKMG(02)")["FVAL"]);
        Assert.Equal("400,000", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIPSD-PIKMG(03)")["FVAL"]);
        Assert.Equal("400,000", rows.Single(r => r.GetValueOrDefault("FNAM") as string == "LIPSD-PIKMG(04)")["FVAL"]);
    }

    [Fact]
    public void BuildSetPickedQuantityRequest_omits_LIKP_BLDAT_when_not_supplied_and_never_sends_KODAT_or_KOUHR()
    {
        // KODAT/KOUHR are never sent at all -- confirmed live that both are
        // rejected as "Field ... does not exist in dynpro SAPMV50A 1000"
        // despite SHDB recording them alongside BLDAT.
        var request = WarehouseHelpers.BuildSetPickedQuantityRequest(new SetPickedQuantityRequest
        {
            DeliveryNumber = "0082291409", PickedQuantities = [400m],
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        Assert.DoesNotContain(rows, r => r.GetValueOrDefault("FNAM") as string == "LIKP-BLDAT");
        Assert.DoesNotContain(rows, r => r.GetValueOrDefault("FNAM") as string == "LIKP-KODAT");
        Assert.DoesNotContain(rows, r => r.GetValueOrDefault("FNAM") as string == "LIKP-KOUHR");
    }

    [Fact]
    public void BuildSetPickedQuantityRequest_sends_LIKP_BLDAT_once_per_screen_when_supplied()
    {
        var request = WarehouseHelpers.BuildSetPickedQuantityRequest(new SetPickedQuantityRequest
        {
            DeliveryNumber = "0082291409", PickedQuantities = [400m], BillingDate = "29.04.2022",
        });

        var rows = request.InputTablesItems["BDCTABLE"];
        var bldatRows = rows.Where(r => r.GetValueOrDefault("FNAM") as string == "LIKP-BLDAT").ToList();
        Assert.Equal(2, bldatRows.Count); // sent once per screen-1000 hit, matching the real recording
        Assert.All(bldatRows, r => Assert.Equal("29.04.2022", r["FVAL"]));
    }
}
