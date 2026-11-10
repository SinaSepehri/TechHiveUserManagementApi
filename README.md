# User Management API

A RESTful ASP.NET Core Web API (.NET 10) built for **TechHive Solutions** to manage employee records.
It was the final project of the *Back-End Development with .NET* course in the Microsoft Full-Stack Developer certificate.

The API lets internal tools create, read, update and delete users. It was then **debugged** (validation,
error handling, performance) and secured with **middleware** for audit logging, standardized errors and
token-based authentication.

---

## Project structure

```
UserManagement/
├── README.md                           ← this file
├── UserManagementAPI/                  ← the Web API
│   ├── Controllers/UsersController.cs  ← CRUD endpoints
│   ├── Middleware/
│   │   ├── ErrorHandlingMiddleware.cs      ← catches unhandled exceptions → JSON 500
│   │   ├── TokenAuthenticationMiddleware.cs← bearer-token check → 401
│   │   ├── TokenAuthenticationOptions.cs   ← token/client settings
│   │   └── RequestLoggingMiddleware.cs     ← audit log of every request
│   ├── Models/                         ← User, CreateUserRequest, UpdateUserRequest, UserRequest (validation)
│   ├── Services/                       ← IUserStore + thread-safe InMemoryUserStore
│   ├── Logs/                           ← daily log files (created at runtime, git-ignored)
│   ├── Program.cs                      ← services, logging and middleware pipeline
│   ├── appsettings.json
│   └── UserManagementAPI.http          ← ready-made requests, including edge cases
└── UserManagementAPI.Tests/            ← 99 xUnit tests (unit + integration)
```

---

## Endpoints

All endpoints require `Authorization: Bearer <token>`.

| Method | Route | Success | Errors |
|---|---|---|---|
| `GET` | `/api/users?page=1&pageSize=50` | `200` list of users; total in `X-Total-Count` header | `400` invalid paging |
| `GET` | `/api/users/{id}` | `200` the user | `404` not found |
| `POST` | `/api/users` | `201` created user + `Location` header | `400` invalid input, `409` duplicate email |
| `PUT` | `/api/users/{id}` | `200` updated user | `400`, `404`, `409` |
| `DELETE` | `/api/users/{id}` | `204` no content | `404` not found |

Example user:

```json
{
  "firstName": "Avery",
  "lastName": "Morgan",
  "email": "avery.morgan@techhive.example",
  "department": "Engineering"
}
```

---

## Phase 1: Debugging

TechHive reported that users were added without validation, that looking up missing users caused errors,
and that the API sometimes crashed. Reviewing the code found these problems, which were then fixed:

| Problem found | Fix |
|---|---|
| The project didn't compile (ambiguous `Execute` helpers in the controller) | A single, typed helper method |
| `[EmailAddress]` accepted `a@b`; names could be digits or `<script>`; nothing was trimmed | Shared `UserRequest` rules: letters (incl. accents), spaces, `'`, `-` and `.` for names; an email pattern that requires a real domain; length limits; trimming; emails stored in lower case |
| The same email could be registered many times | Duplicate check (case-insensitive) → **409 Conflict** |
| Missing users returned an empty 404; paging errors were plain text; crashes returned an empty 500 | Every error returns a JSON body (see *Standard error format*) |
| `GET /api/users` could overflow on very large page numbers, and every read waited behind one lock | Paging with a 100-item limit and overflow-safe math, plus `ReaderWriterLockSlim` so many reads run at the same time |
| Checking emails would have meant scanning every user | An email index (`Dictionary`) makes the duplicate check O(1) |
| `Microsoft.OpenApi` 2.0.0 had a known high-severity vulnerability | Updated packages to 10.0.15 |
| `Location` header was `/api/Users/1` | Lower-case URLs |

---

## Phase 2: Middleware

### Pipeline order

```
Request → ErrorHandling → StatusCodePages → TokenAuthentication → RequestLogging → Controller
```

1. **Error handling first.** It wraps everything after it, so no exception can escape.
2. **Authentication next.** Requests with bad tokens are rejected early, before any other work is done.
3. **Logging last.** It records every authenticated request. Rejected requests are logged by the
   authentication middleware itself, so nothing goes unrecorded.

### Error-handling middleware
Catches unhandled exceptions, logs the full details, and returns a consistent JSON error. Stack traces and
internal messages are **never** sent to the client.

### Standard error format
Every error (400, 401, 404, 405, 409, 500) uses the same RFC 7807 shape, always with an `error` message and a
`traceId` that matches the log entry:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "Server error",
  "status": 500,
  "detail": "Internal server error.",
  "error": "Internal server error.",
  "traceId": "0HNPVCG5TA5N1:00000001"
}
```

Validation errors also list the problems for each field under `errors`.

### Token authentication middleware
- Accepts `Authorization: Bearer <token>`; anything else gets **401 Unauthorized** with a `WWW-Authenticate` header.
- Tokens are configured per client (`Authentication:Clients`), and the client's name is attached to the request.
- Tokens are hashed with SHA-256 at startup and compared with `CryptographicOperations.FixedTimeEquals`,
  which stops attackers from learning anything from response times.
- Tokens shorter than 16 characters are ignored. If no valid token is configured, **every** request is rejected.
- Tokens are never written to the logs.

### Request logging middleware
Logs the HTTP method, path, status code, duration, client and trace ID. 5xx responses are logged as errors and
4xx as warnings. Query strings, headers and bodies are not logged, so no personal data or tokens end up in the
logs. It uses source-generated `LoggerMessage` methods to keep the cost per request small.

### Log files
Logs are written with **Serilog** to the console and to `UserManagementAPI/Logs/usermanagement-YYYYMMDD.log`,
with a new file each day and the last 30 days kept. Log files are git-ignored. Sample:

```
2026-11-10 20:51:44.447 +01:00 [WRN] UserManagementAPI.Middleware.TokenAuthenticationMiddleware: Rejected GET /api/users: invalid token (traceId: 0HNPVCG5TA5MU:00000001).
2026-11-10 20:51:44.640 +01:00 [INF] UserManagementAPI.Middleware.RequestLoggingMiddleware: HTTP POST /api/users responded 201 in 90.5 ms (client: local-dev, traceId: 0HNPVCG5TA5MV:00000001)
2026-11-10 20:51:44.875 +01:00 [WRN] UserManagementAPI.Middleware.RequestLoggingMiddleware: HTTP GET /api/users/999 responded 404 in 4.3 ms (client: local-dev, traceId: 0HNPVCG5TA5N1:00000001)
```

---

## Running the API

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Commands below run from this `UserManagement` folder.

1. **Set a token.** `appsettings.Development.json` is git-ignored, so on a fresh clone use user-secrets:
   ```bash
   cd UserManagementAPI
   dotnet user-secrets set "Authentication:Clients:0:Name" "local-dev"
   dotnet user-secrets set "Authentication:Clients:0:Token" "dev-only-token-change-me-0123456789"
   ```
   In production, use environment variables instead, e.g. `Authentication__Clients__0__Name` and
   `Authentication__Clients__0__Token`. Never put real tokens in `appsettings.json`.
2. **Run it:**
   ```bash
   dotnet run --launch-profile http
   ```
   The API listens on `http://localhost:5070`.
3. **Try it:** open `UserManagementAPI.http` in Visual Studio or VS Code (REST Client) and send the requests,
   or use curl:
   ```bash
   curl -H "Authorization: Bearer dev-only-token-change-me-0123456789" http://localhost:5070/api/users
   ```
   In Development, the OpenAPI document is at `http://localhost:5070/openapi/v1.json` (no token needed).

## Running the tests

```bash
cd UserManagementAPI.Tests
dotnet test
```

The 99 tests run in about 3 seconds:

| Test file | What it covers |
|---|---|
| `InMemoryUserStoreTests` | Store logic, trimming, duplicate emails, paging, 1,000 users created at the same time, speed with 20,000 users |
| `UsersControllerTests` | Controller results (404/409 responses, total-count header) and that unexpected exceptions reach the middleware |
| `UsersApiTests` | Real HTTP calls: CRUD, invalid/missing/too-long/malformed input, non-existent and invalid IDs, paging limits, simulated crashes |
| `MiddlewareTests` | Valid/invalid/missing/malformed tokens, protected endpoints, pipeline order, the standard error format for every status, and the **contents of the log file** |

The integration tests use `WebApplicationFactory` to run the real API in memory, with test tokens and a
temporary log folder.

---

## How Microsoft Copilot helped

Copilot was used as a pair programmer throughout the project:

- **Scaffolding the API.** Copilot generated the first versions of the `User` model, the request DTOs, the
  `IUserStore` interface and the CRUD actions in `UsersController`, which I then refined (for example,
  returning `CreatedAtAction` from `POST` and paging the `GET` list).
- **Finding bugs.** I asked Copilot to review the controller and models against TechHive's bug reports. It
  pointed out the missing validation, the lack of duplicate-email checks, the empty 404/500 responses, and the
  compile errors in the half-finished `Execute` helpers. It also explained why `[EmailAddress]` alone accepts
  addresses like `a@b`.
- **Fixing bugs.** Copilot suggested the shared `UserRequest` base class with regular expressions for names
  and emails, the email index for O(1) duplicate checks, `ReaderWriterLockSlim` for concurrent reads, and
  overflow-safe paging math.
- **Writing the middleware.** Copilot drafted the three middleware classes and explained the correct pipeline
  order. It also suggested security details for authentication: hashing tokens and comparing them with
  `CryptographicOperations.FixedTimeEquals`, adding a `WWW-Authenticate` header to 401 responses, and never
  logging tokens.
- **Logging to file.** Copilot recommended Serilog for file logging with daily rolling files, and
  source-generated `LoggerMessage` methods for fast logging.
- **Testing.** Copilot generated many edge-case tests (malformed JSON, IDs too big for an `int`, unknown
  routes, invalid tokens, a store that throws), plus the `WebApplicationFactory` setup for the integration tests.
- **Debugging the tests.** When some log-file tests failed only occasionally, Copilot helped trace the problem
  to Serilog's global static logger being shared between test instances. The fix was `preserveStaticLogger: true`.

**What I learned:** Copilot speeds up writing boilerplate and spotting likely problems, but its suggestions
still need checking. Running the tests repeatedly and sending real requests to the API caught issues,
including the shared-logger bug, that a quick look at the generated code would have missed.
