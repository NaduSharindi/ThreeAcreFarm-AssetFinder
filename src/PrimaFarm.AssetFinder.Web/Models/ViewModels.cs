using System.ComponentModel.DataAnnotations;

namespace PrimaFarm.AssetFinder.Web.Models;

public class LoginViewModel
{
    [Required, StringLength(50)]
    public string Username { get; set; } = "";

    [Required, StringLength(128), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

public class LookupViewModel
{
    public IReadOnlyList<FarmSummary> Farms { get; set; } = [];
    public bool CanEdit { get; set; }
}

public class ImportViewModel
{
    public IReadOnlyList<ImportBatch> RecentBatches { get; set; } = [];
    public ImportResult? Result { get; set; }
    public string? Error { get; set; }
}

public class AuditViewModel
{
    public IReadOnlyList<AuditEntry> Entries { get; set; } = [];
    public string? Username { get; set; }
    public string? Action { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public int TotalRows { get; set; }
    public int TotalPages => Math.Max(1, (TotalRows + PageSize - 1) / PageSize);
}
