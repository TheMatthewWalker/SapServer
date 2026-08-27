using SAP.Middleware.Connector;
using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>
/// Shared NCo call mechanics — CreateFunction, populate parameters/tables,
/// Invoke, read back exports/tables — used identically by both the
/// stateless pool (NcoStatelessPool, one call at a time on whatever thread
/// is running) and pinned sessions (NcoWorker, a sequence of calls on one
/// dedicated thread). The dispatch logic itself doesn't differ between the
/// two; only how the RfcDestination was obtained and which thread runs it
/// does, so this is the one place that logic lives.
/// </summary>
internal static class NcoRfcExecutor
{
    public static RfcResponse Execute(RfcDestination destination, RfcRequest request, int identity)
    {
        IRfcFunction func;
        try
        {
            func = destination.Repository.CreateFunction(request.FunctionName);
        }
        catch (Exception ex)
        {
            // Failing to even look up the function's metadata (as opposed to
            // Invoke() itself failing) almost always means the connection is
            // stale, not that this particular function module is broken.
            throw new SapConnectionException(identity,
                $"Could not look up RFC metadata for '{request.FunctionName}' — SAP session likely stale.", ex);
        }

        PopulateInputs(func, request);

        try
        {
            func.Invoke(destination);
        }
        catch (RfcCommunicationException ex)
        {
            throw new SapConnectionException(identity,
                $"SAP NCo communication failure during '{request.FunctionName}'.", ex);
        }
        catch (RfcAbapRuntimeException ex)
        {
            throw new SapConnectionException(identity,
                $"SAP NCo ABAP runtime failure during '{request.FunctionName}'.", ex);
        }
        catch (RfcAbapBaseException ex)
        {
            // A real business-level ABAP exception raised by the function
            // module itself — not a connection problem. RfcAbapBaseException
            // has no "Key" property (confirmed via reflection against the
            // real assembly — the base class only exposes PlainText, plus
            // the inherited numeric ErrorCode); the exception's own runtime
            // type (RfcAbapApplicationException/RfcAbapClassException/
            // RfcAbapMessageException/RfcAbapRuntimeException) is what
            // actually identifies which kind of ABAP failure this was.
            throw new SapExecutionException(request.FunctionName,
                $"RFC call to '{request.FunctionName}' raised {ex.GetType().Name}.",
                string.IsNullOrEmpty(ex.PlainText) ? ex.Message : ex.PlainText);
        }

        return BuildResponse(func, request);
    }

    private static void PopulateInputs(IRfcFunction func, RfcRequest request)
    {
        foreach (var (key, value) in request.ImportParameters)
            if (value is not null)
                func.SetValue(key, Unwrap(value));

        foreach (var (structName, fields) in request.StructImportParameters)
        {
            var s = func.GetStructure(structName);
            foreach (var (field, value) in fields)
                if (value is not null)
                    s.SetValue(field, Unwrap(value));
        }

        foreach (var (tableName, rows) in request.InputTables)
            PopulateTable(func.GetTable(tableName), rows);

        // NCo has no InputTables/InputTablesItems split — SAPFunctions64's
        // func.Tables(name) vs func.Tables.Item(name) distinction was a COM
        // OCX quirk, not an RFC-level one. Both map to GetTable(name) here.
        foreach (var (tableName, rows) in request.InputTablesItems)
            PopulateTable(func.GetTable(tableName), rows);
    }

    private static void PopulateTable(IRfcTable table, List<Dictionary<string, object?>> rows)
    {
        table.Clear();
        foreach (var row in rows)
        {
            // IRfcTable.Append() is void (confirmed via reflection against the
            // real assembly, unlike the DevStub's original guess that it
            // returned the new row directly) — it appends a blank row and
            // moves CurrentIndex/CurrentRow to it, which is the real API's
            // own accessor for the row just appended.
            table.Append();
            var line = table.CurrentRow;
            foreach (var (col, val) in row)
                if (val is not null)
                    line.SetValue(col, Unwrap(val));
        }
    }

    private static RfcResponse BuildResponse(IRfcFunction func, RfcRequest request)
    {
        var parameters = new Dictionary<string, object?>();
        var tables     = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var paramName in request.ExportParameters)
        {
            try   { parameters[paramName] = func.GetString(paramName); }
            catch { parameters[paramName] = null; }
        }

        // Confirmed for real against a live IIS deploy: this was a genuine
        // bug, not a "no caller needs it" stub — BdcBuilder.Build() registers
        // MESSG via ReadStructParam for every BDC-based write in the app
        // (backflush, drumming, scrap, GR/TO reversal, MM01/CS01 creation,
        // etc.), and every one of them silently got back a blank MESSG,
        // making ParseBdcResponse's Type/Message/DocumentNumber all come back
        // empty regardless of whether the underlying SAP transaction actually
        // succeeded or failed. IRfcStructure.Metadata gives positional field
        // access (FieldCount + an indexer exposing .Name per field) exactly
        // like the old COM/VB x(1)/x(2)/.../x(N) convention ReadStructParam's
        // field-count-based signature was designed to mirror — this reads
        // each of the requested fieldCount fields by position (not by name,
        // since the real Z-structure's field names were never confirmed) and
        // joins them with a literal " ", unTrimmed, matching the old
        // behaviour exactly (SAP's fixed-width CHAR fields already contain
        // their own trailing padding, which is exactly why ParseBdcResponse's
        // regex uses \s+ between tokens and existing test fixtures already
        // have multiple spaces between MESSG's type/class/number tokens).
        foreach (var (paramName, fieldCount) in request.StructExportParameters)
        {
            try
            {
                var structure = func.GetStructure(paramName);
                var count     = Math.Min(fieldCount, structure.Metadata.FieldCount);
                var values    = new List<string>(count);

                for (var i = 0; i < count; i++)
                    values.Add(structure.GetString(structure.Metadata[i].Name));

                parameters[paramName] = string.Join(" ", values);
            }
            catch { parameters[paramName] = null; }
        }

        foreach (var (tableName, fields) in request.OutputTables)
        {
            var resultRows = new List<Dictionary<string, object?>>();
            try
            {
                var table = func.GetTable(tableName);
                for (int i = 0; i < table.RowCount; i++)
                {
                    var line = table[i];
                    var row  = new Dictionary<string, object?>();

                    if (fields.Count > 0)
                    {
                        foreach (var field in fields)
                        {
                            try   { row[field] = line.GetString(field); }
                            catch { row[field] = null; }
                        }
                    }
                    else
                    {
                        // No fields specified — read the WA (work area)
                        // column, correct for ZRFC_READ_TABLES.
                        try { row["WA"] = line.GetString("WA"); }
                        catch { /* WA column does not exist on this table */ }
                    }

                    resultRows.Add(row);
                }
            }
            catch { /* Table does not exist or has no rows — return empty list */ }

            tables[tableName] = resultRows;
        }

        return new RfcResponse { Parameters = parameters, Tables = tables };
    }

    /// <summary>
    /// Same unwrap contract as the old SapStaWorker.UnwrapJson — System.Text.Json
    /// deserialises object? values as JsonElement, which NCo's SetValue cannot
    /// accept any more than a COM VARIANT could.
    /// </summary>
    private static object Unwrap(object value)
    {
        if (value is decimal d) return (double)d;

        if (value is not System.Text.Json.JsonElement je) return value;
        return je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => je.GetString() ?? string.Empty,
            System.Text.Json.JsonValueKind.Number when je.TryGetInt64(out long l) => l,
            System.Text.Json.JsonValueKind.Number => je.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => je.ToString()
        };
    }
}
