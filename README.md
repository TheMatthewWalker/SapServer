# SapServer

.NET Framework 4.8 ASP.NET Web API 2 (OWIN-hosted-in-IIS) service that exposes SAP RFC function calls over HTTPS to the enterprise frontend hosted by **sql2005-bridge**, via the **SAP .NET Connector (NCo)**. It replaces the WinUI 3 desktop application (`sap-gui-async`) with a multi-user, server-side architecture — and replaces an earlier ASP.NET Core 10 + SAPFunctions64-COM version of this same service. See `CLAUDE.md`'s "Migration Notes" section for why, and for what remains unverified against a real IIS/SAP environment.

---

## Why this project exists

The desktop application required SAP GUI to be installed on every workstation. Each user managed their own SAP session, leading to credential sprawl and no central audit trail.

SapServer centralises SAP access:

- A **single service account** is kept permanently connected per worker thread — no per-request login latency.
- A **configurable pool of worker threads** handles concurrent RFC calls safely.
- The existing enterprise website can trigger SAP operations directly via HTTP, authenticated through the same user accounts already managed by sql2005-bridge.

---

## Architecture

```
sql2005-bridge frontend  (browser)
        │
        │  HTTPS + JWT Bearer token
        ▼
┌───────────────────────────────────────┐
│  SapServer  (.NET Framework 4.8,      │
│              OWIN hosted in IIS)      │
│                                       │
│  RfcController                        │
│    │  permission check (SQL Server)   │
│    │  → ISapConnectionPool            │
│    │      │                           │
│    │      │  least-loaded worker      │
│    │      ▼                           │
│  ┌──────────────────────────────┐     │
│  │  NcoConnectionPool           │     │
│  │  ┌────────┐ ┌────────┐ ...  │     │
│  │  │Worker 0│ │Worker 1│      │     │
│  │  │thread  │ │thread  │      │     │
│  │  │SAP NCo │ │SAP NCo │      │     │
│  │  └────────┘ └────────┘      │     │
│  └──────────────────────────────┘     │
│                                       │
│  SapSessionMonitor (Timer)            │
│    → pings idle workers               │
│    → logs disconnected slots          │
└───────────────────────────────────────┘
        │
        │  SAP NCo (SAP.Middleware.Connector)
        ▼
   SAP Application Server
```

### Worker Pool

Each `NcoWorker` owns one dedicated background thread and one SAP NCo connection pinned to it via `RfcSessionManager.BeginContext` — needed so a create-BAPI + `BAPI_TRANSACTION_COMMIT`/`ROLLBACK` sequence lands on the same SAP session's LUW, a SAP-level requirement independent of transport. HTTP request handlers post `RfcRequest` work items (containing a `TaskCompletionSource`) to the worker's `BlockingCollection`; the worker thread executes the RFC call synchronously via NCo and sets the result on the TCS, which resumes the awaiting HTTP handler on the thread pool.

### Pool sizing

The pool is split into two groups of workers, each configured in `appsettings.json` under `SapNco`:

| Group | Setting | Behaviour |
|-------|---------|-----------|
| Service | `SapNco:ServiceWorkerCount` (default `4`) | Always connected. Handles every ordinary RFC call. |
| Elevated | `SapNco:ElevatedWorkerCount` (default `2`) | Created **unconnected**. Only connected on demand with one specific user's own SAP credentials, for the duration of one elevated request (e.g. PO creation), then disconnected. Prevents one user's elevated session ever being reused for another user. |

`SapNco:ElevatedAcquireTimeoutSeconds` (default `30`) caps how long a caller waits for a free elevated slot if all of them are busy.

Each worker holds its own thread; a service worker also holds one pinned SAP NCo connection for the app's lifetime, while an elevated worker only holds one for the duration of a single elevated request. Set `ServiceWorkerCount`/`ElevatedWorkerCount` based on your SAP system's concurrent user licence count, not just CPU count — total worker threads started is `ServiceWorkerCount + ElevatedWorkerCount`.

**Service account(s):** by default every service worker connects as the single `SapNco:ServiceAccount`. To instead give each service worker its own distinct SAP login — e.g. so concurrent postings aren't all attributed to one shared account in SAP's own change logs, or to sidestep whatever behaviour your SAP system has for the same account logging in multiple times at once — populate `SapNco:ServiceAccounts` as an array. Worker *i* connects as `ServiceAccounts[i % ServiceAccounts.Count]`, wrapping round-robin if there are fewer accounts than workers (e.g. 2 accounts covering 4 workers). `ServiceAccount` (singular) still has to be filled in even when `ServiceAccounts` is used — `PurchasingController`/`PackagingController`'s elevated endpoints read its AppServerHost/Client/SystemNumber/Language as the shared connection profile regardless. Leave `ServiceAccounts` empty/unset to keep the original single-account behaviour.

### Session keep-alive

SAP application servers can drop an idle pooled RFC connection after a period of inactivity, much as SAP GUI did for COM sessions. `SapSessionMonitor` runs every `HealthCheckIntervalSeconds` and sends an `RFC_PING` to any worker whose `LastActivity` exceeds `IdleTimeoutSeconds`. This resets the SAP idle timer without any user-visible delay.

If a worker is found disconnected, it logs a warning and defers reconnection to the next real request via `EnsureConnected()` — the monitor itself does not block. That warning repeats at most once per `DisconnectedWarningRepeatSeconds` while the same slot stays down, instead of on every health-check tick, so a slot with no traffic for a while doesn't bury real errors in the log.

---

## Authentication & Authorisation

### JWT issued by sql2005-bridge

sql2005-bridge is the authority for user accounts. SapServer does **not** have its own user store.

**How it works:**

1. The user logs into sql2005-bridge normally (username + password → session cookie).
2. The frontend calls a new `/api/sap/token` endpoint on sql2005-bridge to obtain a short-lived JWT.
3. The frontend attaches the JWT as a `Bearer` token on every SapServer request.
4. SapServer validates the JWT signature using a shared HMAC-SHA256 secret — no network round-trip.
5. SapServer reads the `userId` claim and checks `dbo.SapDepartmentPermissions` to confirm the user's department has access to the requested RFC function.

**Required changes to sql2005-bridge** (see the section below for the code snippet):

- Add `npm install jsonwebtoken`
- Add a `/api/sap/token` POST endpoint (requires an active session)

### Permission model

Rather than a per-user permission table, SapServer reuses the existing department system:

```
dbo.PortalUserDepartments   →  dbo.SapDepartmentPermissions
(department assignment)         (department ↔ RFC function mapping)
```

To grant the `logistics` department permission to call `L_TO_CREATE_SINGLE`, insert one row:

```sql
INSERT INTO dbo.SapDepartmentPermissions (Department, FunctionName, GrantedBy)
VALUES ('logistics', 'L_TO_CREATE_SINGLE', 'admin');
```

Permissions are cached for `PermissionCacheSeconds` (default 60) to reduce SQL load. Use `*` as `FunctionName` to grant a department access to all functions.

---

## Setup

### Prerequisites

| Requirement | Notes |
|-------------|-------|
| .NET Framework 4.8 | Ships with modern Windows Server; install the Developer Pack for building |
| .NET SDK (10.x works fine as a build tool) | SapServer.csproj is SDK-style, so the modern `dotnet` CLI can build a net48 target |
| SAP NCo (`sapnco.dll`/`sapnco_utils.dll` + native binaries) | Downloaded from the SAP Support Portal under a licensed S-user — see below |
| IIS with ASP.NET 4.8 | `Install-WindowsFeature Web-Server, Web-Asp-Net45` |
| SQL Server   | Same instance used by sql2005-bridge |
| Windows OS   | Required to actually run/host this app (IIS, SAP NCo's native binaries) — the SDK-style project *compiles* on non-Windows too, which is how this rebuild was validated in a Linux sandbox with no way to run it |

### 1. Place the SAP NCo assemblies

```
SapServer/
└── libs/
    ├── sapnco.dll                  ← from the SAP NCo download
    ├── sapnco_utils.dll
    └── sapnco-native/
        ├── librfc32.dll (or librfcum.dll)
        ├── icudt*.dll / icuin*.dll / icuuc*.dll
        └── (matching VC++ runtime, per the NCo download's own instructions)
```

Pull the whole NCo download as one matched-version set rather than mixing DLLs across releases — SAP version/strong-name-checks the managed assemblies against the native ones at load time. Without real DLLs present, `SapServer.csproj` falls back to `interop-stubs/SapNco.DevStub` — a compile-only stand-in for the `SAP.Middleware.Connector` API surface, written from memory and not verified against the real assemblies (see that project for the full caveat).

### 2. Create the permissions table

Run `sql/SapPermissions_setup.sql` against the `kongsberg` (or equivalent) database:

```bash
sqlcmd -S GATEWAYHO -d kongsberg -i sql/SapPermissions_setup.sql
```

### 3. Configure appsettings.json

Copy `appsettings.example.json` → `appsettings.json` and fill in all values:

```json
{
  "SapNco": {
    "ServiceWorkerCount": 4,
    "ElevatedWorkerCount": 2,
    "ServiceAccount": {
      "AppServerHost": "your-sap-app-server",
      "SystemNumber":  "00",
      "Client":        "100",
      "User":          "SVC_SAPAPI",
      "Password":      "...",
      "Language":      "EN"
    },
    "ServiceAccounts": [
      { "AppServerHost": "your-sap-app-server", "SystemNumber": "00", "Client": "100", "User": "SVC_SAPAPI1", "Password": "...", "Language": "EN" },
      { "AppServerHost": "your-sap-app-server", "SystemNumber": "00", "Client": "100", "User": "SVC_SAPAPI2", "Password": "...", "Language": "EN" },
      { "AppServerHost": "your-sap-app-server", "SystemNumber": "00", "Client": "100", "User": "SVC_SAPAPI3", "Password": "...", "Language": "EN" },
      { "AppServerHost": "your-sap-app-server", "SystemNumber": "00", "Client": "100", "User": "SVC_SAPAPI4", "Password": "...", "Language": "EN" }
    ]
  },
  "Auth": {
    "JwtSecret":           "min-32-char-random-secret-shared-with-sql2005-bridge",
    "JwtIssuer":           "normanton-nexus",
    "JwtAudience":         "sap-server",
    "SqlConnectionString": "Server=GATEWAYHO;Database=kongsberg;..."
  },
  "AllowedOrigins": ["https://yourserver:4000"]
}
```

`ServiceAccounts` is optional — omit it (and just fill in `ServiceAccount`) to keep every service worker connected as one shared account, the original behaviour. When present, worker *i* connects as `ServiceAccounts[i % ServiceAccounts.Count]`.

> **Security:** Keep `appsettings.json`/`appsettings.Production.json` out of source control (both already `.gitignore`'d) — that's what protects the secrets kept directly in the config file: `SapNco:ServiceAccount`/`ServiceAccounts` (SAP credentials) and `Auth:JwtSecret`. Neither is set via a machine environment variable — a plain config file is much easier to maintain than env vars once there's more than one value to keep track of, and `install.ps1` no longer prompts for either.

### 4. Add JWT issuance to sql2005-bridge

Install the dependency:
```bash
cd sql2005-bridge && npm install jsonwebtoken
```

Add this endpoint to `server.js` (requires an active session):
```js
import jwt from 'jsonwebtoken';

app.post('/api/sap/token', requireLogin, (req, res) => {
  const payload = {
    userId:      req.session.user.userID,
    username:    req.session.user.username,
    role:        req.session.user.role,
    departments: req.session.user.departments,
  };
  const token = jwt.sign(payload, process.env.SAP_JWT_SECRET, {
    expiresIn: '8h',
    issuer:    'normanton-nexus',
    audience:  'sap-server',
  });
  res.json({ token });
});
```

Set `SAP_JWT_SECRET` as an environment variable (same value as `Auth:JwtSecret` in SapServer).

### 5. Build and run

```bash
cd SapServer
dotnet build
```

There is no `dotnet run` self-host — this is an IIS-hosted OWIN application, not Kestrel. Run it locally via IIS Express or a local IIS site pointed at the build output; see `scripts/install.ps1`/`deploy.ps1` for the production IIS setup. Swagger UI (Swashbuckle for Web API 2) is available at `/swagger` once wired up on a real IIS instance.

---

## API Reference

### POST /api/rfc/execute

Execute an RFC function call. Requires a valid JWT.

**Request body:**

```json
{
  "functionName": "L_TO_CREATE_SINGLE",
  "importParameters": {
    "I_LGNUM": "001",
    "I_WERKS": "3012",
    "I_MATNR": "000000000012345678",
    "I_ANFME": "10",
    "I_VLPLA": "BIN-001",
    "I_NLPLA": "BIN-002"
  },
  "inputTables":    {},
  "exportParameters": ["E_TANUM"],
  "outputTables": {
    "RETURN": ["TYPE", "MESSAGE", "NUMBER"]
  }
}
```

**Response (200):**

```json
{
  "success": true,
  "data": {
    "parameters": { "E_TANUM": "0000001234" },
    "tables": {
      "RETURN": [
        { "TYPE": "S", "MESSAGE": "Transfer order 1234 created", "NUMBER": "001" }
      ]
    }
  }
}
```

**Notes on `outputTables`:**
- List every table name you want to read back, mapped to the field names you need.
- For `ZRFC_READ_TABLES` — pass an empty field list `[]` to receive the raw `WA` work-area strings (delimiter-separated), matching the existing desktop app behaviour.

### GET /api/rfc/status

Returns the current health of all pool workers. Requires `admin` or `superadmin` role.

```json
{
  "success": true,
  "data": [
    { "slotId": 0, "isConnected": true,  "queueDepth": 0, "lastActivity": "2025-03-16T10:42:00Z" },
    { "slotId": 1, "isConnected": true,  "queueDepth": 1, "lastActivity": "2025-03-16T10:42:15Z" }
  ]
}
```

---

## Error responses

All errors use the same envelope:

```json
{ "success": false, "error": { "code": "RFC_ERROR", "message": "..." } }
```

| HTTP | Code | Cause |
|------|------|-------|
| 401 | `UNAUTHORIZED` | Missing or invalid JWT |
| 403 | `FORBIDDEN` | User's departments do not include this RFC function |
| 422 | `RFC_ERROR` | SAP returned false / RETURN table contains an error |
| 503 | `SAP_UNAVAILABLE` | SAP connection is down (reconnect in progress) |
| 503 | `POOL_EXHAUSTED` | All worker queues are full — reduce request rate |
| 500 | `INTERNAL_ERROR` | Unexpected server error |

---

## Configuration reference

| Key | Default | Description |
|-----|---------|-------------|
| `SapNco:ServiceWorkerCount` | `4` | Always-connected workers for ordinary RFC calls |
| `SapNco:ElevatedWorkerCount` | `2` | Unconnected-by-default workers for per-user elevated calls |
| `SapNco:ServiceAccounts` | *(empty)* | Optional array of per-worker service accounts — worker *i* uses `ServiceAccounts[i % Count]`; falls back to the single `ServiceAccount` for every worker when empty |
| `SapNco:ElevatedAcquireTimeoutSeconds` | `30` | Max wait for a free elevated slot |
| `SapNco:MaxQueueDepth` | `50` | Max queued requests per worker |
| `SapNco:IdleTimeoutSeconds` | `300` | Ping threshold (5 min) |
| `SapNco:HealthCheckIntervalSeconds` | `60` | Monitor tick interval |
| `SapNco:ReconnectDelayMs` | `5000` | Delay before reconnect attempt |
| `SapNco:PoolSize` / `MaxPoolSize` | `2` / `4` | NCo's own internal RFC connection pool size per destination |
| `Auth:PermissionCacheSeconds` | `60` | How long permissions are cached |

---

## Project structure

```
SapServer/
├── Configuration/
│   ├── SapNcoOptions.cs        # Pool + SAP NCo connection settings
│   └── AuthOptions.cs          # JWT + SQL connection settings
├── Controllers/
│   ├── RfcController.cs        # POST /api/rfc/execute, GET /api/rfc/status
│   └── ...                     # many domain-specific controllers — see CLAUDE.md
├── Exceptions/
│   └── SapExceptions.cs        # Domain exceptions → HTTP status codes
├── Helpers/                    # Transport-agnostic RFC/BAPI request builders + response parsers
├── Middleware/
│   └── ExceptionHandlingMiddleware.cs   # OwinMiddleware
├── Models/
│   ├── RfcModels.cs            # RfcRequest, RfcResponse, WorkerStatus
│   └── ApiResponse.cs          # Standard {success, data, error} envelope
├── Services/
│   ├── Interfaces/
│   │   ├── ISapConnectionPool.cs
│   │   └── IPermissionService.cs
│   ├── Nco/
│   │   ├── NcoWorkItem.cs              # Queue item bridging HTTP thread ↔ worker thread
│   │   ├── NcoWorker.cs                # Dedicated thread + pinned SAP NCo connection
│   │   ├── NcoConnectionPool.cs        # Pool of workers, least-loaded routing
│   │   └── NcoDestinationRegistry.cs   # IDestinationConfiguration for NCo
│   ├── SapWorkerHandle.cs      # Opaque handle to a pinned worker
│   ├── SapSessionMonitor.cs    # Timer-based keep-alive + health logging
│   ├── DevAuthMiddleware.cs    # Dev-bypass OWIN auth middleware
│   ├── ServiceProviderDependencyResolver.cs   # Bridges Microsoft.Extensions.DI into Web API 2
│   └── PermissionService.cs    # SQL Server permission lookup with caching
├── sql/
│   └── SapPermissions_setup.sql
├── libs/                       # Place sapnco.dll/sapnco_utils.dll/sapnco-native/ here
├── interop-stubs/SapNco.DevStub/   # Compile-only stand-in when libs/ is empty
├── Startup.cs                  # OWIN composition root (replaces Program.cs)
├── web.config                  # IIS hosting config
├── appsettings.json
└── appsettings.example.json
```
