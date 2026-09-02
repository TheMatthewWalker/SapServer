using System.Globalization;
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
    /// FVAL (BDCDATA's field-value column) is always CHAR132 - the same
    /// fixed-length text field a real user's keystrokes would populate on
    /// screen, so it must be a real string, never a raw numeric type passed
    /// through as object. The old COM/VARIANT transport silently stringified
    /// this; confirmed for real against a live IIS deploy that SAP NCo's
    /// typed RfcDataContainer.SetValue throws RfcTypeConversionException
    /// ("cannot convert Double into CHAR132") instead.
    ///
    /// Confirmed against this SAP system (transaction SU3 "Decimal Notation"
    /// user parameter) that BDC screen input expects European decimal notation:
    /// comma as the decimal separator, period as the thousands grouping
    /// separator. This matches every real quantity value observed from this
    /// system (e.g. "300,000" meaning 300, "1.297,000" meaning 1297), and was
    /// confirmed live when ZDEL's BTGEW/NTGEW fields rejected a period-decimal
    /// with "Input must be in the format ___.___.___.__~,___".
    /// </summary>
    // European format: ',' decimal separator, '.' thousands grouping.
    private static readonly NumberFormatInfo SapScreenFormat = new NumberFormatInfo
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
    };

    public BdcBuilder Field(string name, decimal value) =>
        Field(name, value.ToString("0.######", SapScreenFormat));

    public BdcBuilder Field(string name, int value) =>
        Field(name, value.ToString(CultureInfo.InvariantCulture));


    /// <summary>
    /// Appends a field value row — on condition being true. 
    /// Useful for optional fields to avoid unnecessary blank entries.
    /// </summary>
    public BdcBuilder FieldIf(bool condition, string name, string value)
    {
        if (condition)
            Field(name, value);

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

    public BdcBuilder Debug()
    {
        Console.WriteLine("=== BDC TABLE (GRID VIEW) ===");

        // Collect all possible column names across all rows
        var allColumns = _rows
            .SelectMany(r => r.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Determine column widths
        var colWidths = allColumns.ToDictionary(
            col => col,
            col => Math.Max(col.Length, _rows.Max(r => r.ContainsKey(col) && r[col] != null
                ? r[col]!.ToString()!.Length
                : 0))
        );

        // Print header
        foreach (var col in allColumns)
            Console.Write($"{col.PadRight(colWidths[col] + 2)}");
        Console.WriteLine();

        // Print separator
        foreach (var col in allColumns)
            Console.Write(new string('-', colWidths[col]) + "  ");
        Console.WriteLine();

        // Print each row
        foreach (var row in _rows)
        {
            foreach (var col in allColumns)
            {
                var value = row.ContainsKey(col) && row[col] != null
                    ? row[col]!.ToString()
                    : "";

                Console.Write(value.PadRight(colWidths[col] + 2));
            }
            Console.WriteLine();
        }

        Console.WriteLine("=== END BDC TABLE ===");

        return this;
    }



}



