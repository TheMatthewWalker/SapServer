using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SapServer.Configuration;
using SapServer.Exceptions;
using SapServer.Models;
using SAPFunctions64;

namespace SapServer.Services;

/// <summary>
/// Owns a single dedicated STA thread and a persistent SAP GUI COM connection.
///
/// Why STA?
/// SAPFunctions64 is a COM object. COM objects must be used from the apartment
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

    // This worker's own service-account login — assigned once at construction
    // (see SapConnectionPool's constructor, which hands out SapPoolOptions.
    // ServiceAccounts[i % count] per worker when that list is populated).
    // Only meaningful for non-elevated workers; elevated workers never use
    // this (they log in solely via LogonElevatedAsync's per-user creds).
    private readonly SapConnectionOptions _serviceAccount;

    // Prevents concurrent SAPFunctions64 COM initialization across STA threads.
    // The OCX has a race in its constructor/Connection property when multiple instances
    // are created simultaneously — serializing here eliminates the AccessViolationException
    // that silently kills worker threads 1+ at startup.
    private static readonly SemaphoreSlim _connectLock = new(1, 1);

    // SAP COM object — must ONLY be touched from _staThread
    // Typed as SAPFunctions (COM interface) so .NET uses vtable dispatch, not IDispatch
    // reflection. Dynamic dispatch via IDispatch fails with DISP_E_BADCALLEE on this OCX.
    private SAPFunctions64.SAPFunctions? _sapFunctions;
    private volatile bool _isConnected;
    private DateTime _lastActivity = DateTime.UtcNow;

    public int      SlotId       { get; }
    public bool     IsConnected  => _isConnected;
    public int      QueueDepth   => _queue.Count;
    public DateTime LastActivity => _lastActivity;

    /// <summary>
    /// True for one of the pool's elevated worker slots. Elevated workers do
    /// NOT log in with the service account at startup — they sit logged out
    /// until SapConnectionPool.AcquireElevatedWorkerAsync logs them in with a
    /// specific user's own SAP credentials for one request, then logs them
    /// back out (see ReleaseElevatedWorkerAsync). This is what stops one
    /// user's elevated session from ever being handed to another user.
    /// </summary>
    public bool IsElevated { get; }

    public SapStaWorker(int slotId, SapPoolOptions options, ILogger logger, bool isElevated = false,
        SapConnectionOptions? serviceAccount = null)
    {
        SlotId          = slotId;
        _options        = options;
        _logger         = logger;
        IsElevated      = isElevated;
        _serviceAccount = serviceAccount ?? options.ServiceAccount;
        _queue          = new BlockingCollection<SapWorkItem>(options.MaxQueueDepth);

        _staThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name         = $"SAP-STA-{slotId}{(isElevated ? "-ELEVATED" : "")}"
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
        if (IsElevated)
        {
            // Elevated slots start logged OUT on purpose — they only ever log in with
            // a specific caller's own credentials, via AcquireElevatedWorkerAsync,
            // immediately before an elevated request runs on this thread.
            _logger.LogInformation("Elevated SAP slot {SlotId} started (logged out, awaiting an elevated request).", SlotId);
        }
        else
        {
            try { Connect(); }
            catch (Exception ex) when (IsOutOfMemory(ex))
            {
                _logger.LogCritical(ex,
                    "Slot {SlotId}: unrecoverable OutOfMemoryException during initial SAP connection — terminating process so Task Scheduler restarts it.",
                    SlotId);
                Environment.FailFast($"SapServer OOM on slot {SlotId} during initial connect.", ex);
            }
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

    /// <summary>
    /// Runs an elevated logon/logoff control item on this worker's STA thread.
    /// Deliberately bypasses ProcessItem/ExecuteRfc entirely — this is
    /// session management, not an RFC call.
    /// </summary>
    private void ProcessControlItem(SapWorkItem item)
    {
        try
        {
            switch (item.ControlKind)
            {
                case SapControlKind.ElevatedLogon:
                    Connect(item.ElevatedCreds);
                    item.ControlTcs!.TrySetResult(true);
                    break;

                case SapControlKind.ElevatedLogoff:
                    Disconnect();
                    item.ControlTcs!.TrySetResult(true);
                    break;
            }
        }
        catch (Exception ex) when (IsOutOfMemory(ex))
        {
            _logger.LogCritical(ex,
                "Slot {SlotId}: unrecoverable OutOfMemoryException during elevated control item '{Kind}' — terminating process so Task Scheduler restarts it.",
                SlotId, item.ControlKind);
            Environment.FailFast($"SapServer OOM on slot {SlotId} during elevated control item '{item.ControlKind}'.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Elevated control item '{Kind}' failed on slot {SlotId}.", item.ControlKind, SlotId);
            item.ControlTcs!.TrySetException(ex);
        }
    }

    /// <summary>
    /// Logs this elevated worker in with one specific user's own SAP
    /// credentials. Only valid on a worker created with isElevated: true —
    /// see SapConnectionPool.AcquireElevatedWorkerAsync, which is the only
    /// caller. Runs on this worker's STA thread via the same queue ordinary
    /// RFC work uses, so it can never race with a concurrent RFC call.
    /// </summary>
    public Task<bool> LogonElevatedAsync(SapConnectionOptions creds)
    {
        var tcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new SapWorkItem(SapControlKind.ElevatedLogon, creds, tcs);
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException($"Elevated SAP worker slot {SlotId} is full.");
        return tcs.Task;
    }

    /// <summary>
    /// Logs this elevated worker back out. Always call this in a finally
    /// block after an elevated request completes (success OR failure) — see
    /// SapConnectionPool.ReleaseElevatedWorkerAsync — so the slot never sits
    /// logged in as one user waiting to be handed to the next caller.
    /// </summary>
    public Task<bool> LogoffElevatedAsync()
    {
        var tcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new SapWorkItem(SapControlKind.ElevatedLogoff, null, tcs);
        if (!_queue.TryAdd(item))
            throw new PoolExhaustedException($"Elevated SAP worker slot {SlotId} is full.");
        return tcs.Task;
    }

    private void ProcessItem(SapWorkItem item)
    {
        // Only ever called for non-control items (see WorkerLoop), so Request/Tcs
        // are always populated here — see SapWorkItem's two-constructor split.
        var request = item.Request!;
        var tcs      = item.Tcs!;

        try
        {
            EnsureConnected();
            var response  = ExecuteRfc(request);
            _lastActivity = DateTime.UtcNow;
            tcs.TrySetResult(response);
        }
        catch (Exception ex) when (IsOutOfMemory(ex))
        {
            // A genuine OutOfMemoryException (raw, or wrapped as the InnerException of a
            // SapConnectionException — see ExecuteRfc's func.Add() catch and Connect()'s
            // catch) means the process's memory is already corrupted enough that COM
            // interop can no longer reliably create objects. Microsoft's own guidance is
            // to treat OOM as fatal rather than keep running — continuing here previously
            // meant every subsequent call on every slot kept failing the same way
            // (2026-08-24 downtime) while Task Scheduler's RestartCount/RestartInterval
            // policy (see scripts/install.ps1) never fired, because it only restarts the
            // task when the process actually exits. FailFast forces that exit immediately,
            // skipping finally blocks (which could themselves allocate and re-throw) so
            // the task manager sees a real failure and restarts us within a minute.
            _logger.LogCritical(ex,
                "Slot {SlotId}: unrecoverable OutOfMemoryException while processing '{Function}' — terminating process so Task Scheduler restarts it.",
                SlotId, request.FunctionName);
            Environment.FailFast(
                $"SapServer OOM on slot {SlotId} during '{request.FunctionName}' — forcing restart via Task Scheduler.",
                ex);
        }
        catch (SapConnectionException ex) when (IsElevated)
        {
            // No auto-reconnect on an elevated slot — Connect() here would log
            // in as the shared service account, exactly what elevated slots
            // must never do outside AcquireElevatedWorkerAsync's explicit
            // per-user login. Surface the failure and let the caller decide
            // whether to retry the whole elevated flow.
            _logger.LogError(ex, "Elevated SAP slot {SlotId} is not logged in for '{Function}' — not auto-reconnecting.",
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
            catch (Exception retryEx) when (IsOutOfMemory(retryEx))
            {
                _logger.LogCritical(retryEx,
                    "Slot {SlotId}: unrecoverable OutOfMemoryException retrying '{Function}' after reconnect — terminating process so Task Scheduler restarts it.",
                    SlotId, request.FunctionName);
                Environment.FailFast(
                    $"SapServer OOM on slot {SlotId} retrying '{request.FunctionName}' — forcing restart via Task Scheduler.",
                    retryEx);
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
    }

    // -------------------------------------------------------------------------
    // SAP COM connection management (must run on _staThread)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Logs this worker's SAP session in. Service workers (and any internal
    /// reconnect/retry path) always call this with no argument, which logs in
    /// as <see cref="_serviceAccount"/> — this worker's own assigned account
    /// (either one entry of <see cref="SapPoolOptions.ServiceAccounts"/>, or
    /// the single shared <see cref="SapPoolOptions.ServiceAccount"/> when that
    /// list is empty — see the constructor). Elevated workers are logged in
    /// ONLY via <paramref name="overrideCreds"/>, supplied by
    /// <see cref="LogonElevatedAsync"/> with one specific user's own SAP
    /// credentials, decrypted just-in-time by the caller (Node) — see
    /// lib/sapCredentials.js in the sql2005-bridge app for why decryption
    /// happens there rather than here.
    /// </summary>
    private void Connect(SapConnectionOptions? overrideCreds = null)
    {
        var creds = overrideCreds ?? _serviceAccount;

        _connectLock.Wait();
        try
        {
            _sapFunctions = new SAPFunctions64.SAPFunctions();

            dynamic conn  = _sapFunctions!.Connection;
            try
            {
                conn.System   = creds.System;
                conn.Client   = creds.Client;
                conn.SystemID = creds.System;
                conn.User     = creds.User;
                conn.Password = creds.Password;
                conn.Language = creds.Language;

                bool loggedOn = conn.Logon(0, true);
                if (!loggedOn)
                    throw new SapConnectionException(SlotId,
                        $"SAP Logon() returned false for user '{creds.User}'. Check credentials.");
            }
            finally
            {
                // The Connection sub-object is only needed for the duration of this login
                // call — see the header comment on ExecuteRfc's RemoveAll() cleanup for why
                // these dynamic COM proxies need an explicit release: without a Windows
                // message pump on this STA thread, GC-finalizer-driven release of an
                // STA-created RCW can never actually be delivered, so leaving this to the
                // GC leaks the native COM reference for good.
                ReleaseCom(conn, "Connection");
            }

            _isConnected  = true;
            _lastActivity = DateTime.UtcNow;
            _logger.LogInformation("SAP slot {SlotId} connected as '{User}'.",
                SlotId, creds.User);
        }
        catch (Exception ex) when (ex is not SapConnectionException)
        {
            throw new SapConnectionException(SlotId, "Failed to establish SAP connection.", ex);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void EnsureConnected()
    {
        if (_isConnected) return;

        if (IsElevated)
            // Elevated slots must never silently log in as the shared service
            // account — that would defeat the whole point of per-user elevated
            // access. Login here only ever happens via the explicit elevated
            // logon path (SapConnectionPool.AcquireElevatedWorkerAsync), which
            // logs in with the caller's own credentials before any work item is
            // queued. Reaching here means a work item was queued on this slot
            // without going through that path — a bug, not a transient outage.
            throw new SapConnectionException(SlotId,
                "Elevated SAP slot is not logged in. Work must not be queued on an elevated slot outside AcquireElevatedWorkerAsync's login.");

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
                try   { conn.Logoff(); }
                finally { ReleaseCom(conn, "Connection"); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during SAP logoff on slot {SlotId}.", SlotId);
        }
        finally
        {
            // _sapFunctions itself was never explicitly released before — it just got
            // dropped here and left for the GC finalizer, which (see ExecuteRfc's cleanup
            // comment) this bare STA thread can't reliably service. Release it explicitly
            // before letting go of the field.
            ReleaseCom(_sapFunctions, "SAPFunctions");
            _sapFunctions = null;
            _isConnected  = false;
        }
    }

    /// <summary>
    /// Explicitly releases a dynamic SAP COM sub-object (a function, table, row, struct,
    /// or connection proxy) instead of leaving it for the GC finalizer. This worker's
    /// <see cref="_staThread"/> is a bare STA thread with no Windows message pump — releasing
    /// an STA-created COM object from another thread (e.g. the .NET finalizer thread) needs
    /// that Release() call marshaled back into this apartment via its message queue, which
    /// never gets serviced here, so GC-driven cleanup effectively never happens. Every dynamic
    /// object obtained from <see cref="_sapFunctions"/> is therefore released synchronously,
    /// on this same STA thread, right after its last use — see the 2026-08-24 SapServer
    /// downtime: unreleased RCWs from routine (not oversized) RFC calls accumulated over the
    /// day until a genuine OutOfMemoryException hit inside COM marshalling.
    /// </summary>
    private void ReleaseCom(object? comObject, string what) => ReleaseCom(comObject, what, _logger, SlotId);

    // Static overload so the two static helpers below (BuildResponse, TryReadReturnMessage —
    // static because they run against a caller-supplied dynamic func, not this worker's own
    // state) can release their own row/table objects without needing an instance. Logging is
    // optional there: those methods already swallow per-row/per-table errors individually
    // (bare catches around field reads), so a release failure is logged when a logger is
    // available and silently ignored otherwise — consistent with that existing behavior.
    private static void ReleaseCom(object? comObject, string what, ILogger? logger, int slotId)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject))
                Marshal.FinalReleaseComObject(comObject);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to release COM object ({What}) on slot {SlotId}.", what, slotId);
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
            // Failing to even create the function object (as opposed to the call itself
            // failing) almost always means the persistent COM session is stale/dead —
            // e.g. the backend closed it, or it's been idle since the last cron run —
            // not that this particular function module is somehow broken. Previously this
            // threw a plain SapExecutionException, which ProcessItem's catch blocks treat
            // as a normal per-call failure with no reconnect: the worker just kept being
            // marked "connected" and failed the exact same way on every subsequent request
            // until the app was restarted. Marking disconnected + throwing
            // SapConnectionException here routes this into ProcessItem's existing
            // reconnect-and-retry-once path instead, the same way a failed Call() already does.
            _isConnected = false;

            throw new SapConnectionException(SlotId,
                $"Could not create RFC function object for '{request.FunctionName}' — SAP session likely stale.",
                ex);
        }

        // Every call above us adds one entry to _sapFunctions.Functions via .Add() with no
        // matching .Remove() — that collection was growing for the entire lifetime of the
        // process. Wrapping the rest of this method in try/finally and calling RemoveAll()
        // on the way out (success OR failure) keeps it from accumulating. Safe to do
        // unconditionally: this worker's STA thread processes the queue strictly one item
        // at a time (see WorkerLoop), so nothing else can be using _sapFunctions concurrently
        // — by the time the next call starts, the collection is already back to empty.
        //
        // RemoveAll() only clears SAP's own bookkeeping list of added functions though — it
        // says nothing about the .NET-side RCWs (func itself, every exports()/struct/table/row
        // object obtained below) that back it. Those need their own explicit release (see
        // ReleaseCom's doc comment for why leaving them to the GC finalizer doesn't work on
        // this bare STA thread) — every dynamic sub-object created in this method is released
        // here, right after its last use.
        try
        {
            // Scalar import parameters — func.exports("KEY").Value pattern (lowercase, indexer call)
            foreach (var (key, value) in request.ImportParameters)
            {
                if (value is not null)
                {
                    // func.exports(name) returns null (rather than throwing) when this SAP
                    // system's real signature for the function has no EXPORTING parameter
                    // by that name — dereferencing it unchecked crashed as a raw, undiagnosable
                    // Microsoft.CSharp.RuntimeBinderException ("Cannot perform runtime binding
                    // on a null reference") instead of a clean, per-call SapExecutionException.
                    // Hit for real via PurchasingHelper.BuildPoGetPriceRequest's guessed
                    // ITEM_CONDITIONS parameter (see that file's UNVERIFIED header comment) —
                    // this turns that same failure mode into a message that actually names the
                    // bad parameter instead of an unhandled 500.
                    dynamic export = func.exports(key);
                    if (export is null)
                        throw new SapExecutionException(request.FunctionName,
                            $"'{request.FunctionName}' has no EXPORTING parameter named '{key}'.",
                            $"func.exports(\"{key}\") returned null — check the real BAPI/RFC signature in SE37 for this SAP system.");
                    try
                    { export.Value = UnwrapJson(value); }
                    catch (Exception ex)
                    { Console.WriteLine($"SCALAR IMPORT ERROR: {key} -> {ex.Message}");
                        throw; }
                    finally
                    { ReleaseCom(export, $"exports({key})"); }
                }
            }


            // Structured import parameters — func.exports("STRUCT").Field(n) pattern (lowercase, indexer call)
            foreach (var (structName, fields) in request.StructImportParameters)
            {
                dynamic sapStruct = func.exports(structName);
                if (sapStruct is null)
                    throw new SapExecutionException(request.FunctionName,
                        $"'{request.FunctionName}' has no EXPORTING structure named '{structName}'.",
                        $"func.exports(\"{structName}\") returned null — check the real BAPI/RFC signature in SE37 for this SAP system.");
                try
                {
                    foreach (var (field, value) in fields)
                    {
                        if (value is not null)
                            try
                            { sapStruct[field] = UnwrapJson(value); }
                            catch (Exception ex)
                            { Console.WriteLine($"STRUCT FIELD ERROR: {structName}.{field} -> {ex.Message}");
                                throw; }
                    }
                }
                finally
                { ReleaseCom(sapStruct, $"exports({structName})"); }
            }


            // Input tables — clear with Freetable() then populate rows
            try
            {
                foreach (var (tableName, rows) in request.InputTables)
                {
                    dynamic table = func.Tables(tableName);
                    if (table is null)
                        throw new SapExecutionException(request.FunctionName,
                            $"'{request.FunctionName}' has no table parameter named '{tableName}'.",
                            $"func.Tables(\"{tableName}\") returned null — check the real BAPI/RFC signature in SE37 for this SAP system.");
                    try
                    {
                        table.Freetable();
                        foreach (var row in rows)
                        {
                            dynamic sapRow = table.Rows.Add();
                            try
                            {
                                foreach (var (col, val) in row)
                                {
                                    if (val is not null)
                                        try
                                        { sapRow[col] = UnwrapJson(val); }
                                        catch (Exception ex)
                                        { Console.WriteLine($"INPUT TABLE ERROR: {tableName}.{col} -> {ex.Message}");
                                            throw; }
                                }
                            }
                            finally
                            { ReleaseCom(sapRow, $"{tableName} input row"); }
                        }
                    }
                    finally
                    { ReleaseCom(table, $"Tables({tableName})"); }
                }

                // Input table Items — clear with Freetable() then populate rows
                foreach (var (tableName, rows) in request.InputTablesItems)
                {
                    dynamic table = func.Tables.Item(tableName);
                    if (table is null)
                        throw new SapExecutionException(request.FunctionName,
                            $"'{request.FunctionName}' has no table parameter named '{tableName}'.",
                            $"func.Tables.Item(\"{tableName}\") returned null — check the real BAPI/RFC signature in SE37 for this SAP system.");
                    try
                    {
                        table.Freetable();
                        foreach (var row in rows)
                        {
                            dynamic sapRow = table.Rows.Add();
                            try
                            {
                                foreach (var (col, val) in row)
                                {
                                    if (val is not null)
                                        try
                                        { sapRow[col] = UnwrapJson(val); }
                                        catch (Exception ex)
                                        { Console.WriteLine($"INPUT TABLE ERROR: {tableName}.{col} -> {ex.Message}");
                                            throw; }
                                }
                            }
                            finally
                            { ReleaseCom(sapRow, $"{tableName} input row"); }
                        }
                    }
                    finally
                    { ReleaseCom(table, $"Tables.Item({tableName})"); }
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
            var typedFunc = (SAPFunctions64.IFunction)func;

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

            return BuildResponse(func, request, _logger);
        }
        finally
        {
            // Best-effort cleanup — must never mask whatever exception is already propagating
            // out of the try block above, so this failure only ever gets logged, not thrown.
            try
            {
                _sapFunctions?.RemoveAll();
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx,
                    "RemoveAll() cleanup failed on slot {SlotId} after '{Function}'.",
                    SlotId, request.FunctionName);
            }

            // func (and typedFunc, which QueryInterface's the same underlying COM identity
            // rather than creating a distinct one) is released last, after RemoveAll() and
            // after BuildResponse/TryReadReturnMessage above are done reading from it.
            ReleaseCom(func, $"function({request.FunctionName})");
        }
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
        dynamic? tables = null;
        dynamic? ret = null;
        try
        {
            // When the call failed because the RFC connection was already closed
            // (RFC_CLOSED etc.), func.tables itself can come back null — indexing
            // into it with .Item("RETURN") then throws a RuntimeBinderException
            // ("Cannot perform runtime binding on a null reference") that says
            // nothing about the actual SAP condition. Guard it explicitly so the
            // log gets a diagnostic that actually explains what happened.
            tables = func.tables;
            if (tables is null)
            {
                diag = "RETURN table unavailable — SAP connection was already closed when the call failed";
                return null;
            }

            ret = tables.Item("RETURN");
            if (ret is null)
            {
                // The COM automation layer returns a null item rather than
                // throwing when "RETURN" isn't a populated table parameter
                // for this function/this failure — iterating ret.Rows on that
                // null previously threw a RuntimeBinderException ("Cannot
                // perform runtime binding on a null reference") that landed in
                // the catch below and produced a diagnostic that explained
                // nothing. Guard it explicitly instead, same as the `tables
                // is null` check above, so the real reason (no RETURN data
                // for this call) is what gets logged.
                diag = "RETURN table not populated for this call (no business-level message available)";
                return null;
            }

            int rowCount = 0;
            var messages = new List<string>();

            foreach (var row in ret.Rows)
            {
                try
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
                finally
                { ReleaseCom(row, "RETURN row", null, 0); }
            }

            diag = $"{rowCount} row(s)";
            return messages.Count > 0 ? string.Join("; ", messages) : null;
        }
        catch (Exception ex)
        {
            diag = $"table access failed: {ex.Message}";
        }
        finally
        {
            ReleaseCom(ret, "RETURN table", null, 0);
            ReleaseCom(tables, "func.tables", null, 0);
        }
        return null;
    }

    private static RfcResponse BuildResponse(dynamic func, RfcRequest request, ILogger logger)
    {
        var parameters = new Dictionary<string, object?>();
        var tables     = new Dictionary<string, List<Dictionary<string, object?>>>();

        // Read scalar export (SAP IMPORTING) parameters — lowercase func.imports(name).Value
        foreach (var paramName in request.ExportParameters)
        {
            dynamic? imp = null;
            try
            {
                imp = func.imports(paramName);
                parameters[paramName] = imp?.Value?.ToString();
            }
            catch { parameters[paramName] = null; }
            finally { ReleaseCom(imp, $"imports({paramName})", null, 0); }
        }

        // Read structure export parameters — positional fields joined with a space
        // Mirrors VB: Set x = MyFunc.imports("MESSG") / x(1) & " " & x(2) & ...
        foreach (var (paramName, fieldCount) in request.StructExportParameters)
        {
            dynamic? s = null;
            try
            {
                s = func.imports(paramName);
                var parts = new List<string>(fieldCount);
                for (int i = 1; i <= fieldCount; i++)
                {
                    try { parts.Add(s(i)?.ToString() ?? ""); }
                    catch { parts.Add(""); }
                }
                parameters[paramName] = string.Join(" ", parts).Trim();
            }
            catch { parameters[paramName] = null; }
            finally { ReleaseCom(s, $"imports({paramName})", null, 0); }
        }

        // Read output tables — lowercase func.tables.Item(name), foreach over rows
        foreach (var (tableName, fields) in request.OutputTables)
        {
            var resultRows = new List<Dictionary<string, object?>>();
            dynamic? table = null;
            try
            {
                table = func.tables.Item(tableName);

                foreach (var sapRow in table.Rows)
                {
                    try
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
                    finally
                    { ReleaseCom(sapRow, $"{tableName} output row", null, 0); }
                }
            }
            catch { /* Table does not exist or has no rows — return empty list */ }
            finally { ReleaseCom(table, $"tables.Item({tableName})", null, 0); }

            tables[tableName] = resultRows;
        }

        // TEMP DIAGNOSTIC — Normanton Nexus warehouse picksheet completion is
        // maintaining ZDELFLAG correctly in SAP, but every run lands as
        // 'Warning' with blank type/message text. Root cause found by reading
        // the RFC signature directly in SE37: ET_MESSAGE is typed ZERRORTEXT
        // (fields LINE/TEXT — no TYPE or MESSAGE field exists at all, so
        // ZdelflagHelpers.BuildMaintainRequest's ReadTable("ET_MESSAGE", "TYPE",
        // "MESSAGE") never matches anything real), and RC is typed SYST (the
        // full system-fields structure, not a plain return code — so
        // ReadParam("RC")'s scalar .Value read is meaningless too). Dumping
        // the real field values here so the actual SUBRC/MSGTY/MSGID/MSGNO/
        // MSGV1-4/LINE/TEXT content can be read off the next live run and the
        // correct fields identified before ZdelflagHelpers.cs is fixed for
        // real. Remove this block once that's done.
        if (request.FunctionName == "Z_MAINT_ZDELFLAG_ZDELPACK")
            DumpZdelflagDiagnostics(func, logger);

        return new RfcResponse { Parameters = parameters, Tables = tables };
    }

    // TEMP DIAGNOSTIC — see BuildResponse's call site above. Reads RC (typed
    // SYST) field-by-field via the string indexer and logs every field name
    // SE37 reported for it, plus every ET_MESSAGE row's LINE/TEXT. Never
    // throws — a field SAP doesn't actually populate for this call just logs
    // as "(unreadable)" rather than aborting the dump. Goes through the
    // passed-in ILogger (Serilog), not Console.WriteLine — this app's
    // Production log sink is Serilog-only (see Program.cs), so raw
    // Console.WriteLine output never reaches the on-disk log at all.
    private static void DumpZdelflagDiagnostics(dynamic func, ILogger logger)
    {
        string[] systFields =
        [
            "INDEX","PAGNO","TABIX","TFILL","TLOPC","TMAXL","TOCCU","TTABC","TSTIS","TTABI",
            "DBCNT","FDPOS","COLNO","LINCT","LINNO","LINSZ","PAGCT","MACOL","MAROW","TLENG",
            "SFOFF","WILLI","LILLI","SUBRC","FLENG","CUCOL","CUROW","LSIND","LISTI","STEPL",
            "TPAGI","WINX1","WINY1","WINX2","WINY2","WINCO","WINRO","WINDI","SROWS","SCOLS",
            "LOOPC","FOLEN","FODEC","TZONE","DAYST","FTYPE","APPLI","FDAYW","CCURS","CCURT",
            "DEBUG","CTYPE","INPUT","LANGU","MODNO","BATCH","BINPT","CALLD","DYNNR","DYNGR",
            "NEWPA","PRI40","RSTRT","WTITL","CPAGE","DBNAM","MANDT","PREFX","FMKEY","PEXPI",
            "PRINI","PRIMM","PRREL","PLAYO","PRBIG","PLAYP","PRNEW","PRLOG","PDEST","PLIST",
            "PAUTH","PRDSN","PNWPA","CALLR","REPI2","RTITL","PRREC","PRTXT","PRABT","LPASS",
            "NRPAG","PAART","PRCOP","BATZS","BSPLD","BREP4","BATZO","BATZD","BATZW","BATZM",
            "CTABL","DBSYS","DCSYS","MACDB","SYSID","OPSYS","PFKEY","SAPRL","TCODE","UCOMM",
            "CFWAE","CHWAE","SPONO","SPONR","WAERS","CDATE","DATUM","SLSET","SUBTY","SUBCS",
            "GROUP","FFILE","UZEIT","DSNAM","TABID","TFDSN","UNAME","LSTAT","ABCDE","MARKY",
            "SFNAM","TNAME","MSGLI","TITLE","ENTRY","LISEL","ULINE","XCODE","CPROG","XPROG",
            "XFORM","LDBPG","TVAR0","TVAR1","TVAR2","TVAR3","TVAR4","TVAR5","TVAR6","TVAR7",
            "TVAR8","TVAR9","MSGID","MSGTY","MSGNO","MSGV1","MSGV2","MSGV3","MSGV4","ONCOM",
            "VLINE","WINSL","STACO","STARO","DATAR","HOST","LOCDB","LOCOP","DATLO","TIMLO",
            "ZONLO",
        ];

        dynamic? rc = null;
        try
        {
            // Two prior attempts both failed on EVERY field uniformly — the
            // string indexer (rc["SUBRC"]) and positional call (rc(24)) alike,
            // including index 1. That uniform failure (not a per-field one) is
            // the same signature this file documents elsewhere for func.exports
            // returning null instead of throwing when SAP has no real parameter
            // by that name/shape (see the EXPORTING-structure-not-found comment
            // above). So before trying to read fields at all, confirm whether
            // `rc` itself is null, and fall back to whatever .Value / GetType
            // reveal — that's strictly more diagnostic than guessing at another
            // field-access pattern blind.
            rc = func.imports("RC");
            if (rc is null)
            {
                logger.LogWarning(
                    "ZDELFLAG DIAGNOSTIC — func.imports(\"RC\") returned null. " +
                    "SAP has no EXPORTING parameter named RC as far as this COM call is concerned " +
                    "(despite SE37 showing one) — field-level access is moot until this returns something.");
            }
            else
            {
                string typeName;
                try { typeName = ((object)rc).GetType().FullName ?? "(unknown)"; }
                catch (Exception ex) { typeName = $"(GetType failed: {ex.Message})"; }

                string rawValue;
                try { rawValue = rc.Value?.ToString() ?? "(null)"; }
                catch (Exception ex) { rawValue = $"(Value read failed: {ex.Message})"; }

                logger.LogWarning(
                    "ZDELFLAG DIAGNOSTIC — RC import parameter: COM type={TypeName}, " +
                    "raw .Value (len {Len})='{Value}'",
                    typeName, rawValue.Length, rawValue);

                var rcValues = new List<string>();
                for (int i = 0; i < systFields.Length; i++)
                {
                    string value;
                    try { value = rc(i + 1)?.ToString() ?? "(null)"; }
                    catch { value = "(unreadable)"; }
                    if (!string.IsNullOrWhiteSpace(value) && value != "0" && value != "(null)")
                        rcValues.Add($"{systFields[i]}={value}");
                }
                logger.LogWarning(
                    "ZDELFLAG DIAGNOSTIC — RC (SYST) non-blank/non-zero positional fields: {Fields}",
                    rcValues.Count > 0 ? string.Join(", ", rcValues) : "(all blank/zero/null/unreadable)");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ZDELFLAG DIAGNOSTIC — failed to read RC structure.");
        }
        finally
        { ReleaseCom(rc, "RC diagnostic dump", null, 0); }

        dynamic? table = null;
        try
        {
            table = func.tables.Item("ET_MESSAGE");
            int rowNum = 0;
            foreach (var sapRow in table.Rows)
            {
                rowNum++;
                try
                {
                    string line, text;
                    try { line = sapRow["LINE"]?.ToString() ?? "(null)"; } catch { line = "(unreadable)"; }
                    try { text = sapRow["TEXT"]?.ToString() ?? "(null)"; } catch { text = "(unreadable)"; }
                    logger.LogWarning(
                        "ZDELFLAG DIAGNOSTIC — ET_MESSAGE row {RowNum}: LINE={Line} TEXT='{Text}'",
                        rowNum, line, text);
                }
                finally
                { ReleaseCom(sapRow, "ET_MESSAGE diagnostic row", null, 0); }
            }
            if (rowNum == 0)
                logger.LogWarning("ZDELFLAG DIAGNOSTIC — ET_MESSAGE table has no rows.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ZDELFLAG DIAGNOSTIC — failed to read ET_MESSAGE table.");
        }
        finally
        { ReleaseCom(table, "ET_MESSAGE diagnostic dump", null, 0); }
    }

    // RFC_INVALID_HANDLE means the session/connection handle itself is no longer valid
    // (backend closed it, GUI scripting session timed out, etc.) — the same class of
    // problem as a communication failure, just reported differently. Treating it as one
    // here means a call that hits it gets reconnected-and-retried immediately within
    // ProcessItem, instead of only being marked for reconnect on the *next* call while
    // this one fails outright — which is exactly the "Could not create RFC function
    // object" / "RFC_INVALID_HANDLE" pattern reported after the daily cron refresh.
    //
    // RFC_CLOSED is the same class of problem again: the backend (or an idle-timeout)
    // has closed the RFC connection out from under us, but the call itself still
    // returns success=false with this exception code rather than throwing on Add()/Call().
    // Before this was added here, a call that hit RFC_CLOSED fell through to the
    // generic branch below, threw a plain SapExecutionException, and failed outright
    // for the caller — see the repeated "RFC 'ZRFC_READ_TABLES' failed — Exception:
    // 'RFC_CLOSED'" / "RFC call ... failed on slot N" log pairs with no reconnect
    // attempt in between. Folding it in here routes it through ProcessItem's existing
    // reconnect-and-retry-once path instead, so a stale connection is transparent to
    // the caller instead of a hard failure.
    private static bool IsCommunicationError(string exceptionCode) =>
        exceptionCode is "RFC_COMMUNICATION_FAILURE"
                      or "RFC_SYSTEM_FAILURE"
                      or "RFC_ABAP_RUNTIME_FAILURE"
                      or "RFC_INVALID_HANDLE"
                      or "RFC_CLOSED";

    // A real OutOfMemoryException can reach here either directly, or wrapped as the
    // InnerException of a SapConnectionException (see ExecuteRfc's func.Add() catch and
    // Connect()'s catch, both of which wrap "ex is not SapConnectionException" — including
    // OOM — into a SapConnectionException so ProcessItem's normal reconnect-and-retry path
    // picks it up). Checking both here means every catch site below can fail fast on a
    // genuinely fatal OOM without duplicating that unwrap logic at each call site.
    private static bool IsOutOfMemory(Exception ex) =>
        ex is OutOfMemoryException || ex.InnerException is OutOfMemoryException;

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
