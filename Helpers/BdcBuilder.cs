using SapServer.Models;

namespace SapServer.Helpers;

/// <summary>
/// Fluent builder for BDC calls via Z_RFC_CALL_TRANSACTION.
/// Mirrors the VB <c>screene</c> / <c>field</c> helper pattern.
///
/// Usage:
/// <code>
///   var request = BdcBuilder.For("MB1B")
///       .Screen("SAPMM07M", "0400")
///           .Field("BDC_OKCODE",    "/00")
///           .Field("RM07M-BWARTWA", "411")
///           .Field("RM07M-WERKS",   "3012")
///       .Screen("SAPMM07M", "0421")
///           .Field("BDC_OKCODE",    "=BU")
///       .Build();
/// </code>
/// </summary>
public sealed class BdcBuilder
{
    private const string BdcFunction  = "Z_RFC_CALL_TRANSACTION";
    private const string BdcTableName = "BDCTABLE";

    private readonly string _transactionCode;
    private readonly string _updateMode;
    private readonly List<Dictionary<string, object?>> _rows = [];

    private BdcBuilder(string transactionCode, string updateMode)
    {
        _transactionCode = transactionCode;
        _updateMode      = updateMode;
    }

    /// <summary>Creates a new BDC builder for the given SAP transaction code.</summary>
    /// <param name="transactionCode">SAP transaction e.g. "MB1B", "LT01".</param>
    /// <param name="updateMode">"S" = synchronous (default), "A" = asynchronous batch input.</param>
    public static BdcBuilder For(string transactionCode, string updateMode = "S")
        => new(transactionCode, updateMode);

    /// <summary>
    /// Appends a dynpro (screen) begin row — equivalent to VB <c>screene(prog, screen)</c>.
    /// </summary>
    public BdcBuilder Screen(string program, string dynpro)
    {
        _rows.Add(new Dictionary<string, object?>
        {
            ["PROGRAM"]  = program,
            ["DYNPRO"]   = dynpro,
            ["DYNBEGIN"] = "X"
        });
        return this;
    }

    /// <summary>
    /// Appends a field value row — equivalent to VB <c>field(name, value)</c>.
    /// </summary>
    public BdcBuilder Field(string name, string value)
    {
        _rows.Add(new Dictionary<string, object?>
        {
            ["FNAM"] = name,
            ["FVAL"] = value
        });
        return this;
    }

    /// <summary>
    /// Builds the <see cref="RfcRequest"/> ready to pass to <c>ISapConnectionPool.ExecuteAsync</c>.
    /// The response will contain a "MESSG" parameter with the SAP result message.
    /// </summary>
    public RfcRequest Build()
    {
        var builder = new RfcRequestBuilder(BdcFunction)
            .Import("TRANCODE", _transactionCode)
            .Import("UPDMODE",  _updateMode);

        foreach (var row in _rows)
            builder.TableItemRow(BdcTableName, row);

        builder.ReadStructParam("MESSG", 5);

        return builder.Build();
    }
}
