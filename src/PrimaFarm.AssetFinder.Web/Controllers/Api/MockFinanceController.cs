using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Integration;

namespace PrimaFarm.AssetFinder.Web.Controllers.Api;

/// <summary>
/// SIMULATED finance Fixed Asset Register API, so the outbound integration can be demonstrated and tested
/// end-to-end without the real system. Disabled outside Development (MockFinance:Enabled=false).
/// Behaves like a well-designed third-party API: API key, Idempotency-Key replay, transient 503s.
/// </summary>
[ApiController]
[Route("mock/finance/api/fixed-assets/verifications")]
[AllowAnonymous, IgnoreAntiforgeryToken, ApiExplorerSettings(IgnoreApi = true)]
public class MockFinanceController(AppDbContext db, IConfiguration config, ILogger<MockFinanceController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(FinanceVerificationRequest request, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromHeader(Name = "X-Api-Key")] string? apiKey, CancellationToken ct)
    {
        if (!config.GetValue<bool>("MockFinance:Enabled")) return NotFound();

        var expected = Encoding.UTF8.GetBytes(config["MockFinance:ApiKey"] ?? "");
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(apiKey ?? ""), expected))
            return Unauthorized();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64)
            return BadRequest(new { error = "Idempotency-Key header is required." });

        // Replay: same key -> same reference, never a duplicate entry.
        var existing = await db.MockFinanceRegister.AsNoTracking().FirstOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
            return Ok(new FinanceVerificationResponse(existing.Reference, "Duplicate"));

        if (Random.Shared.NextDouble() < config.GetValue<double>("MockFinance:TransientFailureRate"))
        {
            logger.LogWarning("Mock finance: simulating transient 503 for {Key}", idempotencyKey);
            return StatusCode(503, new { error = "Register temporarily unavailable." });
        }

        var entry = new MockFinanceEntry
        {
            Reference = $"FAV-{DateTime.UtcNow:yyyyMMdd}-{RandomNumberGenerator.GetInt32(100000, 999999)}",
            IdempotencyKey = idempotencyKey,
            AssetCode = request.AssetCode,
            Condition = request.Condition,
            ReceivedUtc = DateTime.UtcNow
        };
        db.MockFinanceRegister.Add(entry);
        await db.SaveChangesAsync(ct);
        return StatusCode(201, new FinanceVerificationResponse(entry.Reference, "Recorded"));
    }
}
