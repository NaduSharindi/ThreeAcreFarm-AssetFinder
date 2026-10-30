using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Assets;

namespace PrimaFarm.AssetFinder.Web.Controllers.Api;

/// <summary>
/// Read-only REST API, versioned under /api/v1. Used by the lookup page (session cookie) and by other
/// systems such as the ERP gateway (X-Api-Key). Returns JSON by default, XML with Accept: application/xml.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = Policies.ApiRead)]
[EnableRateLimiting("api")]
[Produces("application/json", "application/xml")]
public class AssetsApiController(AssetService assets) : ControllerBase
{
    /// <summary>Full details of one asset, including photo link and last physical verification.</summary>
    [HttpGet("assets/{code:long}")]
    [ProducesResponseType<AssetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssetDto>> Get(long code, CancellationToken ct)
    {
        var asset = await assets.GetAsync(code, ct);
        return asset is null ? Problem(statusCode: 404, title: $"No asset found with code {code}.") : asset;
    }

    /// <summary>Autocomplete: up to 8 matches by code or name (minimum 2 characters).</summary>
    [HttpGet("assets/search")]
    public async Task<ActionResult<List<AssetSuggestion>>> Search([FromQuery] string? q, [FromQuery] string? farm, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return new List<AssetSuggestion>();
        return await assets.SearchAsync(q.Trim(), farm, ct);
    }

    /// <summary>Asset, photo and verification counts per farm.</summary>
    [HttpGet("farms/summary")]
    public async Task<ActionResult<List<FarmSummary>>> FarmSummary(CancellationToken ct) => await assets.GetFarmSummaryAsync(ct);
}
