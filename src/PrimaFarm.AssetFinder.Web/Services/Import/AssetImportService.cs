using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Services.Import;

/// <summary>
/// Loads a register file into SQL Server: parse -> SqlBulkCopy into dbo.AssetStaging ->
/// dbo.usp_MergeAssetsFromStaging (one transaction, set-based MERGE). Bulk copy + MERGE keeps a
/// 1,500-row workbook to a couple of round trips instead of 1,500 INSERT/UPDATE statements.
/// </summary>
public class AssetImportService(AppDbContext db, AuditService audit, ILogger<AssetImportService> logger)
{
    public async Task<ImportResult> ImportAsync(UploadedFile file, string importedBy, CancellationToken ct)
    {
        var isExcel = file.Extension is ".xlsx" or ".xlsm";
        var sha256 = Convert.ToHexString(SHA256.HashData(file.Content.GetBuffer().AsSpan(0, (int)file.Content.Length)));
        file.Content.Position = 0;

        var read = isExcel ? ExcelAssetReader.Read(file.Content) : As400AssetReader.Read(file.Content);
        foreach (var warning in read.Warnings) logger.LogWarning("Import {File}: {Warning}", file.FileName, warning);

        var batch = new ImportBatch
        {
            FileName = file.FileName,
            SourceType = isExcel ? "Excel" : "AS400",
            FileSha256 = sha256,
            ImportedBy = importedBy,
            StartedUtc = DateTime.UtcNow
        };
        db.ImportBatches.Add(batch);
        await db.SaveChangesAsync(ct);

        await BulkCopyToStagingAsync(batch.BatchId, read.Rows, ct);

        ImportResult result;
        try
        {
            result = await db.MergeStagingAsync(batch.BatchId, ct);
        }
        catch (SqlException ex)
        {
            // The procedure has already rolled back and marked the batch Failed.
            logger.LogError(ex, "Import batch {BatchId} failed in usp_MergeAssetsFromStaging", batch.BatchId);
            await audit.WriteAsync("IMPORT_FAILED", "ImportBatch", batch.BatchId.ToString(), $"{file.FileName}: {ex.Message}", importedBy, ct);
            throw new BusinessRuleException($"The import was rolled back (batch {batch.BatchId}). No assets were changed. Quote this batch number to IT support.");
        }

        await audit.WriteAsync("IMPORT_REGISTER", "ImportBatch", batch.BatchId.ToString(),
            $"{batch.SourceType} '{file.FileName}' sha256={sha256[..16]} farms={string.Join(",", read.Farms)} " +
            $"read={result.RowsRead} inserted={result.InsertedCount} updated={result.UpdatedCount} deactivated={result.DeactivatedCount}",
            importedBy, ct);
        return result;
    }

    public Task<List<ImportBatch>> RecentBatchesAsync(CancellationToken ct) =>
        db.ImportBatches.AsNoTracking().OrderByDescending(b => b.BatchId).Take(10).ToListAsync(ct);

    private async Task BulkCopyToStagingAsync(int batchId, IReadOnlyList<AssetRow> rows, CancellationToken ct)
    {
        var table = new DataTable();
        table.Columns.Add("BatchId", typeof(int));
        table.Columns.Add("AssetCode", typeof(long));
        table.Columns.Add("Farm", typeof(string));
        table.Columns.Add("Department", typeof(string));
        table.Columns.Add("AssetName", typeof(string));
        table.Columns.Add("DetailName", typeof(string));
        table.Columns.Add("Quantity", typeof(decimal));
        table.Columns.Add("Remarks", typeof(string));
        foreach (var r in rows)
            table.Rows.Add(batchId, r.AssetCode, r.Farm, r.Department, r.AssetName, r.DetailName,
                (object?)r.Quantity ?? DBNull.Value, r.Remarks);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.AssetStaging", BatchSize = 5000 };
            foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(table, ct);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }
}
