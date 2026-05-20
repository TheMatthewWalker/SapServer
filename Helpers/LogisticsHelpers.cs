using SapServer.Models;
using SapServer.Models.Bapi;

namespace SapServer.Helpers;

internal static class LogisticsHelpers
{
    internal const string FnReadTables  = "ZRFC_READ_TABLES";
    internal const string FnCreateTo    = "L_TO_CREATE_SINGLE";
    internal const string FnConsignment = "Z_RFC_CALL_TRANSACTION";
    internal const string Warehouse     = "312";
    internal const string Plant         = "3012";

    // Column order must exactly match query_FIELDS registration order below
    internal static readonly string[] PicksheetColumns =
        [
        "VBELN", // deliveryNumber
        "KUNNR", // customerNumber
        "KODAT", // dispatchDate
        "LFDAT", // deliveryDate
        "INCO1" // incoterms
        ];
        

    // ── Picksheets ─────────────────────────────────────────────────────────────────

    internal static RfcRequest BuildPicksheetRequest(PicksheetRow[]? openPicksheets = null)
    {
        var table = "LIKP";
        int records = 0;
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  "")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = table });

        foreach (var field in PicksheetColumns)
            builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = field });

        builder.WhereCondition($"{table}~VKORG EQ '{Plant}'");
        builder.WhereCondition($"{table}~VBELN IN opt"); // open pick sheets only

        if (openPicksheets != null)
            foreach (var row in openPicksheets)
            {
            records ++;
            //Console.WriteLine($"Adding picksheet {row.DeliveryNumber} to request."); // log each delivery number for debugging
            builder.TableItemRow("value_list", new
            {
                TABNAME   = table,
                FIELDNAME = "VBELN",
                SIGN      = "I",
                OPTION    = "",
                LOW       = SapPad.Pad(row.DeliveryNumber, 10),
                HIGH      = ""
            });
            }

        builder.ReadTable("data_display"); // no fields → WA column only
        Console.WriteLine($"Number of picksheets from VBUK: {records}"); // log number of records for debugging

        return builder.Build();
    }

    internal static PicksheetRow[] ParsePicksheetRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= PicksheetColumns.Length)
            .Select(cols => new PicksheetRow
            {
                DeliveryNumber = cols[0],
                CustomerNumber = cols[1],
                DispatchDate = cols[2],
                DeliveryDate = cols[3], 
                Incoterms = cols[4],
            })
            .ToArray();
    }


    internal static RfcRequest BuildVBUKRequest()
    {
        var table = "VBUK";
        var builder = new RfcRequestBuilder(FnReadTables)
            .Import("DELIMITER", "|")
            .Import("ROWCOUNT",  "")
            .Import("NO_DATA",   " ")
            .TableRow("QUERY_TABLES", new { TABNAME = table });

        builder.TableItemRow("query_FIELDS", new { TABNAME = table, FIELDNAME = "VBELN" });

        builder.WhereCondition($"{table}~WBSTK EQ 'A'"); // open pick sheets only
        builder.WhereCondition($"{table}~VBTYP EQ 'J'"); // delivery documents only

        builder.ReadTable("data_display"); // no fields → WA column only

        return builder.Build();
    }


    internal static PicksheetRow[] ParseVBUKRows(RfcResponse response)
    {
        if (!response.Tables.TryGetValue("data_display", out var sapRows))
            return [];

        return SapDelimitedParser
            .ParseRows(sapRows, '|', skipHeader: true)
            .Where(cols => cols.Length >= 1)
            .Select(cols => new PicksheetRow
            {
                DeliveryNumber = cols[0]
            })
            .ToArray();
    }


// ── End ────────────────────────────────────────────────────────────────

}

