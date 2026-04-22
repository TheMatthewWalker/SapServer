# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build
dotnet run
dotnet publish -c Release -r win-x86   # production — must be x86, not x64
```

Swagger UI is available at `https://localhost:7200/swagger` in Development mode only.

## Critical Platform Constraints

**Must build x86, not x64.** `SAPFunctionsOCX` is a 32-bit COM component registered under `WOW6432Node`. The project file must keep `<PlatformTarget>x86</PlatformTarget>`. Any attempt to run as x64 will fail at COM activation.

**Deploy via Task Scheduler, not Windows Service.** The SAP OCX requires an interactive user session. Running under Session 0 (Windows Services) causes `AccessViolationException`. Task Scheduler with the "Interactive" logon type keeps the process in the user session.

**Secrets via machine-level environment variables.** `Auth__JwtSecret` and `SapPool__ServiceAccount__Password` are set as Windows machine-level env vars, not in config files. `appsettings.json` has empty string placeholders for these.

## Architecture

SapServer is an ASP.NET Core 10 REST API that wraps SAP GUI COM objects (`SAPFunctionsOCX`) to expose RFC calls as authenticated JSON endpoints.

### STA Thread Pool

SAP COM components require Single-Threaded Apartment threads. The pool (`SapConnectionPool`) maintains N dedicated STA threads, one per `SapStaWorker`. Each worker owns one persistent SAP COM session and a `BlockingCollection<SapWorkItem>` queue. HTTP handlers post async work items to the least-loaded worker and await a `TaskCompletionSource<RfcResponse>`.

### RFC Execution (SapStaWorker)

The COM dispatch pattern that works:
- Field typed as `SAPFunctionsOCX.SAPFunctions` (vtable dispatch, not `dynamic`)
- `dynamic conn = _sapFunctions.Connection` — use `dynamic` for sub-properties
- Import params: `func.exports("KEY").Value = value`
- Input tables: `func.Tables("NAME")` with `.Freetable()` before populating; add rows via `.Rows.Add()`
- Output tables: `func.tables.Item("NAME")`; iterate rows with `foreach` over `.Rows`

**After any failed `func.Call` — business errors AND communication failures — the OCX drops the connection.** Always set `_isConnected = false`. Communication failures (`RFC_COMMUNICATION_FAILURE`, `RFC_SYSTEM_FAILURE`, `RFC_ABAP_RUNTIME_FAILURE`) trigger immediate reconnect + retry inside `ProcessItem`. Business errors propagate to the caller; `EnsureConnected()` reconnects on the next request.

### JsonElement Unwrapping

ASP.NET Core deserializes `object?` parameters as `JsonElement`. These must be unwrapped to CLR primitives (string, int, double, bool, null) before passing to COM or you get `DISP_E_TYPEMISMATCH`. The `UnwrapJson` method in `SapStaWorker` handles this.

### Authentication & Permissions

JWT Bearer tokens are issued by a separate `sql2005-bridge` service (shared HMAC-SHA256 secret). After JWT validation, `PermissionService` checks `dbo.SapDepartmentPermissions` (SQL Server) to confirm the user's departments have access to the requested RFC function. Wildcard `*` grants all functions to a department.

Dev bypass: `Auth:DevBypassAuth=true` skips JWT validation. `Auth:BypassPermissions=true` skips the SQL permission check. Neither should be enabled in production.

### Background Services

`SapSessionMonitor` is a `BackgroundService` that pings idle workers with `RFC_PING` to prevent automatic SAP session timeout. Interval is configurable via `SapPool:HealthCheckIntervalSeconds`.

### Error Handling

`ExceptionHandlingMiddleware` converts all exceptions to a consistent JSON envelope:
```json
{ "success": false, "error": { "code": "ERROR_CODE", "message": "..." } }
```

Custom exceptions in `Exceptions/SapExceptions.cs` map to specific HTTP status codes (403 for permission denied, 503 for SAP unavailable/pool exhausted, 422 for RFC business errors).

### Domain Helpers

`Helpers/` contains typed RFC wrappers (`WarehouseHelpers`, `BdcBuilder`, `RfcRequestBuilder`, `ReturnTableHelper`) that hide low-level COM details. New SAP integrations should add helpers here rather than putting COM logic in controllers.

## Key Configuration Keys

| Key | Purpose |
|-----|---------|
| `SapPool:PoolSize` | Worker count (0 = ProcessorCount) |
| `SapPool:ServiceAccount.*` | SAP system, client, user, password, language |
| `Auth:JwtSecret` | Shared HMAC-SHA256 secret with sql2005-bridge |
| `Auth:SqlConnectionString` | DB for permission tables |
| `Auth:DevBypassAuth` | Skip JWT (dev only) |
| `AllowedOrigins` | CORS origins for frontend |
