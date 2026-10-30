/* =============================================================================
   Prima Three Acre Farms - Asset Finder
   001_schema.sql : tables, constraints and indexes
   Idempotent: safe to run more than once (CI applies every script twice).
   Target: SQL Server 2016 SP1+ (compatibility level 130).
   ============================================================================= */
SET NOCOUNT ON;
GO

/* ---------- Users (local accounts; roles: Admin, Staff, Auditor) ---------- */
IF OBJECT_ID(N'dbo.AppUsers', N'U') IS NULL
CREATE TABLE dbo.AppUsers
(
    UserId           INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AppUsers PRIMARY KEY,
    Username         NVARCHAR(50)  NOT NULL,
    DisplayName      NVARCHAR(100) NOT NULL,
    Role             NVARCHAR(20)  NOT NULL,
    PasswordHash     NVARCHAR(200) NOT NULL,
    IsActive         BIT           NOT NULL CONSTRAINT DF_AppUsers_IsActive DEFAULT (1),
    FailedLoginCount INT           NOT NULL CONSTRAINT DF_AppUsers_Failed DEFAULT (0),
    LockoutEndUtc    DATETIME2(0)  NULL,
    LastLoginUtc     DATETIME2(0)  NULL,
    CreatedUtc       DATETIME2(0)  NOT NULL CONSTRAINT DF_AppUsers_Created DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_AppUsers_Username UNIQUE (Username),
    CONSTRAINT CK_AppUsers_Role CHECK (Role IN (N'Admin', N'Staff', N'Auditor'))
);
GO

/* ---------- Import batches (one row per Excel / AS400 file loaded) ---------- */
IF OBJECT_ID(N'dbo.ImportBatches', N'U') IS NULL
CREATE TABLE dbo.ImportBatches
(
    BatchId          INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ImportBatches PRIMARY KEY,
    FileName         NVARCHAR(200) NOT NULL,
    SourceType       NVARCHAR(10)  NOT NULL,
    FileSha256       CHAR(64)      NOT NULL,
    Status           NVARCHAR(12)  NOT NULL CONSTRAINT DF_ImportBatches_Status DEFAULT (N'Running'),
    RowsRead         INT           NOT NULL CONSTRAINT DF_ImportBatches_Rows DEFAULT (0),
    InsertedCount    INT           NOT NULL CONSTRAINT DF_ImportBatches_Ins DEFAULT (0),
    UpdatedCount     INT           NOT NULL CONSTRAINT DF_ImportBatches_Upd DEFAULT (0),
    DeactivatedCount INT           NOT NULL CONSTRAINT DF_ImportBatches_Deact DEFAULT (0),
    ErrorMessage     NVARCHAR(1000) NULL,
    ImportedBy       NVARCHAR(100) NOT NULL,
    StartedUtc       DATETIME2(0)  NOT NULL CONSTRAINT DF_ImportBatches_Started DEFAULT (SYSUTCDATETIME()),
    CompletedUtc     DATETIME2(0)  NULL,
    CONSTRAINT CK_ImportBatches_Source CHECK (SourceType IN (N'Excel', N'AS400')),
    CONSTRAINT CK_ImportBatches_Status CHECK (Status IN (N'Running', N'Completed', N'Failed'))
);
GO

/* ---------- Fixed assets (master data; the Excel register is the source of truth) ---------- */
IF OBJECT_ID(N'dbo.Assets', N'U') IS NULL
CREATE TABLE dbo.Assets
(
    AssetCode         BIGINT         NOT NULL CONSTRAINT PK_Assets PRIMARY KEY,
    Farm              NVARCHAR(50)   NOT NULL,
    Department        NVARCHAR(50)   NULL,
    AssetName         NVARCHAR(200)  NULL,
    DetailName        NVARCHAR(400)  NULL,
    Quantity          DECIMAL(18,2)  NULL,
    Remarks           NVARCHAR(1000) NULL,
    PhotoFileName     NVARCHAR(100)  NULL,
    IsActive          BIT            NOT NULL CONSTRAINT DF_Assets_IsActive DEFAULT (1),
    LastImportBatchId INT            NULL CONSTRAINT FK_Assets_ImportBatches REFERENCES dbo.ImportBatches (BatchId),
    CreatedUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_Assets_Created DEFAULT (SYSUTCDATETIME()),
    UpdatedUtc        DATETIME2(0)   NOT NULL CONSTRAINT DF_Assets_Updated DEFAULT (SYSUTCDATETIME())
);
GO

-- Name search and per-farm summaries; INCLUDE columns make the autocomplete query index-only.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Assets_AssetName' AND object_id = OBJECT_ID(N'dbo.Assets'))
    CREATE NONCLUSTERED INDEX IX_Assets_AssetName ON dbo.Assets (AssetName) INCLUDE (Farm, IsActive);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Assets_Farm' AND object_id = OBJECT_ID(N'dbo.Assets'))
    CREATE NONCLUSTERED INDEX IX_Assets_Farm ON dbo.Assets (Farm, IsActive) INCLUDE (PhotoFileName);
GO

/* ---------- Staging table for bulk loads (SqlBulkCopy -> MERGE) ---------- */
IF OBJECT_ID(N'dbo.AssetStaging', N'U') IS NULL
CREATE TABLE dbo.AssetStaging
(
    StagingId   BIGINT IDENTITY(1,1) NOT NULL,
    BatchId     INT            NOT NULL,
    AssetCode   BIGINT         NOT NULL,
    Farm        NVARCHAR(50)   NOT NULL,
    Department  NVARCHAR(50)   NULL,
    AssetName   NVARCHAR(200)  NULL,
    DetailName  NVARCHAR(400)  NULL,
    Quantity    DECIMAL(18,2)  NULL,
    Remarks     NVARCHAR(1000) NULL,
    CONSTRAINT PK_AssetStaging PRIMARY KEY CLUSTERED (BatchId, StagingId)
);
GO

/* ---------- Physical verifications (audit: "asset seen, in this condition, by whom") ---------- */
IF OBJECT_ID(N'dbo.AssetVerifications', N'U') IS NULL
CREATE TABLE dbo.AssetVerifications
(
    VerificationId    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AssetVerifications PRIMARY KEY,
    AssetCode         BIGINT           NOT NULL CONSTRAINT FK_AssetVerifications_Assets REFERENCES dbo.Assets (AssetCode),
    Condition         NVARCHAR(10)     NOT NULL,
    Notes             NVARCHAR(500)    NULL,
    VerifiedBy        NVARCHAR(100)    NOT NULL,
    VerifiedUtc       DATETIME2(0)     NOT NULL CONSTRAINT DF_AssetVerifications_Utc DEFAULT (SYSUTCDATETIME()),
    IdempotencyKey    UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_AssetVerifications_Key DEFAULT (NEWID()),
    FinanceStatus     NVARCHAR(10)     NOT NULL CONSTRAINT DF_AssetVerifications_Fin DEFAULT (N'Pending'),
    FinanceReference  NVARCHAR(40)     NULL,
    CONSTRAINT CK_AssetVerifications_Condition CHECK (Condition IN (N'Good', N'Fair', N'Poor', N'Missing')),
    CONSTRAINT CK_AssetVerifications_Finance CHECK (FinanceStatus IN (N'Pending', N'Posted', N'Failed')),
    CONSTRAINT UQ_AssetVerifications_Key UNIQUE (IdempotencyKey)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssetVerifications_Asset' AND object_id = OBJECT_ID(N'dbo.AssetVerifications'))
    CREATE NONCLUSTERED INDEX IX_AssetVerifications_Asset ON dbo.AssetVerifications (AssetCode, VerifiedUtc DESC);
GO

/* ---------- Outbound integration log (every call to the finance FA register) ---------- */
IF OBJECT_ID(N'dbo.IntegrationLogs', N'U') IS NULL
CREATE TABLE dbo.IntegrationLogs
(
    IntegrationLogId BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_IntegrationLogs PRIMARY KEY,
    CreatedUtc       DATETIME2(3)   NOT NULL,
    TargetSystem     NVARCHAR(50)   NOT NULL,
    Operation        NVARCHAR(100)  NOT NULL,
    IdempotencyKey   NVARCHAR(64)   NULL,
    RequestPayload   NVARCHAR(MAX)  NULL,
    ResponsePayload  NVARCHAR(MAX)  NULL,
    HttpStatus       INT            NULL,
    Succeeded        BIT            NOT NULL,
    DurationMs       INT            NOT NULL,
    ErrorMessage     NVARCHAR(1000) NULL,
    CorrelationId    NVARCHAR(64)   NULL
);
GO

/* ---------- Mock finance store (Development only: simulates the third-party register) ---------- */
IF OBJECT_ID(N'dbo.MockFinanceRegister', N'U') IS NULL
CREATE TABLE dbo.MockFinanceRegister
(
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_MockFinanceRegister PRIMARY KEY,
    Reference      NVARCHAR(40)  NOT NULL,
    IdempotencyKey NVARCHAR(64)  NOT NULL CONSTRAINT UQ_MockFinanceRegister_Key UNIQUE,
    AssetCode      BIGINT        NOT NULL,
    Condition      NVARCHAR(10)  NOT NULL,
    ReceivedUtc    DATETIME2(0)  NOT NULL
);
GO

/* ---------- Append-only audit trail ---------- */
IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
CREATE TABLE dbo.AuditLog
(
    AuditId       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    EventUtc      DATETIME2(3)   NOT NULL CONSTRAINT DF_AuditLog_Utc DEFAULT (SYSUTCDATETIME()),
    Username      NVARCHAR(100)  NOT NULL,
    Action        NVARCHAR(40)   NOT NULL,
    EntityType    NVARCHAR(40)   NULL,
    EntityId      NVARCHAR(100)  NULL,
    Details       NVARCHAR(2000) NULL,
    IpAddress     NVARCHAR(45)   NULL,
    CorrelationId NVARCHAR(64)   NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditLog_EventUtc' AND object_id = OBJECT_ID(N'dbo.AuditLog'))
    CREATE NONCLUSTERED INDEX IX_AuditLog_EventUtc ON dbo.AuditLog (EventUtc DESC) INCLUDE (Username, Action);
GO

-- Audit rows can be inserted but never changed or removed (tamper evidence for auditors).
CREATE OR ALTER TRIGGER dbo.trg_AuditLog_NoUpdateDelete ON dbo.AuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50001, N'AuditLog is append-only.', 1;
END
GO
