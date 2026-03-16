# SapServer

ASP.NET Core 10 Web API that exposes SAP RFC function calls over HTTPS to the enterprise frontend hosted by **sql2005-bridge**. It replaces the WinUI 3 desktop application (`sap-gui-async`) with a multi-user, server-side architecture.

---

## Why this project exists

The desktop application required SAP GUI to be installed on every workstation. Each user managed their own SAP session, leading to credential sprawl and no central audit trail.

SapServer centralises SAP access:

- A **single service account** is kept permanently logged in per STA thread — no per-request login latency.
- A **configurable pool of STA threads** (default: `ProcessorCount`) handles concurrent RFC calls safely.
- The existing enterprise website can trigger SAP operations directly via HTTP, authenticated through the same user accounts already managed by sql2005-bridge.

---

## Architecture

```
sql2005-bridge frontend  (browser)
        │
        │  HTTPS + JWT Bearer token
        ▼
┌───────────────────────────────────────┐
│  SapServer  (ASP.NET Core 10)         │
│                                       │
│  RfcController                        │
│    │  permission check (SQL Server)   │
│    │  → ISapConnectionPool            │
│    │      │                           │
│    │      │  least-loaded worker      │
│    │      ▼                           │
│  ┌──────────────────────────────┐     │
│  │  SapConnectionPool           │     │
│  │  ┌────────┐ ┌────────┐ ...  │     │
│  │  │Worker 0│ │Worker 1│      │     │
│  │  │STA thd │ │STA thd │      │     │
│  │  │SAP COM │ │SAP COM │      │     │
│  │  └────────┘ └────────┘      │     │
│  └──────────────────────────────┘     │
│                                       │
│  SapSessionMonitor (BackgroundService)│
│    → pings idle workers               │
│    → logs disconnected slots          │
└───────────────────────────────────────┘
        │
        │  SAP GUI COM (SAPFunctionsOCX)
        ▼
   SAP Application Server
```

### STA Thread Pool

SAP's `SAPFunctionsOCX` COM component must be used from the apartment thread that created it, and that thread must be STA (Single-Threaded Apartment). ASP.NET Core's thread pool is MTA, so direct RFC calls from request handlers would fail or corrupt COM state.

The solution: each `SapStaWorker` owns one dedicated STA thread. HTTP request handlers post `SapWorkItem` objects (containing a `TaskCompletionSource`) to the worker's `BlockingCollection`. The STA thread executes the RFC call synchronously and sets the result on the TCS, which resumes the awaiting HTTP handler on the thread pool. This bridges async/await with COM-safe execution.

### Pool sizing

`SapPool:PoolSize` in `appsettings.json` controls the number of workers:

| Value | Behaviour |
|-------|-----------|
| `0`   | Automatic — `Environment.ProcessorCount` |
| `N`   | Exactly N workers |

Each worker holds one persistent SAP COM session. Set this based on your SAP system's concurrent user licence count, not just CPU count.

### Session keep-alive

SAP GUI sessions disconnect after a configurable idle period (typically 10–15 minutes). `SapSessionMonitor` runs every `HealthCheckIntervalSeconds` and sends an `RFC_PING` to any worker whose `LastActivity` exceeds `IdleTimeoutSeconds`. This resets the SAP idle timer without any user-visible delay.

If a worker is found disconnected, it logs a warning and defers reconnection to the next real request via `EnsureConnected()` — the monitor itself does not block.

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
| .NET 10 SDK | [dotnet.microsoft.com](https://dotnet.microsoft.com) |
| SAP GUI 7.x | Must be installed on the **server** running SapServer |
| SQL Server   | Same instance used by sql2005-bridge |
| Windows OS   | COM interop requires Windows; SAP GUI is Windows-only |

### 1. Place the SAP COM Interop DLL

```
SapServer/
└── libs/
    └── SAPFunctionsOCX.Interop.dll   ← copy from SAP GUI installation
```

If the file doesn't exist in `libs/`, you can generate it from Visual Studio:
- **Project → Add COM Reference → SAP Functions OCX**

Or use `tlbimp.exe` from the Windows SDK:
```
tlbimp "C:\Program Files (x86)\SAP\FrontEnd\SapGui\SAPFunctionsOCX.ocx" /out:libs\SAPFunctionsOCX.Interop.dll
```

### 2. Create the permissions table

Run `sql/SapPermissions_setup.sql` against the `kongsberg` (or equivalent) database:

```bash
sqlcmd -S GATEWAYHO -d kongsberg -i sql/SapPermissions_setup.sql
```

### 3. Configure appsettings.json

Copy `appsettings.example.json` → `appsettings.json` and fill in all values:

```json
{
  "SapPool": {
    "PoolSize": 0,
    "ServiceAccount": {
      "System":   "SAP",
      "Client":   "100",
      "SystemId": "01",
      "User":     "SVC_SAPAPI",
      "Password": "...",
      "Language": "EN"
    }
  },
  "Auth": {
    "JwtSecret":           "min-32-char-random-secret-shared-with-sql2005-bridge",
    "JwtIssuer":           "sql2005-bridge",
    "JwtAudience":         "sap-server",
    "SqlConnectionString": "Server=GATEWAYHO;Database=kongsberg;..."
  },
  "AllowedOrigins": ["https://yourserver:4000"]
}
```

> **Security:** Keep `appsettings.json` out of source control. Set secrets via environment variables in production: `SapPool__ServiceAccount__Password`, `Auth__JwtSecret`, etc.

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
    issuer:    'sql2005-bridge',
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
dotnet run
```

Swagger UI is available at `https://localhost:7200/swagger` in Development.

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
| `SapPool:PoolSize` | `0` | Workers (0 = ProcessorCount) |
| `SapPool:MaxQueueDepth` | `50` | Max queued requests per worker |
| `SapPool:IdleTimeoutSeconds` | `300` | Ping threshold (5 min) |
| `SapPool:HealthCheckIntervalSeconds` | `60` | Monitor tick interval |
| `SapPool:ReconnectDelayMs` | `5000` | Delay before reconnect attempt |
| `Auth:PermissionCacheSeconds` | `60` | How long permissions are cached |

---

## Project structure

```
SapServer/
├── Configuration/
│   ├── SapPoolOptions.cs       # Pool + SAP connection settings
│   └── AuthOptions.cs          # JWT + SQL connection settings
├── Controllers/
│   └── RfcController.cs        # POST /api/rfc/execute, GET /api/rfc/status
├── Exceptions/
│   └── SapExceptions.cs        # Domain exceptions → HTTP status codes
├── Middleware/
│   └── ExceptionHandlingMiddleware.cs
├── Models/
│   ├── RfcModels.cs            # RfcRequest, RfcResponse, WorkerStatus
│   └── ApiResponse.cs          # Standard {success, data, error} envelope
├── Services/
│   ├── Interfaces/
│   │   ├── ISapConnectionPool.cs
│   │   └── IPermissionService.cs
│   ├── SapWorkItem.cs          # Queue item bridging HTTP thread ↔ STA thread
│   ├── SapStaWorker.cs         # Dedicated STA thread + SAP COM connection
│   ├── SapConnectionPool.cs    # Pool of workers, least-loaded routing
│   ├── SapSessionMonitor.cs    # BackgroundService: keep-alive + health logging
│   └── PermissionService.cs    # SQL Server permission lookup with caching
├── sql/
│   └── SapPermissions_setup.sql
├── libs/                       # Place SAPFunctionsOCX.Interop.dll here
├── Program.cs
├── appsettings.json
└── appsettings.example.json
```
