using System.Collections.Concurrent;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;

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
    private dynamic? _sapFunctions;
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
        Connect();

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
            _isConnected = false;
            _logger.LogWarning(ex, "SAP connection lost on slot {SlotId}; will reconnect on next call.", SlotId);
            item.Tcs.TrySetException(ex);
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
            _sapFunctions = Activator.CreateInstance(
                Type.GetTypeFromProgID("SAPFunctions.SAPFunctions")
                ?? throw new InvalidOperationException(
                    "SAPFunctions COM ProgID not found. Ensure SAP GUI 7.x is installed on this server."));

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

        // Set scalar import (SAP EXPORTING) parameters
        foreach (var (key, value) in request.ImportParameters)
        {
            if (value is not null)
                func.Exports(key).Value = value;
        }

        // Populate input tables
        foreach (var (tableName, rows) in request.InputTables)
        {
            dynamic table = func.Tables(tableName);
            foreach (var row in rows)
            {
                table.Rows.Add();
                int idx = (int)table.Rows.Count - 1;
                foreach (var (col, val) in row)
                {
                    if (val is not null)
                        table.Rows.Item(idx).Value(col) = val;
                }
            }
        }

        bool success = func.Call;
        if (!success)
        {
            string? sapMsg = TryReadReturnMessage(func);
            throw new SapExecutionException(
                request.FunctionName,
                $"RFC call to '{request.FunctionName}' returned false.",
                sapMsg);
        }

        return BuildResponse(func, request);
    }

    private static string? TryReadReturnMessage(dynamic func)
    {
        try
        {
            dynamic ret = func.Tables("RETURN");
            if ((int)ret.Rows.Count > 0)
                return ret.Rows.Item(0).Value("MESSAGE")?.ToString();
        }
        catch { /* RETURN table may not exist for this function */ }
        return null;
    }

    private static RfcResponse BuildResponse(dynamic func, RfcRequest request)
    {
        var parameters = new Dictionary<string, object?>();
        var tables     = new Dictionary<string, List<Dictionary<string, object?>>>();

        // Read scalar export (SAP IMPORTING) parameters
        foreach (var paramName in request.ExportParameters)
        {
            try   { parameters[paramName] = func.Imports(paramName)?.Value?.ToString(); }
            catch { parameters[paramName] = null; }
        }

        // Read output tables
        foreach (var (tableName, fields) in request.OutputTables)
        {
            var resultRows = new List<Dictionary<string, object?>>();
            try
            {
                dynamic table    = func.Tables(tableName);
                int     rowCount = (int)table.Rows.Count;

                for (int i = 0; i < rowCount; i++)
                {
                    dynamic sapRow = table.Rows.Item(i);
                    var row = new Dictionary<string, object?>();

                    if (fields.Count > 0)
                    {
                        // Caller specified which fields to extract
                        foreach (var field in fields)
                        {
                            try   { row[field] = sapRow.Value(field)?.ToString(); }
                            catch { row[field] = null; }
                        }
                    }
                    else
                    {
                        // No fields specified — fall back to reading the WA (work area) column.
                        // This is the output format used by ZRFC_READ_TABLES.
                        try { row["WA"] = sapRow.Value("WA")?.ToString(); }
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
