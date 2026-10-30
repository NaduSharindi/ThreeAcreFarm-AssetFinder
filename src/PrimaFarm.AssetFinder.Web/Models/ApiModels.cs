using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace PrimaFarm.AssetFinder.Web.Models;

// API contracts are classes with public setters and parameterless constructors so the same types
// serialise as JSON, XML (XmlSerializer, REST content negotiation) and SOAP (DataContractSerializer).

[DataContract(Namespace = ApiNamespaces.Assets)]
public class AssetDto
{
    [DataMember(Order = 1)] public long AssetCode { get; set; }
    [DataMember(Order = 2)] public string Farm { get; set; } = "";
    [DataMember(Order = 3)] public string? Department { get; set; }
    [DataMember(Order = 4)] public string? AssetName { get; set; }
    [DataMember(Order = 5)] public string? DetailName { get; set; }
    [DataMember(Order = 6)] public decimal? Quantity { get; set; }
    [DataMember(Order = 7)] public string? Remarks { get; set; }
    [DataMember(Order = 8)] public string? PhotoUrl { get; set; }
    [DataMember(Order = 9)] public DateTime? LastVerifiedUtc { get; set; }
    [DataMember(Order = 10)] public string? LastCondition { get; set; }
}

public class AssetSuggestion
{
    public long AssetCode { get; set; }
    public string? AssetName { get; set; }
    public string Farm { get; set; } = "";
    public string? Department { get; set; }
}

[DataContract(Namespace = ApiNamespaces.Assets)]
public class FarmSummary
{
    [DataMember(Order = 1)] public string Farm { get; set; } = "";
    [DataMember(Order = 2)] public int AssetCount { get; set; }
    [DataMember(Order = 3)] public int WithPhotoCount { get; set; }
    [DataMember(Order = 4)] public int VerifiedLast12MonthsCount { get; set; }
}

public class VerificationRequest
{
    [Required, RegularExpression("^(Good|Fair|Poor|Missing)$", ErrorMessage = "Condition must be Good, Fair, Poor or Missing.")]
    public string Condition { get; set; } = "Good";

    [StringLength(500)]
    public string? Notes { get; set; }
}

public class VerificationResult
{
    public int VerificationId { get; set; }
    public string FinanceStatus { get; set; } = "";
    public string? FinanceReference { get; set; }
    public string? Message { get; set; }
}

public class ImportResult
{
    public int BatchId { get; set; }
    public int RowsRead { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeactivatedCount { get; set; }
}

public static class ApiNamespaces
{
    public const string Assets = "http://primafarms.lk/assetfinder/2026";
}
