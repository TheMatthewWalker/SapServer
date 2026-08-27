using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class PackagingHelpersTests
{
    [Fact]
    public void TryGetDrumComponent_resolves_a_known_packaging_code_case_insensitively()
    {
        Assert.True(PackagingHelpers.TryGetDrumComponent("sd", out var component));
        Assert.Equal("P_DRUMSML_NMT", component);
    }

    [Fact]
    public void TryGetDrumComponent_fails_for_an_unknown_code()
    {
        Assert.False(PackagingHelpers.TryGetDrumComponent("ZZ", out _));
    }

    [Fact]
    public void ReferenceMaterial_and_PackagingMaterial_follow_the_IB_naming_convention()
    {
        Assert.Equal("IB_363800_SD", PackagingHelpers.ReferenceMaterial("SD"));
        Assert.Equal("IB_363660_SD", PackagingHelpers.PackagingMaterial("363660", "SD"));
    }

    [Fact]
    public void ParseMaterialExists_is_true_only_when_a_row_comes_back()
    {
        var exists = new RfcResponse { Tables = new() { ["data_display"] = new() { new() { ["WA"] = "header" }, new() { ["WA"] = "3012" } } } };
        var missing = new RfcResponse { Tables = new() };
        Assert.True(PackagingHelpers.ParseMaterialExists(exists));
        Assert.False(PackagingHelpers.ParseMaterialExists(missing));
    }

    [Fact]
    public void ParseMara_parses_the_native_comma_decimal_weight_with_no_extra_scaling()
    {
        // Regression test for the same bug class as ParseZpackInstr's fix
        // (see its comment): this used to divide the parsed weight by 1000
        // on a mistaken "grams to kg" assumption. Confirmed live via a raw
        // ZRFC_READ_TABLES bypass read: CP104's real MARA-BRGEW is "0,021"
        // (SAP's native comma-decimal, no thousands grouping) = 0.021 kg, a
        // plausible real component weight -- ParseSapDecimal alone parses
        // this correctly; the extra /1000 made it display as an implausible
        // 0.000021 kg. endpoint-test-log-2026-08-27.md's correction section.
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "0,021|VERP|INSB|KG" },
                }
            }
        };
        var row = PackagingHelpers.ParseMara(response);
        Assert.NotNull(row);
        Assert.Equal(0.021m, row!.WeightKg);
        Assert.Equal("VERP", row.MaterialType);
    }

    [Fact]
    public void ParseMara_returns_null_when_nothing_matches()
    {
        Assert.Null(PackagingHelpers.ParseMara(new RfcResponse()));
    }

    [Fact]
    public void ParsePackagingBom_parses_the_native_comma_decimal_quantity_with_no_extra_scaling()
    {
        // Same bug class as ParseZpackInstr/ParseMara. Confirmed live via a
        // raw ZRFC_READ_TABLES bypass read: IB_CARTON2_NMT's real
        // ZBOM_INFO-MENGE is "1,000" (SAP's native comma-decimal, no
        // thousands grouping) = exactly 1 EA, a completely standard BOM
        // component quantity -- the extra /1000 made it display as an
        // implausible 0.001. endpoint-test-log-2026-08-27.md's correction
        // section.
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "P_CARTON2_NMT|EA|1,000" },
                }
            }
        };
        var rows = PackagingHelpers.ParsePackagingBom(response);
        Assert.Single(rows);
        Assert.Equal("P_CARTON2_NMT", rows[0].Component);
        Assert.Equal(1.0m, rows[0].Quantity);
    }

    [Fact]
    public void BuildZpackInstrRequest_filters_by_blank_KUNNR_for_a_plant_default_lookup()
    {
        var request = PackagingHelpers.BuildZpackInstrRequest("30005R", "");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("ZPACK_INSTR~KUNNR EQ ''", whereText);
    }

    [Fact]
    public void BuildZpackInstrRequest_filters_by_the_padded_customer_when_one_is_given()
    {
        var request = PackagingHelpers.BuildZpackInstrRequest("30005R", "12345");
        var whereText = string.Join(" ", request.InputTablesItems["where_clause"].Select(r => r["TEXT"]));
        Assert.Contains("ZPACK_INSTR~KUNNR EQ '0000012345'", whereText);
    }

    [Fact]
    public void ParseZpackInstr_parses_native_comma_decimal_quantities_with_no_extra_scaling()
    {
        // Regression test for a real, confirmed-live bug: this used to divide
        // PalletQty/SmallBoxQty by 1000 on top of ParseSapDecimal's own
        // parsing, on a mistaken assumption that ZPACK_INSTR's raw dump
        // needed an extra gram->kg-style conversion. It didn't -- SAP's own
        // raw dump for these quantities is plain comma-decimal text (e.g.
        // "300,000" meaning 300, same convention as RfcRowExtensions.
        // ParseSapDecimal/GetDecimal elsewhere), and ParseSapDecimal already
        // parses that correctly on its own. The extra /1000 silently shrank
        // every real value 1000x -- confirmed live against CP104's actual
        // SAP data (endpoint-test-log-2026-08-27.md, ROUND 2 #16, and its
        // same-day correction after this test's own /1000 assumption turned
        // out to be the bug, not BuildPackInstrMaintRequest's write side).
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "header" },
                    new() { ["WA"] = "IB_363660_MB|1000,000|500,000|X| |X| |X| | " },
                }
            }
        };
        var row = PackagingHelpers.ParseZpackInstr(response);

        Assert.NotNull(row);
        Assert.Equal(1000m, row!.PalletQty);
        Assert.Equal(500m, row.SmallBoxQty);
        Assert.True(row.PackProd);
        Assert.False(row.BoxGen);
        Assert.True(row.BatchSpread);
    }

    [Fact]
    public void BuildPackInstrMaintRequest_only_sends_trace_flags_when_saving_at_plant_level()
    {
        var plantLevel = PackagingHelpers.BuildPackInstrMaintRequest(new PackagingInstrSaveRequest
        {
            Material = "30005R", Customer = null, ChargeReq = true, TechStatReq = true, PNumReq = true,
        });
        var plantRow = plantLevel.InputTables["IT_ZPACK_INSTR"][0];
        Assert.Equal("X", plantRow["CHARGE_REQ"]);
        Assert.Equal("X", plantRow["TECHSTAT_REQ"]);
        Assert.Equal("X", plantRow["PNUM_REQ"]);

        var customerLevel = PackagingHelpers.BuildPackInstrMaintRequest(new PackagingInstrSaveRequest
        {
            Material = "30005R", Customer = "12345", ChargeReq = true, TechStatReq = true, PNumReq = true,
        });
        var customerRow = customerLevel.InputTables["IT_ZPACK_INSTR"][0];
        Assert.Equal("", customerRow["CHARGE_REQ"]); // ignored at customer level even though requested
        Assert.Equal("", customerRow["TECHSTAT_REQ"]);
        Assert.Equal("", customerRow["PNUM_REQ"]);
    }

    [Fact]
    public void BuildPackInstrMaintRequest_passes_quantities_through_with_no_scaling()
    {
        // Regression test: an earlier same-day fix multiplied PalletQty/
        // SmallBoxQty by 1000 here to "match" ParseZpackInstr's /1000 --
        // but that /1000 was itself the actual bug (see ParseZpackInstr's
        // comment), and this write side never needed any scaling at all.
        // Confirmed live: writing SmallBoxQty=300 through this method must
        // store the real SAP value 300 (raw dump "300,000", i.e. 300 with
        // no thousands grouping needed) -- not 300000, which is what the
        // brief x1000 "fix" actually wrote before being caught and reverted.
        // endpoint-test-log-2026-08-27.md, ROUND 2 #16 for the full incident.
        var request = PackagingHelpers.BuildPackInstrMaintRequest(new PackagingInstrSaveRequest
        {
            Material = "30005R", PalletQty = 1000m, SmallBoxQty = 300m,
        });
        var row = request.InputTables["IT_ZPACK_INSTR"][0];
        Assert.Equal(1000m, row["PALL_QTY"]);
        Assert.Equal(300m, row["SMBX_QTY"]);
    }

    [Fact]
    public void ParsePackInstrMaintResult_reports_success_when_RC_is_zero()
    {
        var response = new RfcResponse { Parameters = new() { ["RC"] = "0" }, Tables = new() };
        var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(response);
        Assert.True(success);
        Assert.Equal("OK", message);
    }

    [Fact]
    public void ParsePackInstrMaintResult_reports_failure_and_joins_messages_when_RC_is_nonzero()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["RC"] = "4" },
            Tables = new() { ["IT_MESSAGES"] = [new() { ["Text"] = "Material does not exist" }] },
        };
        var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(response);
        Assert.False(success);
        Assert.Equal("Material does not exist", message);
    }

    [Fact]
    public void ParsePackInstrMaintResult_falls_back_to_a_generic_message_when_RC_is_nonzero_with_no_messages()
    {
        var response = new RfcResponse { Parameters = new() { ["RC"] = "4" }, Tables = new() };
        var (success, message) = PackagingHelpers.ParsePackInstrMaintResult(response);
        Assert.False(success);
        Assert.Equal("Update failed (no message returned)", message);
    }
}
