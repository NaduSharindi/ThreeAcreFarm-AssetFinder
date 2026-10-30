namespace PrimaFarm.AssetFinder.Web.Models;

public static class Roles
{
    public const string Admin = "Admin";       // import the register, everything Staff can do
    public const string Staff = "Staff";       // look up, add photos, record verifications
    public const string Auditor = "Auditor";   // read-only + audit trail
}

public class AppUser
{
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = Roles.Staff;
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public class Asset
{
    public long AssetCode { get; set; }
    public string Farm { get; set; } = "";
    public string? Department { get; set; }
    public string? AssetName { get; set; }
    public string? DetailName { get; set; }
    public decimal? Quantity { get; set; }
    public string? Remarks { get; set; }
    public string? PhotoFileName { get; set; }
    public bool IsActive { get; set; } = true;
    public int? LastImportBatchId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

public class ImportBatch
{
    public int BatchId { get; set; }
    public string FileName { get; set; } = "";
    public string SourceType { get; set; } = "Excel";
    public string FileSha256 { get; set; } = "";
    public string Status { get; set; } = "Running";
    public int RowsRead { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeactivatedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string ImportedBy { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public class AssetVerification
{
    public int VerificationId { get; set; }
    public long AssetCode { get; set; }
    public string Condition { get; set; } = "Good";
    public string? Notes { get; set; }
    public string VerifiedBy { get; set; } = "";
    public DateTime VerifiedUtc { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string FinanceStatus { get; set; } = "Pending";
    public string? FinanceReference { get; set; }
}

public class IntegrationLog
{
    public long IntegrationLogId { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string TargetSystem { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? IdempotencyKey { get; set; }
    public string? RequestPayload { get; set; }
    public string? ResponsePayload { get; set; }
    public int? HttpStatus { get; set; }
    public bool Succeeded { get; set; }
    public int DurationMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CorrelationId { get; set; }
}

public class MockFinanceEntry
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public long AssetCode { get; set; }
    public string Condition { get; set; } = "";
    public DateTime ReceivedUtc { get; set; }
}

/// <summary>Row shape returned by dbo.usp_SearchAuditLog.</summary>
public class AuditEntry
{
    public long AuditId { get; set; }
    public DateTime EventUtc { get; set; }
    public string Username { get; set; } = "";
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string? CorrelationId { get; set; }
    public int TotalRows { get; set; }
}

/// <summary>Raised for expected, user-correctable problems (bad file, unknown code). Shown to the user as-is.</summary>
public class BusinessRuleException(string message) : Exception(message);
