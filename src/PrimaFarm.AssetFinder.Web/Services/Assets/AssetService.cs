using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Integration;

namespace PrimaFarm.AssetFinder.Web.Services.Assets;

public class StorageOptions
{
    /// <summary>Folder for asset photos. Relative paths resolve against the content root. Kept outside wwwroot.</summary>
    public string PhotoPath { get; set; } = "App_Data/photos";
}

public class AssetService(
    AppDbContext db,
    AuditService audit,
    FinanceRegisterClient finance,
    IOptions<StorageOptions> storage,
    IWebHostEnvironment env)
{
    private string PhotoFolder => Path.GetFullPath(Path.Combine(env.ContentRootPath, storage.Value.PhotoPath));

    public async Task<AssetDto?> GetAsync(long code, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.AssetCode == code && a.IsActive, ct);
        if (asset is null) return null;

        var last = await db.AssetVerifications.AsNoTracking()
            .Where(v => v.AssetCode == code)
            .OrderByDescending(v => v.VerifiedUtc)
            .Select(v => new { v.VerifiedUtc, v.Condition })
            .FirstOrDefaultAsync(ct);

        return new AssetDto
        {
            AssetCode = asset.AssetCode,
            Farm = asset.Farm,
            Department = asset.Department,
            AssetName = asset.AssetName,
            DetailName = asset.DetailName,
            Quantity = asset.Quantity,
            Remarks = asset.Remarks,
            PhotoUrl = asset.PhotoFileName is null ? null : $"/Assets/Photo/{asset.AssetCode}?v={asset.UpdatedUtc.Ticks}",
            LastVerifiedUtc = last?.VerifiedUtc,
            LastCondition = last?.Condition
        };
    }

    public Task<List<AssetSuggestion>> SearchAsync(string query, string? farm, CancellationToken ct) =>
        db.SearchAssetsAsync(query.Length > 100 ? query[..100] : query, string.IsNullOrWhiteSpace(farm) ? null : farm, 8, ct);

    public Task<List<FarmSummary>> GetFarmSummaryAsync(CancellationToken ct) => db.GetFarmSummaryAsync(ct);

    /// <summary>Returns the photo path for an asset, or null. The file name comes from the database, never from the request.</summary>
    public async Task<(string Path, string ContentType)?> GetPhotoAsync(long code, CancellationToken ct)
    {
        var fileName = await db.Assets.AsNoTracking().Where(a => a.AssetCode == code).Select(a => a.PhotoFileName).FirstOrDefaultAsync(ct);
        if (fileName is null) return null;
        var path = Path.Combine(PhotoFolder, fileName);
        if (!File.Exists(path)) return null;
        return (path, fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
    }

    public async Task<string> SavePhotoAsync(long code, UploadedFile photo, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.AssetCode == code && a.IsActive, ct)
            ?? throw new BusinessRuleException($"No asset found with code {code}.");

        Directory.CreateDirectory(PhotoFolder);
        // Server-chosen name: <code>.<ext>. The uploaded file name is never used as a path (path traversal).
        var ext = photo.Extension == ".jpeg" ? ".jpg" : photo.Extension;
        var fileName = $"{code}{ext}";
        foreach (var old in new[] { ".jpg", ".png" }.Select(e => Path.Combine(PhotoFolder, $"{code}{e}")))
            if (File.Exists(old)) File.Delete(old);

        await using (var fs = new FileStream(Path.Combine(PhotoFolder, fileName), FileMode.Create, FileAccess.Write))
            await photo.Content.CopyToAsync(fs, ct);

        var replaced = asset.PhotoFileName is not null;
        asset.PhotoFileName = fileName;
        asset.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(replaced ? "PHOTO_REPLACED" : "PHOTO_ADDED", "Asset", code.ToString(),
            $"{photo.Content.Length} bytes from '{photo.FileName}'", ct: ct);
        return $"/Assets/Photo/{code}?v={asset.UpdatedUtc.Ticks}";
    }

    public async Task<VerificationResult> VerifyAsync(long code, VerificationRequest request, string user, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.AssetCode == code && a.IsActive, ct)
            ?? throw new BusinessRuleException($"No asset found with code {code}.");

        var verification = new AssetVerification
        {
            AssetCode = code,
            Condition = request.Condition,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            VerifiedBy = user,
            VerifiedUtc = DateTime.UtcNow,
            IdempotencyKey = Guid.NewGuid()
        };
        db.AssetVerifications.Add(verification);
        await db.SaveChangesAsync(ct);   // saved first: the field record is never lost if finance is down

        await audit.WriteAsync("ASSET_VERIFIED", "Asset", code.ToString(), $"Condition={verification.Condition}", user, ct);
        await PostToFinanceAsync(verification, asset.Farm, ct);

        return new VerificationResult
        {
            VerificationId = verification.VerificationId,
            FinanceStatus = verification.FinanceStatus,
            FinanceReference = verification.FinanceReference,
            Message = verification.FinanceStatus == "Posted"
                ? $"Verified and sent to the finance register ({verification.FinanceReference})."
                : "Verified. The finance register is unavailable - IT can resend it from the Integration log."
        };
    }

    /// <summary>Resends verifications whose finance posting failed (support action after an outage).</summary>
    public async Task<int> RetryFailedPostingsAsync(CancellationToken ct)
    {
        var failed = await db.AssetVerifications.Where(v => v.FinanceStatus == "Failed").OrderBy(v => v.VerificationId).Take(50).ToListAsync(ct);
        var posted = 0;
        foreach (var v in failed)
        {
            var farm = await db.Assets.Where(a => a.AssetCode == v.AssetCode).Select(a => a.Farm).FirstAsync(ct);
            if (await PostToFinanceAsync(v, farm, ct)) posted++;
        }
        await audit.WriteAsync("FINANCE_RETRY", "AssetVerification", null, $"Retried {failed.Count}, posted {posted}", ct: ct);
        return posted;
    }

    private async Task<bool> PostToFinanceAsync(AssetVerification verification, string farm, CancellationToken ct)
    {
        var outcome = await finance.PostVerificationAsync(verification, farm, ct);
        verification.FinanceStatus = outcome.Succeeded ? "Posted" : "Failed";
        verification.FinanceReference = outcome.Reference;
        await db.SaveChangesAsync(ct);
        return outcome.Succeeded;
    }
}
