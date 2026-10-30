# Security – OWASP Top 10 (2021) mapping

| OWASP risk | Control in this application | Code |
|---|---|---|
| **A01 Broken Access Control** | Deny-by-default fallback policy; role policies (`CanEdit`, `CanImport`, `CanViewAudit`, `ApiRead`); photo file name comes from the DB, never the request; `returnUrl` limited to local URLs | `Program.cs`, `AccountController`, `AssetService.GetPhotoAsync` |
| **A02 Cryptographic Failures** | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte salt, constant-time compare; API keys stored as SHA-256 only; HSTS + `Secure` cookies outside Development; DataProtection keys persisted for IIS | `PasswordHasher`, `ApiKeyValidator`, `Program.cs` |
| **A03 Injection** | All SQL is parameterised (EF `FormattableString` → `SqlParameter`); stored procedures; LIKE wildcards escaped in `usp_SearchAssets`; Razor HTML-encodes; JS escapes all server text; CA2100 is a build error | `AppDbContext`, `002_stored_procedures.sql`, `assets.js` |
| **A04 Insecure Design** | Import is all-or-nothing (`XACT_ABORT` + transaction); deactivation is soft-delete; AS/400 trailer control total; idempotent finance postings | `usp_MergeAssetsFromStaging`, `As400AssetReader`, `FinanceRegisterClient` |
| **A05 Security Misconfiguration** | CSP `default-src 'self'` with no inline script or style; `X-Frame-Options: DENY`; `nosniff`; `Server` header removed; `no-store` on pages; generic error page | `SecurityHeadersMiddleware`, `ErrorController` |
| **A06 Vulnerable Components** | CI fails on `dotnet list package --vulnerable --include-transitive` | `.github/workflows/ci-cd.yml` |
| **A07 Identification & Authentication Failures** | Lockout after 5 failures for 15 min; one generic message (no username enumeration) plus a dummy hash for unknown users (timing); login rate limit of 10/min/IP; 30-min sliding session; `HttpOnly` + `SameSite=Strict` cookies | `AuthService`, `usp_RecordLoginAttempt`, `Program.cs` |
| **A08 Software & Data Integrity** | Global antiforgery filter on all POSTs (AJAX sends the header token); file signature checks (ZIP/JPEG/PNG magic bytes; no binary in `.txt`); SHA-256 of every imported file recorded | `FileUploadValidator`, `AssetImportService` |
| **A09 Logging & Monitoring Failures** | Append-only `AuditLog` (INSTEAD OF UPDATE/DELETE trigger); audit failures fall back to the structured log; correlation id on every request, log line, audit row and integration call | `AuditService`, `CorrelationIdMiddleware`, `001_schema.sql` |
| **A10 SSRF** | Outbound calls go only to the configured finance base URL; no user-supplied URLs are fetched | `FinanceRegisterClient` |

## Upload rules

| Upload | Allowed | Max | Checks |
|---|---|---|---|
| Register | `.xlsx`, `.xlsm`, `.txt` | 20 MB | extension allow-list, ZIP signature / no NUL bytes, parsed in memory, never saved to disk |
| Photo | `.jpg`, `.jpeg`, `.png` | 5 MB | extension allow-list, JPEG/PNG signature, saved as `<assetCode>.<ext>` outside `wwwroot`, served only to signed-in users |

## Secrets

Production secrets are never committed. That covers the connection string, `Integration:Finance:ApiKey`,
the `ApiClients` hashes and `DataProtection:KeyPath`. They are set as IIS app-pool environment
variables (for example `Integration__Finance__ApiKey`) or in `appsettings.Production.json`, which is git-ignored.
`DevSeed` users and `MockFinance` are only configured in `appsettings.Development.json`.

To issue an API key: generate a random key, give it to the consumer over a secure channel and store only its hash:

```powershell
$key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))
```
