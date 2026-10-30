# Runbook – Fixed Asset Finder (IIS / Windows Server)

## Where to look first

| Signal | Location |
|---|---|
| Is it up? | `GET /health/live` (process) and `GET /health/ready` (database) |
| Application log | `<site>\logs\prima-assets-YYYYMMDD.log`: rolling daily, 90 days, every line carries `[CorrelationId]` |
| User-reported error | The error page shows a **reference**: search the log and `dbo.AuditLog.CorrelationId` for it |
| Integration failures | **Integration log** page, or `dbo.IntegrationLogs` (HTTP status, latency, error, correlation id) |
| Imports | **Import register** page, or `dbo.ImportBatches` (status, counts, error, file SHA-256) |
| IIS startup failures | Event Viewer → Application (source *IIS AspNetCore Module V2*); enable `stdoutLogEnabled` in `web.config` temporarily |

## Common incidents

**HTTP 500.30 / 502.5 at startup.** The ASP.NET Core Hosting Bundle (.NET 10) is missing, or the app failed on boot.
Check the Event Viewer, then the first lines of today's log. A typical cause is a bad connection string or a missing app-pool environment variable.

**`/health/ready` is Unhealthy.** SQL Server is unreachable or the login failed. Check that the app-pool identity
(`IIS AppPool\PrimaAssets`) has a login with `db_datareader`, `db_datawriter` and `EXECUTE` on `PrimaAssetFinderDb`.

**Users are logged out after every recycle.** `DataProtection:KeyPath` is empty, so set it to a folder the app pool can write to.

**"Account temporarily locked".** This is expected after 5 bad passwords and clears after 15 minutes. To unlock now (record the change):
```sql
UPDATE dbo.AppUsers SET FailedLoginCount = 0, LockoutEndUtc = NULL WHERE Username = N'<user>';
```

**Import rejected.** The message tells the user what is wrong: no "Asset Code" header, a trailer count mismatch,
or content that does not match the extension. A *rolled back* import names its batch number.
Find it in `dbo.ImportBatches.ErrorMessage`. No asset data changes on failure.

**Verifications stuck at "Failed".** The finance register was down: the verification itself is saved.
Once finance is back, an Admin clicks **Resend failed** on the Integration log page. Resending is safe because the
Idempotency-Key is reused, so the register cannot create duplicates.

**Photo missing after deployment.** Photos live in `Storage:PhotoPath` (default `App_Data\photos`).
The deploy script excludes `App_Data`, but in production `PhotoPath` should point outside the site folder and be included in the file backup.

**Slow search.** Run the last query in `database/900_backup_and_maintenance.sql` (top statements by average time)
and check index fragmentation. The search procedure is designed to be served by `IX_Assets_AssetName`.

## Deploy and rollback

```powershell
.\deploy\iis-deploy.ps1 -PackagePath .\publish -SitePath D:\inetpub\PrimaAssets -AppPoolName PrimaAssets -HealthUrl https://assets.prima.local/health/ready
.\deploy\iis-deploy.ps1 -SitePath D:\inetpub\PrimaAssets -AppPoolName PrimaAssets -HealthUrl https://assets.prima.local/health/ready -Rollback
```
The script backs up the current release, takes the site offline with `app_offline.htm`, mirrors the package
and checks health for 60 seconds. If health fails, it restores the backup automatically.

## Business continuity

- Nightly `BACKUP DATABASE … WITH CHECKSUM` + `RESTORE VERIFYONLY` (`900_backup_and_maintenance.sql`).
- Back up the photo folder with the same schedule. The database stores only photo file names.
- The Excel register remains the source of truth: after a total loss, restore the DB or re-import the latest workbook.
