/* =============================================================================
   Prima Three Acre Farms - Asset Finder
   900_backup_and_maintenance.sql : operational jobs (SQL Agent / Task Scheduler)
   NOT applied by the application. Run by the DBA under change management.
   ============================================================================= */
SET NOCOUNT ON;
GO

/* ---------- Business continuity: nightly full backup with checksum + verify ---------- */
DECLARE @File NVARCHAR(400) = N'D:\SQLBackups\PrimaAssetFinderDb_' + CONVERT(NCHAR(8), SYSDATETIME(), 112) + N'.bak';
BACKUP DATABASE PrimaAssetFinderDb TO DISK = @File WITH INIT, CHECKSUM, COMPRESSION, STATS = 10;
RESTORE VERIFYONLY FROM DISK = @File WITH CHECKSUM;
GO

/* ---------- Index maintenance: reorganise 10-30 % fragmentation, rebuild above 30 % ---------- */
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql += CASE WHEN s.avg_fragmentation_in_percent > 30
                    THEN N'ALTER INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(o.name) + N' REBUILD;'
                    ELSE N'ALTER INDEX ' + QUOTENAME(i.name) + N' ON dbo.' + QUOTENAME(o.name) + N' REORGANIZE;' END
FROM   sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') s
JOIN   sys.indexes i ON i.object_id = s.object_id AND i.index_id = s.index_id
JOIN   sys.objects o ON o.object_id = s.object_id
WHERE  s.avg_fragmentation_in_percent >= 10 AND s.page_count > 100 AND i.name IS NOT NULL;
EXEC sys.sp_executesql @sql;
EXEC sys.sp_updatestats;
GO

/* ---------- Retention: integration payloads older than 2 years (audit log is kept) ---------- */
DELETE FROM dbo.IntegrationLogs WHERE CreatedUtc < DATEADD(YEAR, -2, SYSUTCDATETIME());
GO

/* ---------- Health query for incident triage: slowest cached statements ---------- */
SELECT TOP (10)
       qs.total_elapsed_time / qs.execution_count / 1000 AS AvgMs,
       qs.execution_count,
       SUBSTRING(st.text, 1, 300) AS Statement
FROM   sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE  st.dbid = DB_ID()
ORDER BY AvgMs DESC;
GO
