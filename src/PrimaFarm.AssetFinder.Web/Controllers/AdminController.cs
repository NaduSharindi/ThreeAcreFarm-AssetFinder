using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Assets;

namespace PrimaFarm.AssetFinder.Web.Controllers;

[Authorize(Policy = Policies.CanViewAudit)]
public class AdminController(AppDbContext db, AssetService assets) : Controller
{
    public async Task<IActionResult> Audit(string? user, string? evt, int page = 1, CancellationToken ct = default)
    {
        var model = new AuditViewModel
        {
            Username = string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
            Action = string.IsNullOrWhiteSpace(evt) ? null : evt.Trim().ToUpperInvariant(),
            Page = Math.Max(1, page)
        };
        model.Entries = await db.SearchAuditAsync(model.Username, model.Action, model.Page, model.PageSize, ct);
        model.TotalRows = model.Entries.FirstOrDefault()?.TotalRows ?? 0;
        return View(model);
    }

    public async Task<IActionResult> Integrations(CancellationToken ct)
    {
        ViewBag.FailedCount = await db.AssetVerifications.CountAsync(v => v.FinanceStatus == "Failed", ct);
        return View(await db.IntegrationLogs.AsNoTracking().OrderByDescending(l => l.IntegrationLogId).Take(100).ToListAsync(ct));
    }

    [HttpPost, Authorize(Policy = Policies.CanImport)]
    public async Task<IActionResult> RetryFailed(CancellationToken ct)
    {
        var posted = await assets.RetryFailedPostingsAsync(ct);
        TempData["Message"] = $"Resent failed verifications: {posted} posted to the finance register.";
        return RedirectToAction(nameof(Integrations));
    }
}
