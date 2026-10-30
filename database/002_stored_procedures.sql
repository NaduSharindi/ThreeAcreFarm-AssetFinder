/* =============================================================================
   Prima Three Acre Farms - Asset Finder
   002_stored_procedures.sql : stored procedures used by the application
   All procedures are CREATE OR ALTER (SQL Server 2016 SP1+) and fully parameterised.
   ============================================================================= */
SET NOCOUNT ON;
GO

/* ---------- Autocomplete: exact code, then code prefix, then name prefix, then name contains ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SearchAssets
    @Query NVARCHAR(100),
    @Farm  NVARCHAR(50) = NULL,
    @Top   INT          = 8
AS
BEGIN
    SET NOCOUNT ON;
    SET @Query = LTRIM(RTRIM(@Query));
    IF LEN(@Query) < 2 RETURN;
    IF @Top NOT BETWEEN 1 AND 50 SET @Top = 8;

    -- User input is data, never pattern syntax: escape LIKE wildcards.
    DECLARE @Escaped NVARCHAR(200) = REPLACE(REPLACE(REPLACE(@Query, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');
    DECLARE @Code BIGINT = TRY_CAST(@Query AS BIGINT);

    SELECT TOP (@Top)
           a.AssetCode, a.AssetName, a.Farm, a.Department
    FROM   dbo.Assets a
    CROSS APPLY (SELECT CASE
                    WHEN a.AssetCode = @Code                                         THEN 0
                    WHEN @Code IS NOT NULL
                         AND CAST(a.AssetCode AS NVARCHAR(20)) LIKE @Escaped + N'%' THEN 1
                    WHEN a.AssetName LIKE @Escaped + N'%'                           THEN 2
                    WHEN a.AssetName LIKE N'%' + @Escaped + N'%'
                      OR a.DetailName LIKE N'%' + @Escaped + N'%'                   THEN 3
                 END AS Rank) r
    WHERE  a.IsActive = 1
      AND  (@Farm IS NULL OR a.Farm = @Farm)
      AND  r.Rank IS NOT NULL
    ORDER BY r.Rank, a.AssetName, a.AssetCode;
END
GO

/* ---------- Per-farm counts for the summary strip and the REST API ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_GetFarmSummary
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Since DATETIME2(0) = DATEADD(DAY, -365, SYSUTCDATETIME());

    SELECT  a.Farm,
            COUNT(*)                                                      AS AssetCount,
            SUM(CASE WHEN a.PhotoFileName IS NOT NULL THEN 1 ELSE 0 END)  AS WithPhotoCount,
            SUM(CASE WHEN v.AssetCode IS NOT NULL THEN 1 ELSE 0 END)      AS VerifiedLast12MonthsCount
    FROM    dbo.Assets a
    LEFT JOIN (SELECT DISTINCT AssetCode FROM dbo.AssetVerifications WHERE VerifiedUtc >= @Since) v
           ON v.AssetCode = a.AssetCode
    WHERE   a.IsActive = 1
    GROUP BY a.Farm
    ORDER BY a.Farm;
END
GO

/* ---------- Import: staging -> Assets in one transaction ----------
   - Duplicate codes within a file: the last row read wins.
   - Assets on a farm that is in this file but whose code is no longer listed are deactivated
     (soft delete: verification history must remain for audit). Farms not in the file are untouched. */
CREATE OR ALTER PROCEDURE dbo.usp_MergeAssetsFromStaging
    @BatchId INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;   -- any error rolls back the whole import

    DECLARE @Actions TABLE (Act NVARCHAR(10) NOT NULL);
    DECLARE @Deactivated INT = 0, @RowsRead INT;

    SELECT @RowsRead = COUNT(*) FROM dbo.AssetStaging WHERE BatchId = @BatchId;

    BEGIN TRY
        BEGIN TRANSACTION;

        ;WITH src AS
        (
            SELECT AssetCode, Farm, Department, AssetName, DetailName, Quantity, Remarks,
                   ROW_NUMBER() OVER (PARTITION BY AssetCode ORDER BY StagingId DESC) AS rn
            FROM   dbo.AssetStaging
            WHERE  BatchId = @BatchId
        )
        MERGE dbo.Assets WITH (HOLDLOCK) AS t
        USING (SELECT * FROM src WHERE rn = 1) AS s
           ON t.AssetCode = s.AssetCode
        WHEN MATCHED AND (   t.IsActive = 0
                          OR t.Farm <> s.Farm
                          OR ISNULL(t.Department, N'') <> ISNULL(s.Department, N'')
                          OR ISNULL(t.AssetName,  N'') <> ISNULL(s.AssetName,  N'')
                          OR ISNULL(t.DetailName, N'') <> ISNULL(s.DetailName, N'')
                          OR ISNULL(t.Remarks,    N'') <> ISNULL(s.Remarks,    N'')
                          OR ISNULL(t.Quantity, -1)    <> ISNULL(s.Quantity, -1)) THEN
            UPDATE SET Farm = s.Farm, Department = s.Department, AssetName = s.AssetName,
                       DetailName = s.DetailName, Quantity = s.Quantity, Remarks = s.Remarks,
                       IsActive = 1, LastImportBatchId = @BatchId, UpdatedUtc = SYSUTCDATETIME()
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (AssetCode, Farm, Department, AssetName, DetailName, Quantity, Remarks, LastImportBatchId)
            VALUES (s.AssetCode, s.Farm, s.Department, s.AssetName, s.DetailName, s.Quantity, s.Remarks, @BatchId)
        OUTPUT $action INTO @Actions (Act);

        UPDATE a
           SET IsActive = 0, LastImportBatchId = @BatchId, UpdatedUtc = SYSUTCDATETIME()
        FROM   dbo.Assets a
        WHERE  a.IsActive = 1
          AND  a.Farm IN (SELECT DISTINCT Farm FROM dbo.AssetStaging WHERE BatchId = @BatchId)
          AND  NOT EXISTS (SELECT 1 FROM dbo.AssetStaging s WHERE s.BatchId = @BatchId AND s.AssetCode = a.AssetCode);
        SET @Deactivated = @@ROWCOUNT;

        UPDATE dbo.ImportBatches
           SET Status = N'Completed',
               RowsRead = @RowsRead,
               InsertedCount = (SELECT COUNT(*) FROM @Actions WHERE Act = N'INSERT'),
               UpdatedCount  = (SELECT COUNT(*) FROM @Actions WHERE Act = N'UPDATE'),
               DeactivatedCount = @Deactivated,
               CompletedUtc = SYSUTCDATETIME()
        WHERE  BatchId = @BatchId;

        DELETE FROM dbo.AssetStaging WHERE BatchId = @BatchId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        UPDATE dbo.ImportBatches
           SET Status = N'Failed', ErrorMessage = LEFT(ERROR_MESSAGE(), 1000), CompletedUtc = SYSUTCDATETIME()
        WHERE  BatchId = @BatchId;
        DELETE FROM dbo.AssetStaging WHERE BatchId = @BatchId;
        THROW;
    END CATCH

    SELECT BatchId, RowsRead, InsertedCount, UpdatedCount, DeactivatedCount
    FROM   dbo.ImportBatches WHERE BatchId = @BatchId;
END
GO

/* ---------- Login attempt: reset or increment the counter, lock after N failures ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_RecordLoginAttempt
    @Username       NVARCHAR(50),
    @Succeeded      BIT,
    @MaxFailed      INT,
    @LockoutMinutes INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @Succeeded = 1
        UPDATE dbo.AppUsers
           SET FailedLoginCount = 0, LockoutEndUtc = NULL, LastLoginUtc = SYSUTCDATETIME()
        WHERE  Username = @Username;
    ELSE
        UPDATE dbo.AppUsers
           SET FailedLoginCount = FailedLoginCount + 1,
               LockoutEndUtc = CASE WHEN FailedLoginCount + 1 >= @MaxFailed
                                    THEN DATEADD(MINUTE, @LockoutMinutes, SYSUTCDATETIME()) ELSE LockoutEndUtc END
        WHERE  Username = @Username;
END
GO

/* ---------- Audit write ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_WriteAuditLog
    @Username      NVARCHAR(100),
    @Action        NVARCHAR(40),
    @EntityType    NVARCHAR(40)   = NULL,
    @EntityId      NVARCHAR(100)  = NULL,
    @Details       NVARCHAR(2000) = NULL,
    @IpAddress     NVARCHAR(45)   = NULL,
    @CorrelationId NVARCHAR(64)   = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT dbo.AuditLog (Username, Action, EntityType, EntityId, Details, IpAddress, CorrelationId)
    VALUES (@Username, @Action, @EntityType, @EntityId, @Details, @IpAddress, @CorrelationId);
END
GO

/* ---------- Audit search (paged, OFFSET/FETCH) ---------- */
CREATE OR ALTER PROCEDURE dbo.usp_SearchAuditLog
    @Username   NVARCHAR(100) = NULL,
    @Action     NVARCHAR(40)  = NULL,
    @PageNumber INT           = 1,
    @PageSize   INT           = 50
AS
BEGIN
    SET NOCOUNT ON;
    IF @PageNumber < 1 SET @PageNumber = 1;
    IF @PageSize NOT BETWEEN 1 AND 200 SET @PageSize = 50;

    SELECT AuditId, EventUtc, Username, Action, EntityType, EntityId, Details, IpAddress, CorrelationId,
           COUNT(*) OVER () AS TotalRows
    FROM   dbo.AuditLog
    WHERE  (@Username IS NULL OR Username = @Username)
      AND  (@Action IS NULL OR Action = @Action)
    ORDER BY EventUtc DESC, AuditId DESC
    OFFSET (@PageNumber - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END
GO
