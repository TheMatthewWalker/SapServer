using SapServer.Helpers;
using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Tests.Helpers;

public class ZdelflagHelpersTests
{
    [Fact]
    public void ParseLikpAblad_returns_the_second_column_trimmed()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "VBELN|ABLAD" },
                    new() { ["WA"] = "0080001234| Loading point 1 " },
                }
            }
        };
        Assert.Equal("Loading point 1", ZdelflagHelpers.ParseLikpAblad(response));
    }

    [Fact]
    public void ParseLikpAblad_returns_empty_when_nothing_matches()
    {
        Assert.Equal("", ZdelflagHelpers.ParseLikpAblad(new RfcResponse()));
    }

    [Fact]
    public void ParseLipsItemDetail_maps_ARKTX_KDMAT_VRKME_per_item()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "POSNR|ARKTX|KDMAT|VRKME" },
                    new() { ["WA"] = "000010|Widget|CUSTMAT1|EA" },
                }
            }
        };
        var rows = ZdelflagHelpers.ParseLipsItemDetail(response);
        Assert.Single(rows);
        Assert.Equal("Widget", rows[0].Description);
        Assert.Equal("CUSTMAT1", rows[0].CustomerMaterial);
    }

    [Fact]
    public void ParseKnvvEikto_returns_the_first_column_trimmed()
    {
        var response = new RfcResponse
        {
            Tables = new() { ["data_display"] = new() { new() { ["WA"] = "EIKTO" }, new() { ["WA"] = " CUST-EIKTO1 " } } },
        };
        Assert.Equal("CUST-EIKTO1", ZdelflagHelpers.ParseKnvvEikto(response));
    }

    [Fact]
    public void BuildZbomInfoRequest_deduplicates_packaging_instructions_without_padding_them()
    {
        var request = ZdelflagHelpers.BuildZbomInfoRequest(["IB_363660_MB", "IB_363660_MB", "12345678"]);
        var values = request.InputTablesItems["value_list"];
        Assert.Equal(2, values.Count); // deduped
        Assert.Equal("12345678", values[1]["LOW"]); // NOT zero-padded — it's a lookup key, not a material number
    }

    [Fact]
    public void ParseZbomInfoRows_trims_both_columns()
    {
        var response = new RfcResponse
        {
            Tables = new()
            {
                ["data_display"] = new()
                {
                    new() { ["WA"] = "MATNR|IDNRK" },
                    new() { ["WA"] = " IB_363660_MB | 30006R  " },
                }
            }
        };
        var rows = ZdelflagHelpers.ParseZbomInfoRows(response);
        Assert.Equal("IB_363660_MB", rows[0].PackagingInstruction);
        Assert.Equal("30006R", rows[0].ComponentMaterial);
    }

    private static DelflagRowRequest HeaderRow(string smbxMatnr = "", string pallMatnr = "IB_363660_MB", string matnr = "") => new()
    {
        Vbeln = "80001234", Kunnr = "12345", Werks = "3012",
        SmbxMatnr = smbxMatnr, PallMatnr = pallMatnr, Matnr = matnr,
        Vhart = "PALL", Pallet = "G", PrintPalletLabel = true, PrintBoxLabel = false,
    };

    [Fact]
    public void BuildMaintainRequest_pads_VBELN_KUNNR_and_PALL_MATNR_but_never_pads_SMBX_MATNR()
    {
        var request = ZdelflagHelpers.BuildMaintainRequest(new MaintainZdelflagRequest
        {
            DelflagRows = [HeaderRow(smbxMatnr: "12345678", pallMatnr: "12345678")], // both purely-numeric-looking
        });

        var row = request.InputTables["T_DELFLAG"][0];
        Assert.Equal("0080001234", row["VBELN"]); // padded to 10
        Assert.Equal("0000012345", row["KUNNR"]);
        Assert.Equal("000000000012345678", row["PALL_MATNR"]); // padded to 18 — a real material number
        Assert.Equal("12345678", row["SMBX_MATNR"]);           // NOT padded — deliberately a lookup key, not a material
    }

    [Fact]
    public void BuildMaintainRequest_only_pads_MATNR_when_it_is_actually_present()
    {
        var withMaterial = ZdelflagHelpers.BuildMaintainRequest(new MaintainZdelflagRequest { DelflagRows = [HeaderRow(matnr: "12345678")] });
        Assert.Equal("000000000012345678", withMaterial.InputTables["T_DELFLAG"][0]["MATNR"]);

        var withoutMaterial = ZdelflagHelpers.BuildMaintainRequest(new MaintainZdelflagRequest { DelflagRows = [HeaderRow()] });
        Assert.Equal("", withoutMaterial.InputTables["T_DELFLAG"][0]["MATNR"]);
    }

    [Fact]
    public void BuildMaintainRequest_converts_print_flags_to_SAPs_X_or_blank()
    {
        var request = ZdelflagHelpers.BuildMaintainRequest(new MaintainZdelflagRequest { DelflagRows = [HeaderRow()] });
        var row = request.InputTables["T_DELFLAG"][0];
        Assert.Equal("X", row["P_PALL"]);
        Assert.Equal("", row["P_SMBX"]);
    }

    [Fact]
    public void BuildMaintainRequest_pads_DELPACK_PALL_MATNR_to_18()
    {
        var request = ZdelflagHelpers.BuildMaintainRequest(new MaintainZdelflagRequest
        {
            DelpackRows = [new DelpackRowRequest { Packid = "1001", PallMatnr = "12345678", Tarewei = 0.5m }],
        });
        Assert.Equal("000000000012345678", request.InputTables["T_DELPACK"][0]["PALL_MATNR"]);
    }

    // ET_MESSAGE is ZERRORTEXT (LINE/TEXT — no TYPE/MESSAGE, no per-line
    // severity) confirmed against a live SE37 signature dump; RC only ever
    // surfaces through this COM layer as a flat scalar ("0" = success) even
    // though it's typed SYST in ABAP — see ParseMaintainResponse's header
    // comment for the full story.
    [Fact]
    public void ParseMaintainResponse_reads_RC_and_ET_MESSAGE_TEXT_as_type_S_on_success()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["RC"] = "0" },
            Tables = new() { ["ET_MESSAGE"] = [new() { ["LINE"] = "000000", ["TEXT"] = "SUCCESS: ALL PALLETs was finished!" }] },
        };
        var result = ZdelflagHelpers.ParseMaintainResponse(response);
        Assert.Equal("0", result.Rc);
        Assert.Single(result.Messages);
        Assert.Equal("S", result.Messages[0].Type);
        Assert.Equal("SUCCESS: ALL PALLETs was finished!", result.Messages[0].Message);
    }

    [Fact]
    public void ParseMaintainResponse_treats_nonzero_RC_as_type_E()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["RC"] = "4" },
            Tables = new() { ["ET_MESSAGE"] = [new() { ["LINE"] = "000000", ["TEXT"] = "Something went wrong" }] },
        };
        var result = ZdelflagHelpers.ParseMaintainResponse(response);
        Assert.Single(result.Messages);
        Assert.Equal("E", result.Messages[0].Type);
        Assert.Equal("Something went wrong", result.Messages[0].Message);
    }

    [Fact]
    public void ParseMaintainResponse_gives_a_fallback_message_when_RC_fails_with_no_ET_MESSAGE_text()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["RC"] = "4" },
            Tables = new() { ["ET_MESSAGE"] = [] },
        };
        var result = ZdelflagHelpers.ParseMaintainResponse(response);
        Assert.Single(result.Messages);
        Assert.Equal("E", result.Messages[0].Type);
        Assert.Contains("RC=4", result.Messages[0].Message);
    }

    [Fact]
    public void ParseMaintainResponse_returns_no_messages_when_RC_is_success_and_ET_MESSAGE_is_empty()
    {
        var response = new RfcResponse
        {
            Parameters = new() { ["RC"] = "0" },
            Tables = new() { ["ET_MESSAGE"] = [] },
        };
        var result = ZdelflagHelpers.ParseMaintainResponse(response);
        Assert.Empty(result.Messages);
    }
}
