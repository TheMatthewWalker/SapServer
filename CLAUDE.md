# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build, Publish & Run

```bash
dotnet build
dotnet run
```

Swagger UI is available at `https://localhost:7200/swagger` in Development mode only (see `appsettings.example.json`'s `Urls`).

Production is published **self-contained for win-x64** and deployed via a Task Scheduler task, not `dotnet run`:

```powershell
dotnet publish . -c Release -r win-x64 --self-contained true -o publish
```

`scripts/deploy.ps1` does this end-to-end (stop task → publish → start task → tail today's log). Other scripts: `install.ps1` (registers the `SapServer` scheduled task + sets machine env vars, first-time setup), `start.ps1` / `stop.ps1` / `status.ps1` / `uninstall.ps1`, `watch-log.ps1` / `install-log-watcher.ps1`.

## Testing

```bash
dotnet test
```

`SapServer.Tests` (xUnit + Moq + `Microsoft.AspNetCore.Mvc.Testing`), referenced from `SapServer.sln`. Two small, permanent changes to `SapServer.csproj`/`Program.cs` make this possible — don't remove them:
- `public partial class Program { }` appended to the bottom of `Program.cs` — required for `WebApplicationFactory<Program>` to work with top-level statements.
- `[assembly: InternalsVisibleTo("SapServer.Tests")]` in `SapServer.csproj` — lets tests reach the `internal` `Helpers/*` classes directly.

- `SapServer.Tests/Helpers/` — pure `BuildXxxRequest`/`ParseXxxRows` functions (no COM, no DB) — highest-value, easiest tests in the codebase. Every file in `Helpers/` now has a dedicated test class: `SapDelimitedParser`, `SapPad`, `ProductionHelpers`, `WarehouseHelpers`, `PurchasingHelper`, `QualityHelpers`, `BdcBuilder`, `RfcRowHelpers`, `CostingHelper`, `CustomsHelpers`, `FunctionHelper`, `GoodsReceiptHelper`, `LogisticsHelpers`, `PicksheetHelpers`, `ZdelflagHelpers`, `ConsignmentHelpers`, `PackagingHelpers`, `PerformanceHelpers`. Two real bugs were found this way and documented via a test + comment rather than silently fixed (a product call, not a testing one):
  - `SapPad`'s XML doc claims mixed/alpha strings get space-padded, but the implementation's non-digit branch just returns the value unchanged — every padded WHERE-clause material number relies on the actual (unpadded) behavior, so don't "fix" it without checking every caller first.
  - `RfcRowHelpers.GetDecimal` (and the private `Dec()` helper duplicated inside a few of `PerformanceHelpers`/`CostingHelper`) unconditionally strips every `.` before converting `,` to `.`, assuming SAP always sends European-grouped decimals (`"1.234,56"`). A plain invariant-culture value with no thousands separator (`"1234.56"`) has its decimal point stripped as a false grouping separator and parses as `123456` — silently 100x too large. `RfcRowHelpersTests.cs` documents this precisely.
  - `PerformanceHelpers.cs` (1465 lines, the largest and most business-logic-dense helper) is covered for everything reachable via its `internal` surface (`NormaliseMaterial`, `BookValueFactor`, currency conversion, `ValidateValuationClassChanges`'s pre-flight checks ahead of an irreversible MM02 valuation-class change) — `ComputeTurnsRows` and its `private` helpers (`TurnoverCategoryFor`, `BuildWarning`, `StockTurnsFor`) aren't reachable even via `InternalsVisibleTo` (that only exposes `internal`, not `private`) and would need a large multi-table fixture to exercise meaningfully through `ComputeTurnsRows` itself — a deliberately deferred gap, not an oversight.
  - `test/helpers/mockPool.js`'s Normanton-Nexus equivalent gained the same `sql.Transaction` support this file's `consignmentsql.js`/`shipmentmain.js` tests needed — see that repo's `CLAUDE.md` if porting a similar pattern here.
- `SapServer.Tests/Middleware/` — `ExceptionHandlingMiddlewareTests` covers every exception → status/code mapping directly (constructs the middleware with a throwing `RequestDelegate`, no host needed).
- `SapServer.Tests/Controllers/` — two patterns, both valid, pick whichever fits:
  - `WebApplicationFactory<Program>` (`RfcControllerTests`) — full HTTP pipeline including real JWT auth. Auth config (`Auth:JwtSecret` etc.) is injected via **environment variables** in `SapServerTestFactory`'s static constructor, not `ConfigureAppConfiguration` — `Program.cs` reads `AuthOptions` eagerly, before `builder.Build()`, which is too early for the usual `WebApplicationFactory` config-injection hook to reach; env vars are loaded by `WebApplicationBuilder.CreateBuilder(args)` itself, before that point.
  - Direct instantiation + Moq (`ControllerTestHelpers.SetUser`, every other controller test) — faster to write per controller, skips the real auth pipeline (already proven separately via the pattern above). For `AcquireWorker()`/`AcquireElevatedWorkerAsync()`-based endpoints (stock adjustments, PO/packaging creation), leave the pool mock's return value unconfigured (Moq defaults to `null`) rather than constructing a real `SapWorkerHandle` — the handle is only ever passed through opaquely to other mocked pool calls, never dereferenced by controller code, and constructing a real one means starting an actual `SapStaWorker` STA thread.
  - Both `ISapConnectionPool` and `IPermissionService` are always mocked — never let the real `SapConnectionPool` construct in a test, since that spins up real STA threads against a real SAP system. All 12 controllers have coverage; the elevated-worker "always release in `finally`" guarantee (`PurchasingController`, `PackagingController`) and the bin-existence/quality-block fail-fast guards (`WarehouseController`, `QualityController`) are the highest-value cases covered.
- `SapServer.Tests/Integration/` — `PermissionServiceIntegrationTests` runs the real `PermissionService` (it opens its own `SqlConnection` directly, not through an injectable seam) against a **real staging SQL Server** — the only layer that can catch the SQL Server version migration's real behavior differences. Env-gated on `TEST_SQL_SERVER`/`TEST_SQL_DATABASE`/`TEST_SQL_USER`/`TEST_SQL_PASSWORD` (same convention as Normanton-Nexus's `test/helpers/stagingDb.js`) — skips, not fails, when unset. See `SapServer.Tests/Infrastructure/StagingDb.cs`.

**No CI runner (or this dev machine) has a real SAP GUI install**, so `libs/Interop.SAPFunctions64.dll` is never present outside a real dev/production machine that's generated it. `SapServer.csproj` falls back to `interop-stubs/SAPFunctions64.DevStub` — a tiny stand-in assembly satisfying just the type shape `SapStaWorker.cs` needs to compile — whenever that file is missing; see that project's `SAPFunctions64.cs` for the full rationale. Real SAP RFC/BAPI execution is explicitly out of scope for automated tests either way.

## Critical Platform Constraints

**Build x64, not x86.** The project was migrated from a 32-bit `SAPFunctionsOCX` COM component to the 64-bit `SAPFunctions64` interop assembly (`libs/Interop.SAPFunctions64.dll`) — `SapServer.csproj` sets `<PlatformTarget>x64</PlatformTarget>` and every publish path (`FolderProfile.pubxml`, `scripts/deploy.ps1`) targets `win-x64`. If you see older guidance anywhere claiming this must build x86, it's describing a pre-migration state that no longer applies.

**Deploy via Task Scheduler, not Windows Service.** The SAP OCX requires an interactive user session. Running under Session 0 (Windows Services) causes `AccessViolationException`. `install.ps1` registers an `AtLogon`-triggered Task Scheduler task instead, which keeps the process in the interactive user session.

**`Auth:JwtSecret` via a machine-level environment variable, SAP credentials directly in the config file.** `Auth__JwtSecret` is set as a Windows machine env var by `install.ps1` (shared with sql2005-bridge, so not something edited per-deployment). `SapPool:ServiceAccount`/`ServiceAccounts` (SAP service account credentials — one or several, see the STA Thread Pool section below) are kept directly in `appsettings.Production.json` instead — that file is `.gitignore`'d the same as `appsettings.json`, and a plain config file is far easier to maintain than machine env vars once there's more than one account. `appsettings.example.json` has placeholder values for both — see it for the full shape.

## Architecture

SapServer is an ASP.NET Core 10 REST API that wraps the SAP GUI `SAPFunctions64` COM component to expose RFC/BAPI calls as authenticated JSON endpoints, replacing an older per-desktop WinUI 3 app (`sap-gui-async`).

### STA Thread Pool — service workers + elevated workers

`SapConnectionPool` (singleton, `Services/SapConnectionPool.cs`) maintains **two separate groups** of `SapStaWorker`, each owning one dedicated STA thread and one SAP COM session:

- **Service workers** (`SapPool:ServiceWorkerCount`, default 4) — always logged in, for the lifetime of the process. Logs in as one entry of `SapPool:ServiceAccounts` (worker *i* → `ServiceAccounts[i % Count]`) when that array is populated, otherwise every worker shares the single `SapPool:ServiceAccount` (the original behavior) — see `SapStaWorker`'s `serviceAccount` ctor param. `ExecuteAsync`/`AcquireWorker` (i.e. every ordinary RFC call from `RfcController` and the domain controllers below) only ever route to this group via least-loaded selection with round-robin tiebreaking (`SelectWorker()`) — confirmed against real per-call slot/thread logging (`SapConnectionPool.ExecuteAsync` logs the routed-to slot at enqueue time; `SapStaWorker.ProcessItem` logs its own managed thread id at start/finish) after a prior version of `SelectWorker()` turned out to be a plain first-minimum-wins scan with no actual tiebreak, silently serializing bursts onto slot 0.
- **Elevated workers** (`SapPool:ElevatedWorkerCount`, default 2) — created **logged out**. `AcquireElevatedWorkerAsync(creds)` claims one via a semaphore (queues up to `ElevatedAcquireTimeoutSeconds`, then throws `PoolExhaustedException`), logs it in with one specific user's own SAP credentials for the duration of a single elevated request (e.g. PO creation in `PurchasingController`/`PackagingController`'s `-elevated` endpoints), then `ReleaseElevatedWorkerAsync` **must** be called in a `finally` block to log it back out and return it to the pool. This is what stops one user's elevated session ever being reused for another user — it is not just a bigger version of the service pool.

Total STA threads started at startup = `ServiceWorkerCount + ElevatedWorkerCount`; size relative to your SAP system's concurrent-user license count, not CPU count.

### RFC Execution (`SapStaWorker.ExecuteRfc`)

The COM dispatch pattern that works:
- Field typed as `SAPFunctions64.SAPFunctions` (vtable dispatch, not `dynamic`) — `dynamic conn = _sapFunctions.Connection` for sub-properties
- Import params: `func.exports("KEY").Value = value`; structures: `func.exports("STRUCT")[field] = value`
- Input tables: `func.Tables("NAME")` (or `.Tables.Item("NAME")` for the `*Items` variant) with `.Freetable()` before populating; add rows via `.Rows.Add()`
- Output tables: `func.tables.Item("NAME")`; iterate rows with `foreach` over `.Rows`
- The call itself is `func.Call` — a **property get** on the dynamic COM object, not a method call (`typedFunc.Call()` via the typed `IFunction` interface was tried and left commented out; `func.Call` is what actually works)
- Every `func.Add(functionName)` call leaks an entry into `_sapFunctions.Functions` with no automatic cleanup — `ExecuteRfc` calls `_sapFunctions.RemoveAll()` in a `finally` block on every call (safe because one STA thread only ever processes one item at a time)

**After any failed call, the OCX drops the connection** — `_isConnected` is always set `false`, whether the failure was a business error or a communication failure. Failing to even create the function object (`func.Add(...)` throwing) is treated the same way, since it almost always means the persistent session is already stale. Communication-class errors trigger immediate reconnect + retry inside `ProcessItem`: `RFC_COMMUNICATION_FAILURE`, `RFC_SYSTEM_FAILURE`, `RFC_ABAP_RUNTIME_FAILURE`, `RFC_INVALID_HANDLE`, `RFC_CLOSED` (the last two were added later — a call that hit them used to fail outright with no reconnect attempt). Plain business errors (a real `SapExecutionException`) propagate straight to the caller instead. **Elevated workers never auto-reconnect** — a connection failure on one just fails the request, since reconnecting there would mean silently logging in as the shared service account, defeating the whole point.

### JsonElement Unwrapping

ASP.NET Core deserializes `object?` parameters as `JsonElement`. `SapStaWorker.UnwrapJson` converts these to CLR primitives (string, long/double, bool, null) before passing to COM — otherwise you get `DISP_E_TYPEMISMATCH`. `decimal` is coerced to `double` first (COM VARIANT has no decimal support).

### Controllers — one generic + many domain-specific

`RfcController` (`api/rfc/execute`, `api/rfc/status`) is the original generic surface: caller supplies the raw RFC function name, import parameters, and which export parameters/output tables to read back. It still exists, but most functionality now lives in **typed domain controllers**, all deriving from `SapControllerBase` (which supplies `_pool`, `_permissions`, `_logger`, `GetUserId()`, and `CheckPermissionAsync(userId, functionCode, ct)`):

| Controller | Route | Covers |
|---|---|---|
| `ProductionController` | `api/production` | Backflush (ZF40N), drumming backflush + `Z_ZPRODBATCH_MAINT`, BOM lookup, scrap post/reverse, cost collector/profit centre lookups, order-text (`RFC_READ_TEXT`) |
| `WarehouseController` | `api/warehouse` | Stock/bin queries, transfer orders (LT01/LT04), stock adjustments (BAPI_GOODSMVT_CREATE), picksheet staging/unstaging, ZDELFLAG maintenance |
| `PurchasingController` | `api/purchasing` | PO creation, goods receipt post/reverse, combined create-PO-and-receipt, elevated PO creation |
| `PackagingController` | `api/packaging` | Material master lookups (MARA/BOM/customers), mass update, packaging creation (standard + elevated) |
| `QualityController` | `api/quality` | Quality notification display, block/unblock |
| `CostingController` | `api/costing` | Cost sheet, period balance, profit-center, freight posting (single + batch) |
| `CustomsController` | `api/customs` | LIPS/LIKP/VBFA/MARC/KNA1 lookups for customs declarations |
| `LogisticsController` | `api/logistics` | Open picksheets |
| `ConsignmentController` | `api/consignment` | Vendor consignment goods-receipt + stock lookups |
| `PerformanceController` | `api/performance` | Stock, agreements, VBFA order-link, invoicing, OTIF, MM Turns/Valuation Class (+ change-valuation-class) |
| `FunctionController` | `api/function` | Generic RFC function parameter introspection |
| `RfcController` | `api/rfc` | Generic execute/status (original, still used directly by some callers) |

Domain controllers call typed `Helpers/` builders to construct an `RfcRequest`, run it via `_pool.ExecuteAsync(...)`, then parse the typed response — controllers themselves contain no raw COM/dispatch code.

**Permission checks are keyed by an app-level function code, not necessarily the literal SAP RFC name** — e.g. `ProductionController` checks `ProductionHelpers.FnCreate` for several different endpoints that all call different underlying RFCs. Only `RfcController.Execute` checks the raw `request.FunctionName` directly against `SapDepartmentPermissions`, since that's the only place where the caller picks the RFC name itself.

### Authentication & Permissions

JWT Bearer tokens are issued by a separate `sql2005-bridge`/Normanton-Nexus service (shared HMAC-SHA256 secret, claim `userId`). After JWT validation, `PermissionService.CanExecuteAsync(userId, functionCode)` checks `dbo.SapDepartmentPermissions` (SQL Server) — a user passes if any of their `PortalUserDepartments` departments has a matching row for that function code, or a `*` wildcard row. Results are cached per-`(userId, functionCode)` for `Auth:PermissionCacheSeconds` (default 60) and the lookup **fails closed** — any SQL error denies access rather than allowing it.

Dev bypass: `Auth:DevBypassAuth=true` (Development environment only) swaps in `DevAuthHandler` + `NullPermissionService`, auto-authenticating every request. `Auth:BypassPermissions=true` still requires a valid JWT but skips the SQL permission check — use only until `SapDepartmentPermissions` is provisioned. Neither should be enabled in production.

### Background Services

`SapSessionMonitor` is a `BackgroundService` that pings idle **service** workers with `RFC_PING` every `HealthCheckIntervalSeconds` to prevent SAP's own idle-session timeout (elevated workers are deliberately excluded — logged-out-and-idle is their normal resting state). A disconnected slot's warning is throttled to at most once per `DisconnectedWarningRepeatSeconds`, so a slot with no traffic doesn't spam the log every health-check tick.

### Error Handling

`ExceptionHandlingMiddleware` converts all exceptions to a consistent JSON envelope:
```json
{ "success": false, "error": { "code": "ERROR_CODE", "message": "..." } }
```

| Exception | HTTP | Code |
|---|---|---|
| `SapPermissionException` | 403 | `FORBIDDEN` |
| `SapConnectionException` | 503 | `SAP_UNAVAILABLE` |
| `SapExecutionException` | 422 | `RFC_ERROR` |
| `PoolExhaustedException` | 503 | `POOL_EXHAUSTED` |
| `OperationCanceledException` | 499 | `REQUEST_CANCELLED` |
| `UnauthorizedAccessException` | 401 | `UNAUTHORIZED` |
| (anything else) | 500 | `INTERNAL_ERROR` |

### Domain Helpers

`Helpers/` holds typed RFC/BAPI request builders and response parsers, one broadly per business area: `ProductionHelpers`, `PurchasingHelper`, `WarehouseHelpers`, `QualityHelpers`, `PackagingHelpers`, `CostingHelper`, `CustomsHelpers`, `LogisticsHelpers`, `ConsignmentHelpers`, `PerformanceHelpers`, `FunctionHelper`, plus cross-cutting ones: `BdcBuilder` (BDC/session-based posting helpers), `RfcRequestBuilder`/`RfcRowHelpers` (generic request/table-row construction), `ReturnTableHelper` (parsing SAP's RETURN/BAPIRETURN messages), `GoodsReceiptHelper`, `StockAdjustmentHelper`, `ZdelflagHelpers`, `SapDelimitedParser` (WA/delimited raw-table parsing for `ZRFC_READ_TABLES`-style reads), `SapPad` (material-number/field zero-padding), `WhereClauseBuilder`, `CommitHelpers`. New SAP integrations should add a helper here rather than putting COM logic in a controller.

## Key Configuration Keys

| Key | Purpose |
|-----|---------|
| `SapPool:ServiceWorkerCount` / `ElevatedWorkerCount` | Worker counts per group (default 4 + 2) |
| `SapPool:ServiceAccounts` | Optional array of per-worker service accounts (worker *i* → `ServiceAccounts[i % Count]`); falls back to the single `ServiceAccount` for every worker when empty |
| `SapPool:ElevatedAcquireTimeoutSeconds` | Max wait for a free elevated slot before `PoolExhaustedException` (default 30) |
| `SapPool:MaxQueueDepth` | Per-worker queued-item cap before rejecting new work (default 50) |
| `SapPool:IdleTimeoutSeconds` / `HealthCheckIntervalSeconds` | Keep-alive ping threshold / monitor tick interval |
| `SapPool:DisconnectedWarningRepeatSeconds` | Throttle for repeated "still disconnected" log warnings |
| `SapPool:ServiceAccount.*` | SAP system, client, user, password, language for service workers |
| `Auth:JwtSecret` / `JwtIssuer` / `JwtAudience` | Shared HMAC-SHA256 secret + issuer/audience with sql2005-bridge/Normanton-Nexus |
| `Auth:SqlConnectionString` | DB for permission tables (`PortalUsers`, `PortalUserDepartments`, `SapDepartmentPermissions`) |
| `Auth:PermissionCacheSeconds` | Permission-check cache TTL (default 60) |
| `Auth:DevBypassAuth` / `BypassPermissions` | Dev-only auth/permission skips — never enable in production |
| `AllowedOrigins` | CORS origins for the frontend |
