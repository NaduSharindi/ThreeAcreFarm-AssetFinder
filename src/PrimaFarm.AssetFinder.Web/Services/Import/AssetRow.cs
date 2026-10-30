namespace PrimaFarm.AssetFinder.Web.Services.Import;

/// <summary>One asset line read from a register file, before it is staged in SQL Server.</summary>
public sealed record AssetRow(
    long AssetCode,
    string Farm,
    string? Department,
    string? AssetName,
    string? DetailName,
    decimal? Quantity,
    string? Remarks);

public sealed record RegisterReadResult(IReadOnlyList<AssetRow> Rows, IReadOnlyList<string> Farms, IReadOnlyList<string> Warnings);

internal static class Text
{
    /// <summary>Trims, turns blanks into null and cuts to the column width so SQL never truncates silently.</summary>
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}
