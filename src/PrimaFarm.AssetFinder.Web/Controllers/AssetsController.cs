using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services;
using PrimaFarm.AssetFinder.Web.Services.Assets;
using PrimaFarm.AssetFinder.Web.Services.Import;
using PrimaFarm.AssetFinder.Web.Services.Security;

namespace PrimaFarm.AssetFinder.Web.Controllers;

/// <summary>Browser UI. State-changing actions are POSTs protected by the global antiforgery filter.</summary>
public class AssetsController(AssetService assets, AssetImportService importer, AuditService audit, IOptions<SecurityOptions> security) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(new LookupViewModel
    {
        Farms = await assets.GetFarmSummaryAsync(ct),
        CanEdit = User.IsInRole(Roles.Admin) || User.IsInRole(Roles.Staff)
    });

    [HttpGet]
    public async Task<IActionResult> Photo(long id, CancellationToken ct)
    {
        var photo = await assets.GetPhotoAsync(id, ct);
        return photo is null ? NotFound() : PhysicalFile(photo.Value.Path, photo.Value.ContentType);
    }

    [HttpPost, Authorize(Policy = Policies.CanEdit), RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> Photo(long id, IFormFile? photo, CancellationToken ct)
    {
        try
        {
            var upload = await FileUploadValidator.ReadAsync(photo, UploadKind.Photo, security.Value.MaxPhotoBytes, ct);
            return Ok(new { photoUrl = await assets.SavePhotoAsync(id, upload, ct) });
        }
        catch (BusinessRuleException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost, Authorize(Policy = Policies.CanEdit)]
    public async Task<IActionResult> Verify(long id, [FromBody] VerificationRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { error = ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage });
        try
        {
            return Ok(await assets.VerifyAsync(id, request, User.Identity!.Name!, ct));
        }
        catch (BusinessRuleException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet, Authorize(Policy = Policies.CanImport)]
    public async Task<IActionResult> Import(CancellationToken ct) =>
        View(new ImportViewModel { RecentBatches = await importer.RecentBatchesAsync(ct) });

    [HttpPost, Authorize(Policy = Policies.CanImport), RequestSizeLimit(21 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? register, CancellationToken ct)
    {
        var model = new ImportViewModel();
        try
        {
            var upload = await FileUploadValidator.ReadAsync(register, UploadKind.Register, security.Value.MaxWorkbookBytes, ct);
            model.Result = await importer.ImportAsync(upload, User.Identity!.Name!, ct);
        }
        catch (BusinessRuleException ex)
        {
            model.Error = ex.Message;
            await audit.WriteAsync("IMPORT_REJECTED", "ImportBatch", null, $"{register?.FileName}: {ex.Message}", ct: ct);
        }
        model.RecentBatches = await importer.RecentBatchesAsync(ct);
        return View(model);
    }
}
