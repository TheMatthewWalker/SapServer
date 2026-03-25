using System.Collections.Concurrent;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;
using SAPFunctionsOCX;

namespace SapServer.Services;

/// <summary>
/// Owns a single dedicated STA thread and a persistent SAP GUI COM connection.
///
/// Why STA?
/// SAPFunctionsOCX is a COM object. COM objects must be used from the apartment
/// thread that created them; for legacy in-process COM servers this is STA.
/// .NET thread-pool threads are MTA — so we create our own STA threads and keep
/// the COM objects alive for the lifetime of the application.
///
/// Each worker:
///   1. Creates a SAPFunctions COM object and logs in with the service account.
///   2. Loops on a BlockingCollection, executing queued RFC work items serially.
///   3. On completion, sets the TaskCompletionSource so the awaiting HTTP thread resumes.
///   4. Responds to Ping() keep-alive requests from the session monitor.
/// </summary>
internal sealed class SapStaWorker : IDisposable
{
    private readonly Thread _staThread;
    private readonly BlockingCollection<SapWorkItem> _queue;
    private readonly SapPoolOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    // SAP COM object — must ONLY be touched from _staThread
    // Typed as SAPFunctions (COM interface) so .NET uses vtable dispatch, not IDispatch
    // reflection. Dynamic dispatch via IDispatch fails with DISP_E_BADCALLEE on this OCX.
    private SAPFunctionsOCX.SAPFunctions? _sapFunctions;
    private volatile bool _isConnected;
    private DateTime _lastActivity = DateTime.UtcNow;

    public int      SlotId       { get; }
    public bool     IsConnected  => _isConnected;
    public int      QueueDepth   => _queue.Count;
    public DateTime LastActivity => _lastActivity;

    public SapStaWorker(int slotId, SapPoolOptions options, ILogger logger)
    {
        SlotId   = slotId;
        _options = options;
        _logger  = logger;
        _queue   = new BlockingCollection<SapWorkItem>(options.MaxQueueDepth);

        _staThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name         = $"SAP-STA-{slotId}"
        };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    /// <summary>
    /// Queues an RFC work item for execution on this worker's STA thread.
    /// Returns immediately; the caller awaits item.Tcs.Task.
    /// </summary>
    /// <exception cref="PoolExhaustedException">Queue is full.</exception>
    public void Enqueue(SapWorkItem item)
    {
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException(
                $"SAP worker slot {SlotId} is full (max queue depth = {_options.MaxQueueDepth}).");
    }

    /// <summary>
    /// Queues an RFC_PING keep-alive. Fire-and-forget — result is discarded.
    /// </summary>
    public void Ping()
    {
        var pingRequest = new RfcRequest { FunctionName = "RFC_PING" };
        var tcs = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Best-effort — if queue is full the ping is silently dropped
        _queue.TryAdd(new SapWorkItem(pingRequest, tcs, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // STA thread loop
    // -------------------------------------------------------------------------

    private void WorkerLoop()
    {
        try { Connect(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slot {SlotId} failed initial SAP connection — will retry on first request.", SlotId);
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                ProcessItem(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path
        }

        Disconnect();
    }

    private void ProcessItem(SapWorkItem item)
    {
        try
        {
            EnsureConnected();
            var response  = ExecuteRfc(item.Request);
            _lastActivity = DateTime.UtcNow;
            item.Tcs.TrySetResult(response);
        }
        catch (SapConnectionException ex)
        {
            _logger.LogWarning(ex, "SAP connection lost on slot {SlotId}; reconnecting and retrying '{Function}'.",
                SlotId, item.Request.FunctionName);
            try
            {
                Connect();
                var response  = ExecuteRfc(item.Request);
                _lastActivity = DateTime.UtcNow;
                item.Tcs.TrySetResult(response);
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "RFC '{Function}' failed on slot {SlotId} after reconnect.",
                    item.Request.FunctionName, SlotId);
                item.Tcs.TrySetException(retryEx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RFC call '{Function}' failed on slot {SlotId}.",
                item.Request.FunctionName, SlotId);
            item.Tcs.TrySetException(ex);
        }
    }

    // -------------------------------------------------------------------------
    // SAP COM connection management (must run on _staThread)
    // -------------------------------------------------------------------------

    private void Connect()
    {
        try
        {
            _sapFunctions = new SAPFunctionsOCX.SAPFunctions();

            dynamic conn  = _sapFunctions!.Connection;
            conn.System   = _options.ServiceAccount.System;
            conn.Client   = _options.ServiceAccount.Client;
            conn.SystemID = _options.ServiceAccount.SystemId;
            conn.User     = _options.ServiceAccount.User;
            conn.Password = _options.ServiceAccount.Password;
            conn.Language = _options.ServiceAccount.Language;

            bool loggedOn = conn.Logon(0, true);
            if (!loggedOn)
                throw new SapConnectionException(SlotId,
                    "SAP Logon() returned false. Check service-account credentials.");

            _isConnected  = true;
            _lastActivity = DateTime.UtcNow;
            _logger.LogInformation("SAP slot {SlotId} connected as '{User}'.",
                SlotId, _options.ServiceAccount.User);
        }
        catch (Exception ex) when (ex is not SapConnectionException)
        {
            throw new SapConnectionException(SlotId, "Failed to establish SAP connection.", ex);
        }
    }

    private void EnsureConnected()
    {
        if (_isConnected) return;

        _logger.LogInformation("Slot {SlotId} attempting reconnection.", SlotId);
        Thread.Sleep(_options.ReconnectDelayMs);
        Connect();
    }

    private void Disconnect()
    {
        try
        {
            if (_sapFunctions is not null)
            {
                dynamic conn = _sapFunctions.Connection;
                conn.Logoff();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during SAP logoff on slot {SlotId}.", SlotId);
        }
        finally
        {
            _sapFunctions = null;
            _isConnected  = false;
        }
    }

    // -------------------------------------------------------------------------
    // RFC execution (runs on _staThread)
    // -------------------------------------------------------------------------

    private RfcResponse ExecuteRfc(RfcRequest request)
    {
        dynamic func;
        try
        {
            func = _sapFunctions!.Add(request.FunctionName);
        }
        catch (Exception ex)
        {
            throw new SapExecutionException(
                request.FunctionName,
                $"Could not create RFC function object for '{request.FunctionName}'.",
                ex.Message);
        }

        // Scalar import parameters — func.exports("KEY").Value pattern (lowercase, indexer call)
        foreach (var (key, value) in request.ImportParameters)
        {
            if (value is not null)
                func.exports(key).Value = UnwrapJson(value);
        }

        // Input tables — clear with Freetable() then populate rows
        try
        {
            foreach (var (tableName, rows) in request.InputTables)
            {
                dynamic table = func.Tables(tableName);
                table.Freetable();
                foreach (var row in rows)
                {
                    dynamic sapRow = table.Rows.Add();
                    foreach (var (col, val) in row)
                    {
                        if (val is not null)
                            sapRow[col] = UnwrapJson(val);
                    }
                }
            }

            // Input table Items — clear with Freetable() then populate rows
            foreach (var (tableName, rows) in request.InputTablesItems)
            {
                dynamic table = func.Tables.Item(tableName);
                table.Freetable();
                foreach (var row in rows)
                {
                    dynamic sapRow = table.Rows.Add();
                    foreach (var (col, val) in row)
                    {
                        if (val is not null)
                            sapRow[col] = UnwrapJson(val);
                    }
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new SapExecutionException(request.FunctionName,
                $"Failed to populate input tables for '{request.FunctionName}' (HRESULT 0x{ex.ErrorCode:X8}).",
                ex.Message);
        }

        // Cast to the typed IFunction interface so Call() is invoked via FUNC dispatch
        // (not PROPERTYGET), which is required for the COM server to populate Exception.
        var typedFunc = (SAPFunctionsOCX.IFunction)func;

        bool success;
        try
        {
            //success = typedFunc.Call();
            success = func.Call;
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            throw new SapExecutionException(request.FunctionName,
                $"SAP call failed (HRESULT 0x{ex.ErrorCode:X8}).", ex.Message);
        }

        if (!success)
        {
            string exceptionCode = typedFunc.Exception ?? "";

            string? sapMsg = TryReadReturnMessage(func, out string returnTableDiag);

            _logger.LogWarning(
                "RFC '{Function}' failed — Exception: '{ExCode}', ReturnTable: {RetDiag}, ReturnMsg: '{RetMsg}'",
                request.FunctionName, exceptionCode, returnTableDiag, sapMsg ?? "(none)");

            // The SAP OCX drops the connection after any failed call — always mark disconnected
            // so EnsureConnected() reconnects before the next request.
            _isConnected = false;

            if (IsCommunicationError(exceptionCode))
                throw new SapConnectionException(SlotId,
                    $"SAP communication failure during '{request.FunctionName}': {exceptionCode}.");

            string detail = !string.IsNullOrEmpty(sapMsg)
                ? (string.IsNullOrEmpty(exceptionCode) ? sapMsg : $"{exceptionCode}: {sapMsg}")
                : (!string.IsNullOrEmpty(exceptionCode) ? exceptionCode : $"RFC call to '{request.FunctionName}' failed (no detail available).");

            throw new SapExecutionException(
                request.FunctionName,
                $"RFC call to '{request.FunctionName}' returned {exceptionCode}.",
                detail);
        }

        return BuildResponse(func, request);
    }

    /// <summary>
    /// System.Text.Json deserialises <c>object?</c> values as <see cref="System.Text.Json.JsonElement"/>,
    /// which COM cannot marshal to a VARIANT. Unwrap to the underlying CLR primitive.
    /// </summary>
    private static object UnwrapJson(object value)
    {
        // COM VARIANT doesn't support .NET decimal — coerce to double first
        if (value is decimal d) return (double)d;

        if (value is not System.Text.Json.JsonElement je) return value;
        return je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String  => je.GetString() ?? string.Empty,
            System.Text.Json.JsonValueKind.Number
                when je.TryGetInt64(out long l)    => l,
            System.Text.Json.JsonValueKind.Number  => je.GetDouble(),
            System.Text.Json.JsonValueKind.True    => true,
            System.Text.Json.JsonValueKind.False   => false,
            _                                      => je.ToString()
        };
    }

    private static string? TryReadReturnMessage(dynamic func, out string diag)
    {
        try
        {
            dynamic ret      = func.tables.Item("RETURN");
            int     rowCount = 0;
            var     messages = new List<string>();

            foreach (var row in ret.Rows)
            {
                rowCount++;
                // Prefer the pre-formatted MESSAGE field; fall back to MESSAGE_V1-V4
                string msg = row["MESSAGE"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(msg))
                {
                    var parts = new[]
                    {
                        row["MESSAGE_V1"]?.ToString(),
                        row["MESSAGE_V2"]?.ToString(),
                        row["MESSAGE_V3"]?.ToString(),
                        row["MESSAGE_V4"]?.ToString(),
                    };
                    msg = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                }
                if (!string.IsNullOrWhiteSpace(msg))
                    messages.Add($"[{row["TYPE"]}] {msg}");
            }

            diag = $"{rowCount} row(s)";
            return messages.Count > 0 ? string.Join("; ", messages) : null;
        }
        catch (Exception ex)
        {
            diag = $"table access failed: {ex.Message}";
        }
        return null;
    }

    private static RfcResponse BuildResponse(dynamic func, RfcRequest request)
    {
        var parameters = new Dictionary<string, object?>();
        var tables     = new Dictionary<string, List<Dictionary<string, object?>>>();

        // Read scalar export (SAP IMPORTING) parameters — lowercase func.imports(name).Value
        foreach (var paramName in request.ExportParameters)
        {
            try   { parameters[paramName] = func.imports(paramName)?.Value?.ToString(); }
            catch { parameters[paramName] = null; }
        }

        // Read structure export parameters — positional fields joined with a space
        // Mirrors VB: Set x = MyFunc.imports("MESSG") / x(1) & " " & x(2) & ...
        foreach (var (paramName, fieldCount) in request.StructExportParameters)
        {
            try
            {
                dynamic s      = func.imports(paramName);
                var     parts  = new List<string>(fieldCount);
                for (int i = 1; i <= fieldCount; i++)
                {
                    try { parts.Add(s(i)?.ToString() ?? ""); }
                    catch { parts.Add(""); }
                }
                parameters[paramName] = string.Join(" ", parts).Trim();
            }
            catch { parameters[paramName] = null; }
        }

        // Read output tables — lowercase func.tables.Item(name), foreach over rows
        foreach (var (tableName, fields) in request.OutputTables)
        {
            var resultRows = new List<Dictionary<string, object?>>();
            try
            {
                dynamic table = func.tables.Item(tableName);

                foreach (var sapRow in table.Rows)
                {
                    var row = new Dictionary<string, object?>();

                    if (fields.Count > 0)
                    {
                        foreach (var field in fields)
                        {
                            try   { row[field] = sapRow[field]?.ToString(); }
                            catch { row[field] = null; }
                        }
                    }
                    else
                    {
                        // No fields specified — read the WA (work area) column
                        try { row["WA"] = sapRow["WA"]?.ToString(); }
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

    private static bool IsCommunicationError(string exceptionCode) =>
        exceptionCode is "RFC_COMMUNICATION_FAILURE"
                      or "RFC_SYSTEM_FAILURE"
                      or "RFC_ABAP_RUNTIME_FAILURE";

    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _queue.Dispose();
    }
}
