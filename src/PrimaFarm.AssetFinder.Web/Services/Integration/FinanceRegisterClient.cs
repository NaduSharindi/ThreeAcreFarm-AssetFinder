using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PrimaFarm.AssetFinder.Web.Data;
using PrimaFarm.AssetFinder.Web.Infrastructure;
using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Services.Integration;

public class FinanceOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";          // supplied via environment / IIS app settings in production
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed record PostingOutcome(bool Succeeded, string? Reference, string? Error);

public sealed record FinanceVerificationRequest(
    string SourceSystem,
    long AssetCode,
    string Farm,
    string Condition,
    string VerifiedBy,
    DateTime VerifiedUtc);

public sealed record FinanceVerificationResponse(string Reference, string Status);

/// <summary>
/// Outbound integration with the finance Fixed Asset Register: every physical verification is
/// posted so finance can flag "Missing" or "Poor" assets for write-off / impairment review.
///  - Retries, timeouts and circuit breaker come from the resilience pipeline configured in Program.cs.
///  - Retrying a POST is safe because every request carries an Idempotency-Key (the verification's GUID);
///    the receiver returns the original reference instead of creating a duplicate.
///  - Every call is written to dbo.IntegrationLogs for incident analysis (the API key is never logged).
/// </summary>
public class FinanceRegisterClient(
    HttpClient http,
    AppDbContext db,
    IOptions<FinanceOptions> options,
    IHttpContextAccessor accessor,
    ILogger<FinanceRegisterClient> logger)
{
    public const string TargetSystem = "FinanceFixedAssetRegister";

    public async Task<PostingOutcome> PostVerificationAsync(AssetVerification verification, string farm, CancellationToken ct)
    {
        var body = new FinanceVerificationRequest("PRIMA-ASSET-FINDER", verification.AssetCode, farm,
            verification.Condition, verification.VerifiedBy, verification.VerifiedUtc);
        var key = verification.IdempotencyKey.ToString("N");

        var correlationId = accessor.HttpContext is { } ctx ? CorrelationIdMiddleware.Get(ctx) : Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/fixed-assets/verifications") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Api-Key", options.Value.ApiKey);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId);

        var log = new IntegrationLog
        {
            CreatedUtc = DateTime.UtcNow,
            TargetSystem = TargetSystem,
            Operation = "POST api/fixed-assets/verifications",
            IdempotencyKey = key,
            RequestPayload = JsonSerializer.Serialize(body),
            CorrelationId = correlationId
        };

        var sw = Stopwatch.StartNew();
        PostingOutcome outcome;
        try
        {
            using var response = await http.SendAsync(request, ct);
            log.HttpStatus = (int)response.StatusCode;
            log.ResponsePayload = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<FinanceVerificationResponse>(log.ResponsePayload, JsonSerializerOptions.Web);
                outcome = new PostingOutcome(true, parsed?.Reference, null);
            }
            else
            {
                outcome = new PostingOutcome(false, null, $"Finance register returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.CircuitBreaker.BrokenCircuitException
                                       or Polly.Timeout.TimeoutRejectedException)
        {
            outcome = new PostingOutcome(false, null, $"Finance register unavailable: {ex.GetType().Name}");
            logger.LogWarning(ex, "Finance posting failed for verification {VerificationId}", verification.VerificationId);
        }

        log.DurationMs = (int)sw.ElapsedMilliseconds;
        log.Succeeded = outcome.Succeeded;
        log.ErrorMessage = outcome.Error;
        db.IntegrationLogs.Add(log);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Finance posting verification {VerificationId}: success={Succeeded} ref={Reference} in {DurationMs} ms",
            verification.VerificationId, outcome.Succeeded, outcome.Reference, log.DurationMs);
        return outcome;
    }
}
