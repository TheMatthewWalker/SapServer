using Microsoft.Extensions.Options;
using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>
/// Executes an <see cref="RfcRequest"/> through SAP NCo and returns an
/// <see cref="RfcResponse"/> in the exact same shape SapStaWorker.ExecuteRfc
/// produces from the COM path — RfcRequest/RfcResponse/RfcRequestBuilder are
/// plain POCOs with no COM dependency, so every existing Helpers/* request
/// builder (WhereClauseBuilder, SapDelimitedParser, etc.) works unchanged
/// against this executor. Only the transport differs.
/// </summary>
public interface INcoRfcService
{
    Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken ct = default);
}

/// <summary>
/// SAP NCo rebuild spike — see CLAUDE.md's "SAP NCo Spike" section for the
/// full rationale. Deliberately narrow scope: connect, execute one request,
/// bound concurrency, and confirm resource cleanup. Registered as a singleton
/// (Program.cs) so the registered destination configuration and connection
/// pool live for the app's lifetime, mirroring SapConnectionPool.
///
/// COM marshal / garbage-collection finding (the thing this spike exists to
/// settle): SAP NCo is NOT a COM component, so SapStaWorker's COM-specific
/// cleanup has no equivalent here.
///   - No Marshal.ReleaseComObject: IRfcFunction/IRfcTable/IRfcStructure are
///     plain managed objects — NCo's P/Invoke layer wraps the unmanaged
///     librfc32 calls internally, so these are eligible for ordinary GC once
///     they go out of scope, same as any other .NET object.
///   - No func.RemoveAll()-equivalent leak: SapStaWorker calls RemoveAll()
///     every call because SAPFunctions64.Add() leaks an entry into a COM-side
///     Functions collection with no automatic cleanup. NCo's
///     RfcRepository.CreateFunction() has no equivalent registry — each call
///     returns a fresh, self-contained function instance that isn't tracked
///     anywhere else, so there is nothing to explicitly release per call.
///   - The one real resource to mind is the RfcDestination's own connection
///     pool (SapNco:PoolSize/MaxPoolSize) — not closed per call; NCo returns
///     pooled connections internally once Invoke() completes.
/// This has not been verified against a live SAP system or under sustained
/// load (this sandbox has neither) — treat it as the hypothesis this spike
/// is meant to confirm, not a settled fact, until it's been run for real.
/// </summary>
public sealed class NcoRfcService : INcoRfcService, IDisposable
{
    private readonly SapNcoOptions _options;
    private readonly ILogger<NcoRfcService> _logger;

    // Bounds how many NCo calls this process drives concurrently. NCo's
    // RfcDestination is documented thread-safe with its own internal
    // connection pool, so — unlike the COM path — there is no dedicated
    // thread per slot to route to; this gate exists purely so a burst of
    // callers can't spin up unbounded concurrent Task.Run work, and to give
    // ConcurrencyCheck (NcoTestController) something concrete to observe.
    private readonly SemaphoreSlim _concurrencyGate;

    // RegisterDestinationConfiguration may only be called once per process —
    // a second call throws. Guards against that if NcoRfcService were ever
    // constructed more than once (it shouldn't be, as a singleton, but the
    // guard costs nothing and documents the constraint).
    private static int _configurationRegistered;

    public NcoRfcService(IOptions<SapNcoOptions> options, ILogger<NcoRfcService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _concurrencyGate = new SemaphoreSlim(_options.MaxPoolSize, _options.MaxPoolSize);

        if (Interlocked.Exchange(ref _configurationRegistered, 1) == 0)
        {
            RfcDestinationManager.RegisterDestinationConfiguration(new NcoDestinationConfiguration(_options));
            _logger.LogInformation(
                "SAP NCo destination configuration registered ({Destination}, pool size {PoolSize}-{MaxPoolSize}).",
                _options.DestinationName, _options.PoolSize, _options.MaxPoolSize);
        }
    }

    public async Task<RfcResponse> ExecuteAsync(RfcRequest request, CancellationToken ct = default)
    {
        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // NCo calls block the calling thread (synchronous under the hood).
            // Task.Run hands the blocking work to a thread-pool thread instead
            // of the calling async context, so concurrent callers actually
            // overlap rather than queuing behind one thread — the direct
            // analogue of SapConnectionPool routing across multiple STA slots,
            // minus the STA requirement itself (NCo has none).
            return await Task.Run(() => ExecuteSync(request), ct).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private RfcResponse ExecuteSync(RfcRequest request)
    {
        int threadId = Environment.CurrentManagedThreadId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("NCo RFC '{Function}' starting on thread {ThreadId}.", request.FunctionName, threadId);

        try
        {
            RfcDestination destination;
            try
            {
                destination = RfcDestinationManager.GetDestination(_options.DestinationName);
            }
            catch (Exception ex)
            {
                throw new SapConnectionException(0,
                    $"Could not resolve SAP NCo destination '{_options.DestinationName}'.", ex);
            }

            IRfcFunction func;
            try
            {
                func = destination.Repository.CreateFunction(request.FunctionName);
            }
            catch (Exception ex)
            {
                throw new SapConnectionException(0,
                    $"Could not look up RFC metadata for '{request.FunctionName}' from the SAP repository — " +
                    "check the function name and that the destination can reach SAP.", ex);
            }

            PopulateInputs(func, request);

            try
            {
                func.Invoke(destination);
            }
            catch (RfcCommunicationException ex)
            {
                throw new SapConnectionException(0,
                    $"SAP NCo communication failure during '{request.FunctionName}'.", ex);
            }
            catch (RfcAbapRuntimeException ex)
            {
                throw new SapConnectionException(0,
                    $"SAP NCo ABAP runtime failure during '{request.FunctionName}'.", ex);
            }
            catch (RfcAbapBaseException ex)
            {
                // A real business-level ABAP exception raised by the function
                // module itself (RAISE EXCEPTION) — not a connection problem,
                // so this does not get the reconnect treatment the two
                // catches above imply for their callers.
                throw new SapExecutionException(request.FunctionName,
                    $"RFC call to '{request.FunctionName}' raised {ex.Key}.", ex.Message);
            }

            return BuildResponse(func, request);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation("NCo RFC '{Function}' finished on thread {ThreadId} in {ElapsedMs}ms.",
                request.FunctionName, threadId, stopwatch.ElapsedMilliseconds);
        }
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
        // distinction between func.Tables("name") and func.Tables.Item("name")
        // was a COM-OCX quirk, not an RFC-level one. Both map to the same
        // GetTable(name) here.
        foreach (var (tableName, rows) in request.InputTablesItems)
            PopulateTable(func.GetTable(tableName), rows);
    }

    private static void PopulateTable(IRfcTable table, List<Dictionary<string, object?>> rows)
    {
        table.Clear();
        foreach (var row in rows)
        {
            var line = table.Append();
            foreach (var (col, val) in row)
                if (val is not null)
                    line.SetValue(col, Unwrap(val));
        }
    }

    private static RfcResponse BuildResponse(IRfcFunction func, RfcRequest request)
    {
        var parameters = new Dictionary<string, object?>();
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var paramName in request.ExportParameters)
        {
            try { parameters[paramName] = func.GetString(paramName); }
            catch { parameters[paramName] = null; }
        }

        // Structure export parameters aren't wired up yet — SapStaWorker's
        // positional x(1)/x(2)/... convention doesn't map onto NCo's
        // named-field IRfcStructure, and nothing in this spike's limited
        // endpoint set needs one. Left as a documented gap rather than a
        // guessed-at implementation.
        foreach (var (paramName, _) in request.StructExportParameters)
            parameters[paramName] = null;

        foreach (var (tableName, fields) in request.OutputTables)
        {
            var resultRows = new List<Dictionary<string, object?>>();
            try
            {
                var table = func.GetTable(tableName);
                for (int i = 0; i < table.RowCount; i++)
                {
                    var line = table[i];
                    var row = new Dictionary<string, object?>();

                    if (fields.Count > 0)
                    {
                        foreach (var field in fields)
                        {
                            try { row[field] = line.GetString(field); }
                            catch { row[field] = null; }
                        }
                    }
                    else
                    {
                        // No fields specified — read the WA (work area) column,
                        // correct for ZRFC_READ_TABLES's data_display table.
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
    /// Same unwrap contract as SapStaWorker.UnwrapJson — System.Text.Json
    /// deserialises object? values as JsonElement, which the NCo API cannot
    /// accept any more than a COM VARIANT could. Duplicated rather than
    /// shared so this spike stays fully isolated from the COM path.
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

    public void Dispose() => _concurrencyGate.Dispose();
}
