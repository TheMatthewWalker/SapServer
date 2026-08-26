using System.Collections.Concurrent;
using SAP.Middleware.Connector;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;

namespace SapServer.Services.Nco;

/// <summary>
/// Owns one dedicated background thread and one pinned SAP NCo connection.
///
/// Why a dedicated thread, given NCo isn't COM and doesn't need an STA
/// apartment? RfcSessionManager.BeginContext pins the CALLING THREAD to one
/// physical pooled connection for a destination — required so a stateful
/// multi-call sequence (a create-BAPI followed by BAPI_TRANSACTION_COMMIT/
/// ROLLBACK, i.e. AcquireWorker/ExecuteOnWorkerAsync) lands on the same SAP
/// session's LUW. That's a SAP-level requirement independent of COM vs NCo —
/// it's why this worker calls BeginContext once at connect time and keeps
/// running every call for its whole life on that same managed thread, rather
/// than bouncing each call through Task.Run onto an arbitrary thread-pool
/// thread the way the original NCo spike's stateless NcoRfcService did (fine
/// for a one-off read like ZRFC_READ_TABLES, wrong for a pinned sequence).
///
/// COM marshal / GC note (carried over from the spike, still holds here):
/// NCo is not COM, so there is no Marshal.ReleaseComObject or
/// SAPFunctions64.RemoveAll()-style leak to guard against — IRfcFunction/
/// IRfcTable/IRfcStructure are plain managed objects, eligible for ordinary
/// GC once they go out of scope.
///
/// UNVERIFIED: BeginContext/EndContext's exact pinning behavior is documented
/// SAP NCo API, not something this sandbox (no live SAP, no real NCo DLLs)
/// can exercise. Validate a real create+commit sequence against a live
/// system before trusting any elevated/transactional endpoint in production.
/// </summary>
internal sealed class NcoWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<NcoWorkItem> _queue;
    private readonly SapNcoOptions _options;
    private readonly NcoDestinationRegistry _registry;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private readonly SapConnectionOptions _serviceAccount;

    // Fixed for service workers (SLOT_n). Regenerated per-connect for elevated
    // workers (SLOT_n_ELEVATED_<guid>) so a later acquisition with a different
    // user's credentials can never resolve against another user's destination
    // still cached under the same name inside NCo's own destination cache.
    private string _destinationName;

    private RfcDestination? _destination;
    private volatile bool _isConnected;
    private DateTime _lastActivity = DateTime.UtcNow;

    public int      SlotId       { get; }
    public bool     IsConnected  => _isConnected;
    public int      QueueDepth   => _queue.Count;
    public DateTime LastActivity => _lastActivity;
    public bool     IsElevated   { get; }

    public NcoWorker(int slotId, SapNcoOptions options, NcoDestinationRegistry registry, ILogger logger,
        bool isElevated = false, SapConnectionOptions? serviceAccount = null)
    {
        SlotId           = slotId;
        _options         = options;
        _registry        = registry;
        _logger          = logger;
        IsElevated       = isElevated;
        _serviceAccount  = serviceAccount ?? options.ServiceAccount;
        _destinationName = $"SLOT_{slotId}{(isElevated ? "_ELEVATED" : "")}";
        _queue           = new BlockingCollection<NcoWorkItem>(options.MaxQueueDepth);

        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name         = $"SAP-NCO-{slotId}{(isElevated ? "-ELEVATED" : "")}"
        };
        _thread.Start();
    }

    public void Enqueue(NcoWorkItem item)
    {
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException(
                $"SAP NCo worker slot {SlotId} is full (max queue depth = {_options.MaxQueueDepth}).");
    }

    public void Ping()
    {
        var pingRequest = new RfcRequest { FunctionName = "RFC_PING" };
        var tcs = new TaskCompletionSource<RfcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.TryAdd(new NcoWorkItem(pingRequest, tcs, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Worker thread loop
    // -------------------------------------------------------------------------

    private void WorkerLoop()
    {
        if (IsElevated)
        {
            _logger.LogInformation("Elevated NCo slot {SlotId} started (unconnected, awaiting an elevated request).", SlotId);
        }
        else
        {
            try { Connect(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Slot {SlotId} failed initial SAP connection — will retry on first request.", SlotId);
            }
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                if (item.IsControl)
                {
                    ProcessControlItem(item);
                    continue;
                }

                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Tcs!.TrySetCanceled(item.CancellationToken);
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

    private void ProcessControlItem(NcoWorkItem item)
    {
        try
        {
            switch (item.ControlKind)
            {
                case NcoControlKind.ElevatedLogon:
                    Connect(item.ElevatedCreds);
                    item.ControlTcs!.TrySetResult(true);
                    break;

                case NcoControlKind.ElevatedLogoff:
                    Disconnect();
                    item.ControlTcs!.TrySetResult(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Elevated control item '{Kind}' failed on slot {SlotId}.", item.ControlKind, SlotId);
            item.ControlTcs!.TrySetException(ex);
        }
    }

    public Task<bool> LogonElevatedAsync(SapConnectionOptions creds)
    {
        var tcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new NcoWorkItem(NcoControlKind.ElevatedLogon, creds, tcs);
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException($"Elevated SAP NCo worker slot {SlotId} is full.");
        return tcs.Task;
    }

    public Task<bool> LogoffElevatedAsync()
    {
        var tcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new NcoWorkItem(NcoControlKind.ElevatedLogoff, null, tcs);
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException($"Elevated SAP NCo worker slot {SlotId} is full.");
        return tcs.Task;
    }

    private void ProcessItem(NcoWorkItem item)
    {
        var request = item.Request!;
        var tcs     = item.Tcs!;

        int threadId = Environment.CurrentManagedThreadId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("RFC '{Function}' starting on slot {SlotId}, thread {ThreadId}.",
            request.FunctionName, SlotId, threadId);

        try
        {
            EnsureConnected();
            var response  = ExecuteRfc(request);
            _lastActivity = DateTime.UtcNow;
            tcs.TrySetResult(response);
        }
        catch (SapConnectionException ex) when (IsElevated)
        {
            _logger.LogError(ex, "Elevated SAP NCo slot {SlotId} is not connected for '{Function}' — not auto-reconnecting.",
                SlotId, request.FunctionName);
            tcs.TrySetException(ex);
        }
        catch (SapConnectionException ex)
        {
            _logger.LogWarning(ex, "SAP connection lost on slot {SlotId}; reconnecting and retrying '{Function}'.",
                SlotId, request.FunctionName);
            try
            {
                Connect();
                var response  = ExecuteRfc(request);
                _lastActivity = DateTime.UtcNow;
                tcs.TrySetResult(response);
            }
            catch (Exception retryEx)
            {
                _logger.LogError(retryEx, "RFC '{Function}' failed on slot {SlotId} after reconnect.",
                    request.FunctionName, SlotId);
                tcs.TrySetException(retryEx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RFC call '{Function}' failed on slot {SlotId}.",
                request.FunctionName, SlotId);
            tcs.TrySetException(ex);
        }
        finally
        {
            stopwatch.Stop();
            _logger.LogInformation("RFC '{Function}' finished on slot {SlotId}, thread {ThreadId} in {ElapsedMs}ms — {Outcome}.",
                request.FunctionName, SlotId, threadId, stopwatch.ElapsedMilliseconds,
                tcs.Task.Status == TaskStatus.RanToCompletion ? "OK" : "FAILED"); // net48 lacks Task.IsCompletedSuccessfully
        }
    }

    // -------------------------------------------------------------------------
    // NCo connection management (must run on _thread)
    // -------------------------------------------------------------------------

    private void Connect(SapConnectionOptions? overrideCreds = null)
    {
        var creds = overrideCreds ?? _serviceAccount;

        if (IsElevated)
            _destinationName = $"SLOT_{SlotId}_ELEVATED_{Guid.NewGuid():N}";

        try
        {
            _registry.Register(_destinationName, creds);
            _destination = RfcDestinationManager.GetDestination(_destinationName);
            RfcSessionManager.BeginContext(_destination);
            _destination.Ping();

            _isConnected  = true;
            _lastActivity = DateTime.UtcNow;
            _logger.LogInformation("SAP NCo slot {SlotId} connected as '{User}'.", SlotId, creds.User);
        }
        catch (Exception ex) when (ex is not SapConnectionException)
        {
            throw new SapConnectionException(SlotId, "Failed to establish SAP NCo connection.", ex);
        }
    }

    private void EnsureConnected()
    {
        if (_isConnected) return;

        if (IsElevated)
            throw new SapConnectionException(SlotId,
                "Elevated SAP NCo slot is not connected. Work must not be queued on an elevated slot outside AcquireElevatedWorkerAsync's login.");

        _logger.LogInformation("Slot {SlotId} attempting reconnection.", SlotId);
        Thread.Sleep(_options.ReconnectDelayMs);
        Connect();
    }

    private void Disconnect()
    {
        try
        {
            if (_destination is not null)
                RfcSessionManager.EndContext(_destination);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error ending SAP NCo session context on slot {SlotId}.", SlotId);
        }
        finally
        {
            _registry.Unregister(_destinationName);
            _destination  = null;
            _isConnected  = false;
        }
    }

    // -------------------------------------------------------------------------
    // RFC execution (runs on _thread)
    // -------------------------------------------------------------------------

    private RfcResponse ExecuteRfc(RfcRequest request)
    {
        IRfcFunction func;
        try
        {
            func = _destination!.Repository.CreateFunction(request.FunctionName);
        }
        catch (Exception ex)
        {
            // Failing to even look up the function's metadata (as opposed to
            // Invoke() itself failing) almost always means the pinned session
            // is stale — mirrors SapStaWorker's identical reasoning for a
            // failed func.Add() on the COM path.
            _isConnected = false;
            throw new SapConnectionException(SlotId,
                $"Could not look up RFC metadata for '{request.FunctionName}' — SAP session likely stale.", ex);
        }

        PopulateInputs(func, request);

        try
        {
            func.Invoke(_destination);
        }
        catch (RfcCommunicationException ex)
        {
            _isConnected = false;
            throw new SapConnectionException(SlotId,
                $"SAP NCo communication failure during '{request.FunctionName}'.", ex);
        }
        catch (RfcAbapRuntimeException ex)
        {
            _isConnected = false;
            throw new SapConnectionException(SlotId,
                $"SAP NCo ABAP runtime failure during '{request.FunctionName}'.", ex);
        }
        catch (RfcAbapBaseException ex)
        {
            // A real business-level ABAP exception raised by the function
            // module itself — not a connection problem, so no reconnect.
            throw new SapExecutionException(request.FunctionName,
                $"RFC call to '{request.FunctionName}' raised {ex.Key}.", ex.Message);
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
            var line = table.Append();
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

        // Structure export parameters aren't wired up — SapStaWorker's
        // positional x(1)/x(2)/... convention doesn't map onto NCo's
        // named-field IRfcStructure. No caller currently needs one.
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

    public void Dispose()
    {
        _cts.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _queue.Dispose();
    }
}
