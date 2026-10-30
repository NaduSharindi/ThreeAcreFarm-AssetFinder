# Prima Three Acre Farms – Fixed Asset Finder

An internal web app for farm staff: type (or pick) an **asset code** from the Fixed Assets
register and see every detail for that item, its photo and when it was last physically verified.
It is the ASP.NET Core / SQL Server rebuild of the Meegoda *Breeder Farms Fixed Assets* tool
(Flask + SQLite), with the same workflow and look, hardened for a regulated, audited environment.

| | |
|---|---|
| **Stack** | C# / .NET 10, ASP.NET Core MVC + Web API, Entity Framework Core, SQL Server (T-SQL, stored procedures), SoapCore, ClosedXML, Serilog, xUnit |
| **Hosting** | IIS on Windows Server (in-process), or Kestrel for development |
| **Users** | Admin (import register), Staff (lookup, photos, verification), Auditor (read-only + audit trail) |

## What it does

1. **Lookup.** Search by code or name with live suggestions, filter by farm, and see the asset card with photo.
2. **Photos.** Staff add or replace a photo from the card; on a phone, the button opens the camera.
3. **Physical verification.** Staff record "seen, condition Good/Fair/Poor/Missing". Each record is
   sent to the finance Fixed Asset Register. That call is idempotent and retried, and every attempt is logged.
4. **Import the register.** Admins upload the Excel workbook (one sheet per farm) **or** the
   IBM i (AS/400) `FAMAST` fixed-width extract. The file is bulk-copied into staging, then merged in one transaction.
5. **Integration.** Other systems use a versioned REST API (JSON/XML) or a SOAP endpoint, authenticated with an API key.
6. **Audit and operations.** The audit trail is append-only, and the integration log has a "Resend failed" action.
   Health checks, structured logs with correlation ids, and an IIS deploy script with automatic rollback round it out.

## Run it locally

Prerequisites: .NET 10 SDK and SQL Server LocalDB (installed with Visual Studio). Any SQL Server 2016 SP1+ also works.

```bat
run.bat
```

Then open http://localhost:5016. In Development the app creates the `PrimaAssetFinderDb`
database, applies `database/0*.sql` and seeds three accounts:

| User | Password | Role |
|---|---|---|
| `admin` | `Admin@2026` | Admin |
| `staff` | `Staff@2026` | Staff |
| `auditor` | `Auditor@2026` | Auditor |

To try the import, sign in as `admin` and use **Import register** with
`samples/fixed_assets_sample.xlsx`, then `samples/as400_famast_sample.txt`.
The AS/400 file drops one Kosgama asset, which shows the deactivation rule at work.

```bat
dotnet test PrimaFarm.AssetFinder.slnx
```

## Project layout

```
ThreeAcreFarm-AssetFinder/
├── PrimaFarm.AssetFinder.slnx
├── run.bat
├── database/                     T-SQL: schema, stored procedures, seed, DBA maintenance (900_)
├── src/PrimaFarm.AssetFinder.Web/
│   ├── Program.cs                DI, auth, policies, rate limiting, resilience, SOAP, health
│   ├── Controllers/              MVC pages (Assets, Account, Admin) + Api/ (REST v1, mock finance)
│   ├── Data/                     EF Core DbContext + stored-procedure calls, dev DB initializer
│   ├── Infrastructure/           correlation id, security headers, API-key auth, upload validation
│   ├── Services/
│   │   ├── Assets/               lookup, photos, verification
│   │   ├── Import/               Excel reader, AS/400 fixed-width reader, bulk copy + MERGE
│   │   ├── Integration/          finance Fixed Asset Register client (HttpClient + Polly)
│   │   ├── Security/             PBKDF2 hashing, login/lockout, API-key validation
│   │   └── Soap/                 SOAP service contract
│   ├── Views/, wwwroot/          Razor views, CSS, vanilla JS (no build step)
├── tests/PrimaFarm.AssetFinder.Tests/
├── samples/                      synthetic workbook, AS/400 extract, .http and SOAP requests
├── deploy/iis-deploy.ps1         backup → deploy → health check → auto-rollback
├── .github/workflows/ci-cd.yml   build, test, vulnerable-package scan, SQL validation, UAT/PROD gates
└── docs/                         RUNBOOK, SECURITY, UAT-AND-CHANGE-MANAGEMENT
```

## API

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/v1/assets/{code}` | cookie or `X-Api-Key` | 404 as RFC 9457 ProblemDetails |
| GET | `/api/v1/assets/search?q=&farm=` | cookie or `X-Api-Key` | up to 8 matches |
| GET | `/api/v1/farms/summary` | cookie or `X-Api-Key` | counts per farm |
| POST | `/soap/AssetLookup.asmx` | `X-Api-Key` | `GetAsset`, `GetFarmSummary`; WSDL at `?wsdl` |
| GET | `/health/live`, `/health/ready` | anonymous | readiness checks the database |
| GET | `/openapi/v1.json` | anonymous, Development only | OpenAPI document |

Send `Accept: application/xml` for XML. See `samples/api-requests.http` and `samples/soap_get_asset.xml`.
API keys are stored only as SHA-256 hashes under `ApiClients:Clients`.

## How the role requirements are covered

| Requirement | Where |
|---|---|
| C#, .NET, ASP.NET Core MVC, Web API | `Program.cs`, `Controllers/`, `Views/`, `Controllers/Api/AssetsApiController.cs` |
| SQL Server, T-SQL, stored procedures, optimisation | `database/002_stored_procedures.sql`: set-based `MERGE`, `OUTPUT`, `XACT_ABORT`, ranked search, `OFFSET/FETCH`; covering indexes in `001_schema.sql`; `SqlBulkCopy` in `AssetImportService` |
| Entity Framework | `Data/AppDbContext.cs`: entities plus parameterised `SqlQuery` / `ExecuteSql` calls to the procedures |
| REST / SOAP, JSON / XML | REST v1 with JSON+XML negotiation; SoapCore endpoint; AS/400 extract and Excel import |
| System integration (payments, core systems, third parties) | `Services/Integration/FinanceRegisterClient.cs`: idempotency keys, retry, timeout, circuit breaker, integration log, resend |
| IBM i / AS400, DB2 | `Services/Import/As400AssetReader.cs`: FAMAST fixed-width layout, zoned decimals, header/trailer control totals |
| Git and CI/CD | `.github/workflows/ci-cd.yml`, `.gitignore`, `.editorconfig` analyzers |
| IIS, Windows Server, troubleshooting | `deploy/iis-deploy.ps1`, `docs/RUNBOOK.md`, health checks, correlation ids, Serilog rolling files |
| OWASP and secure coding | `docs/SECURITY.md` |
| Incidents, defects, production issues | `docs/RUNBOOK.md`, Integration log + "Resend failed", error page with reference id |
| Requirements, design, testing, UAT, deployment | `docs/UAT-AND-CHANGE-MANAGEMENT.md`, `tests/`, deploy gates |
| Audit and business continuity | append-only `AuditLog` (trigger-enforced), import batches with file SHA-256, soft-delete, `900_backup_and_maintenance.sql` |

---
Developed by Nadunika Sharindi · 2026 · All sample data is synthetic.
