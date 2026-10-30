using Microsoft.EntityFrameworkCore;
using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Data;

/// <summary>
/// EF Core maps the tables created by /database/*.sql (no EF migrations: the T-SQL scripts are the
/// reviewed, DBA-owned schema). Hot paths and set-based work go through stored procedures; every call
/// below uses interpolated FormattableString, which EF turns into SqlParameters (no SQL injection).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<AssetVerification> AssetVerifications => Set<AssetVerification>();
    public DbSet<IntegrationLog> IntegrationLogs => Set<IntegrationLog>();
    public DbSet<MockFinanceEntry> MockFinanceRegister => Set<MockFinanceEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("AppUsers");
            e.HasKey(x => x.UserId);
            e.Property(x => x.CreatedUtc).ValueGeneratedOnAdd();
        });

        b.Entity<Asset>(e =>
        {
            e.ToTable("Assets");
            e.HasKey(x => x.AssetCode);
            e.Property(x => x.AssetCode).ValueGeneratedNever();
            e.Property(x => x.Quantity).HasPrecision(18, 2);
            e.Property(x => x.CreatedUtc).ValueGeneratedOnAdd();
        });

        b.Entity<ImportBatch>(e =>
        {
            e.ToTable("ImportBatches");
            e.HasKey(x => x.BatchId);
            e.Property(x => x.FileSha256).IsFixedLength();
        });

        b.Entity<AssetVerification>(e =>
        {
            e.ToTable("AssetVerifications");
            e.HasKey(x => x.VerificationId);
        });

        b.Entity<IntegrationLog>(e =>
        {
            e.ToTable("IntegrationLogs");
            e.HasKey(x => x.IntegrationLogId);
        });

        b.Entity<MockFinanceEntry>(e =>
        {
            e.ToTable("MockFinanceRegister");
            e.HasKey(x => x.Id);
        });
    }

    public Task<List<AssetSuggestion>> SearchAssetsAsync(string query, string? farm, int top, CancellationToken ct) =>
        Database.SqlQuery<AssetSuggestion>($"EXEC dbo.usp_SearchAssets @Query = {query}, @Farm = {farm}, @Top = {top}")
            .ToListAsync(ct);

    public Task<List<FarmSummary>> GetFarmSummaryAsync(CancellationToken ct) =>
        Database.SqlQuery<FarmSummary>($"EXEC dbo.usp_GetFarmSummary").ToListAsync(ct);

    public async Task<ImportResult> MergeStagingAsync(int batchId, CancellationToken ct) =>
        (await Database.SqlQuery<ImportResult>($"EXEC dbo.usp_MergeAssetsFromStaging @BatchId = {batchId}").ToListAsync(ct)).Single();

    public Task<List<AuditEntry>> SearchAuditAsync(string? username, string? action, int page, int pageSize, CancellationToken ct) =>
        Database.SqlQuery<AuditEntry>(
            $"EXEC dbo.usp_SearchAuditLog @Username = {username}, @Action = {action}, @PageNumber = {page}, @PageSize = {pageSize}")
            .ToListAsync(ct);

    public Task RecordLoginAttemptAsync(string username, bool succeeded, int maxFailed, int lockoutMinutes, CancellationToken ct) =>
        Database.ExecuteSqlAsync(
            $"EXEC dbo.usp_RecordLoginAttempt @Username = {username}, @Succeeded = {succeeded}, @MaxFailed = {maxFailed}, @LockoutMinutes = {lockoutMinutes}", ct);

    public Task WriteAuditAsync(string user, string action, string? entity, string? entityId, string? details,
        string? ip, string? correlationId, CancellationToken ct) =>
        Database.ExecuteSqlAsync(
            $"EXEC dbo.usp_WriteAuditLog @Username = {user}, @Action = {action}, @EntityType = {entity}, @EntityId = {entityId}, @Details = {details}, @IpAddress = {ip}, @CorrelationId = {correlationId}", ct);
}
