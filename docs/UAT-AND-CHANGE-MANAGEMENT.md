# Requirements, testing, UAT and change management

## Requirements (from the farm office)

| ID | Requirement | Acceptance |
|---|---|---|
| R1 | Find an asset by code or name | Matches appear after 2 characters and the card opens from a suggestion or Enter |
| R2 | Show photo; let staff add one | Staff/Admin can add or replace a JPG/PNG of 5 MB or less; Auditor cannot |
| R3 | Keep the Excel register as the source of truth | Admin uploads the workbook; counts of new, updated and deactivated rows are shown |
| R4 | Accept the AS/400 FAMAST extract | Fixed-width file loads; a truncated file is rejected |
| R5 | Record physical verification for audit | Verification is saved with user, time and condition, and sent to the finance register |
| R6 | Other systems can look up assets | REST (JSON/XML) and SOAP with API key |
| R7 | Auditors can see who did what | Audit trail filterable by user/action; rows cannot be edited or deleted |

## Test levels

| Level | What | How |
|---|---|---|
| Unit | Excel/AS400 parsing, zoned decimals, control totals, password hashing, API keys, upload signatures | `dotnet test` (runs in CI on every push) |
| Database | Scripts apply cleanly **twice** (idempotent), procedures execute | CI job `database-validate` on SQL Server 2022 |
| Integration | Finance posting with transient 503s, retry and idempotent replay | Development mock (`MockFinance:TransientFailureRate=0.3`) |
| UAT | Business scenarios below, by farm office users on the UAT server | Signed checklist attached to the change request |

## UAT script

1. Sign in as **staff**. Search "egg" and open *Egg Grading Table*. The details match the workbook.
2. Add a photo from a phone. It appears on the card, and the audit trail shows `PHOTO_ADDED`.
3. Record a verification with condition *Fair*. The message shows the finance reference.
4. Sign in as **auditor**. There is no photo button and no verification form. The audit trail shows steps 2–3.
5. Sign in as **admin** and import the current workbook. The counts match what the farm office expected.
6. Import a workbook renamed from a `.pdf`. It is rejected and nothing changes.
7. Enter a wrong password 5 times. The account locks, and the audit trail shows `LOGIN_LOCKED`.

## Change management

1. **Request.** Raise a change ticket with the business owner's requirement and risk rating.
2. **Branch.** Use `feature/<ticket>`. Every push runs build, tests, the vulnerable-package scan and SQL validation.
3. **Review.** Open a pull request to `main`. Merging needs one reviewer and green CI.
4. **UAT.** A merge to `main` publishes the package. Deployment to UAT waits for the `uat` environment approval.
5. **CAB.** The production deploy waits for the `production` environment approval, which is given only for CAB-approved changes inside the window.
6. **Database.** `database/0*.sql` scripts are idempotent and applied by the pipeline with `sqlcmd` before the app.
   Never edit a released script; add a new numbered one.
7. **Rollback.** Re-run `deploy/iis-deploy.ps1 -Rollback`. Schema changes are additive, so the previous release still runs.
8. **Close.** Attach the UAT sign-off, the deploy log and the post-deploy health check to the ticket.
