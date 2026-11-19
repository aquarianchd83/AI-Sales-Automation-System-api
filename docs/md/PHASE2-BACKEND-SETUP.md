# Phase 2 — Solution Setup, Auth, Users/Roles, Customer Management

Implements, on top of the Phase 1 design: Clean Architecture solution scaffold, EF Core +
SQL Server, ASP.NET Core Identity + JWT auth (access + rotating refresh tokens), Users/Roles
CRUD, and Customer CRUD with CSV/Excel import. No CQRS/MediatR — the Application layer is
plain service classes (`ICustomerService`/`CustomerService`, etc.) per your earlier request.

**Scope note:** this repository is named `-backend`, so Phase 2 (and this plan going forward)
builds the .NET solution only. The Angular admin panel from the Phase 1 doc is treated as a
separate frontend deliverable, not scaffolded here, unless you'd like it added to this repo.

**Compile disclosure:** this sandbox has no .NET SDK installed, so this code has not been
built or run here. It was written carefully against known-good package APIs, but if `dotnet
build` surfaces an error on your machine, tell me the error and I'll fix it immediately.

---

## 1. Solution layout

```
WhatsAppSalesAutomation.sln
src/
  Core/
    WhatsAppSalesAutomation.Domain/         entities, enums, constants - no external deps
    WhatsAppSalesAutomation.Application/    service interfaces + implementations, DTOs, FluentValidation validators
  Infrastructure/
    WhatsAppSalesAutomation.Infrastructure/ EF Core DbContext, Identity + JWT wiring, CSV/Excel import
  Presentation/
    WhatsAppSalesAutomation.Api/            ASP.NET Core Web API, Swagger, Serilog, Program.cs
```

Dependency direction: `Api` → `Application` + `Infrastructure` → `Application` → `Domain`.
`Infrastructure` implements the interfaces `Application` declares (`IApplicationDbContext`,
`IJwtTokenService`, `ICustomerImportService`, `ICurrentUserService`, `IDateTimeProvider`).

## 2. What's implemented

- **Auth**: `POST /api/v1/auth/login`, `refresh-token`, `logout`, `change-password`. JWT access
  tokens (15 min default) + rotating refresh tokens (7 days default, hashed at rest, reuse
  detection revokes all sessions for that user).
- **Users/Roles**: full CRUD + role assignment under `/api/v1/users` and read-only
  `/api/v1/roles`, restricted to `SuperAdmin`/`Admin`. Backed by ASP.NET Core Identity
  (`UserManager<ApplicationUser>`/`RoleManager<ApplicationRole>`), tables renamed to
  `Users`/`Roles`/`UserRoles`/etc. to match the Phase 1 design doc.
- **Customers**: full CRUD, tagging, opt-out, and `/api/v1/customers/import` (multipart file
  upload, CSV or `.xlsx`) under `/api/v1/customers`, any authenticated role. Soft-delete via
  `IsDeleted` + a global EF Core query filter.
- **Cross-cutting**: Serilog (console + rolling file, configured via `appsettings.json`),
  Swagger with a JWT bearer scheme, a global exception-handling middleware mapping
  `NotFoundException`/`ConflictException`/`AuthenticationFailedException`/FluentValidation's
  `ValidationException` to proper HTTP status codes + `ProblemDetails`, and an EF Core
  `SaveChanges` interceptor that stamps `CreatedAt`/`UpdatedAt` on every entity.

**Deferred to later phases per the Phase 1 roadmap:** the `AuditLogs` table itself (Phase 6),
idempotency for outbound messaging (Phase 3, once there's something to send), automated test
projects (Phase 6).

## 3. Prerequisites

- .NET 8 SDK
- SQL Server (local install, or `docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong!Passw0rd" -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`)
- EF Core CLI tool: `dotnet tool install --global dotnet-ef` (skip if already installed)

## 4. First run

```bash
# from the repo root
dotnet restore WhatsAppSalesAutomation.sln

# point at your SQL Server instance (or edit appsettings.Development.json directly)
cd src/Presentation/WhatsAppSalesAutomation.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost,1433;Database=WhatsAppSalesAutomation;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True;"
dotnet user-secrets set "Jwt:Secret" "$(openssl rand -base64 48)"
cd ../../..

# generate the initial migration
dotnet ef migrations add InitialCreate \
  --project src/Infrastructure/WhatsAppSalesAutomation.Infrastructure \
  --startup-project src/Presentation/WhatsAppSalesAutomation.Api

# run - Program.cs applies migrations and seeds roles + the dev Super Admin automatically
dotnet run --project src/Presentation/WhatsAppSalesAutomation.Api
```

Open `https://localhost:{port}/swagger`. In Development, the seeded Super Admin login is
whatever's in `appsettings.Development.json` (`admin@example.com` / `ChangeMe123!` by
default) - **change or remove this before any non-local deployment.**

## 5. Quick smoke test

```bash
# 1. Log in
curl -sk https://localhost:{port}/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@example.com","password":"ChangeMe123!"}'
# → { "accessToken": "...", "refreshToken": "...", "user": {...} }

# 2. Create a customer (use the accessToken above)
curl -sk https://localhost:{port}/api/v1/customers \
  -H "Authorization: Bearer <accessToken>" \
  -H "Content-Type: application/json" \
  -d '{"phoneNumberE164":"+15551234567","firstName":"Ada","lastName":"Lovelace"}'

# 3. Import a CSV (columns: PhoneNumber, FirstName, LastName, Email, Tags)
curl -sk https://localhost:{port}/api/v1/customers/import \
  -H "Authorization: Bearer <accessToken>" \
  -F "file=@customers.csv"
```

Example `customers.csv`:

```csv
PhoneNumber,FirstName,LastName,Email,Tags
+15551234567,Ada,Lovelace,ada@example.com,vip;newsletter
+442071838750,Alan,Turing,alan@example.com,vip
```

## 6. Notes / known trade-offs (called out deliberately, not oversights)

- **No generic repository/`IRepository<T>`**: services depend on `IApplicationDbContext`
  directly. EF Core's `DbContext` already *is* a repository + unit of work; wrapping it again
  is pure ceremony, and it keeps with the "no unnecessary layers" direction from earlier.
- **User list role lookups are N+1** (`GetRolesAsync` per row in `UserService.GetPagedAsync`).
  Fine at admin-panel scale; flagged as a spot to optimize with a join if it ever matters.
- **Customer import is row-by-row** (existence + tag lookups per row). Fine for typical
  first-import sizes; flagged in `CustomerService.ImportAsync` as a batching opportunity for
  very large files.
- **Package versions are pinned to specific known-good releases**, not floated to `latest` -
  this sandbox can't run `dotnet restore` to verify what's current as of your build date, and
  a pinned older-but-real version is safer than guessing a floating range that could resolve
  to a breaking newer major. Feel free to bump them after your first successful restore.

## 7. Next: Phase 3

Campaign management, Media Library, the WhatsApp Cloud API client, the initial-message send
pipeline, the follow-up engine, and Hangfire jobs - per the Phase 1 roadmap.
