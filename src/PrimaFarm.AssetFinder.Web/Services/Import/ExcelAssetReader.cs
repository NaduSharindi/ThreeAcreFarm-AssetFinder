using System.Globalization;
using ClosedXML.Excel;
using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Services.Import;

/// <summary>
/// Reads the Breeder Farms Fixed Assets workbook. Every farm is its own sheet with the columns
/// Asset Code | Department | Asset Name | Detail Name | Asset Quantity | Remarks, but the header row
/// sits on a different line in each sheet (title rows above it). Instead of a hard-coded row map,
/// the reader looks for the "Asset Code" header in the first 30 rows, so a new farm sheet needs no code change.
/// </summary>
public static class ExcelAssetReader
{
    private const int HeaderScanRows = 30;

    private static readonly Dictionary<string, string[]> HeaderAliases = new()
    {
        ["Department"] = ["department", "dept"],
        ["AssetName"] = ["asset name", "name"],
        ["DetailName"] = ["detail name", "description", "details"],
        ["Quantity"] = ["asset quantity", "quantity", "qty"],
        ["Remarks"] = ["remarks", "remark", "notes"]
    };

    public static RegisterReadResult Read(Stream stream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new BusinessRuleException("Could not open the file as an Excel workbook. Open it in Excel to check it is not corrupt.");
        }

        using (workbook)
        {
            var rows = new List<AssetRow>();
            var farms = new List<string>();
            var warnings = new List<string>();

            foreach (var sheet in workbook.Worksheets)
            {
                var header = FindHeaderRow(sheet);
                if (header is null)
                {
                    warnings.Add($"Sheet '{sheet.Name}' has no 'Asset Code' header - skipped.");
                    continue;
                }

                var columns = MapColumns(header);
                var farm = Text.Clean(sheet.Name, 50)!;
                var before = rows.Count;
                var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 0;

                for (var r = header.RowNumber() + 1; r <= lastRow; r++)
                {
                    var row = sheet.Row(r);
                    if (!TryGetCode(row.Cell(1), out var code)) continue;   // section titles, totals, blank lines

                    rows.Add(new AssetRow(
                        code,
                        farm,
                        Text.Clean(row.Cell(columns["Department"]).GetFormattedString(), 50),
                        Text.Clean(row.Cell(columns["AssetName"]).GetFormattedString(), 200),
                        Text.Clean(row.Cell(columns["DetailName"]).GetFormattedString(), 400),
                        GetDecimal(row.Cell(columns["Quantity"])),
                        Text.Clean(row.Cell(columns["Remarks"]).GetFormattedString(), 1000)));
                }

                if (rows.Count > before) farms.Add(farm);
                else warnings.Add($"Sheet '{sheet.Name}' has a header but no asset rows.");
            }

            if (rows.Count == 0)
                throw new BusinessRuleException("No asset rows were found. Each farm sheet needs an 'Asset Code' header with numeric codes in column A below it.");

            return new RegisterReadResult(rows, farms, warnings);
        }
    }

    private static IXLRow? FindHeaderRow(IXLWorksheet sheet)
    {
        for (var r = 1; r <= HeaderScanRows; r++)
        {
            var text = sheet.Cell(r, 1).GetString().Trim();
            if (text.Equals("Asset Code", StringComparison.OrdinalIgnoreCase)) return sheet.Row(r);
        }
        return null;
    }

    /// <summary>Column numbers by header text, falling back to the standard layout (B..F).</summary>
    private static Dictionary<string, int> MapColumns(IXLRow header)
    {
        var map = new Dictionary<string, int>
        {
            ["Department"] = 2, ["AssetName"] = 3, ["DetailName"] = 4, ["Quantity"] = 5, ["Remarks"] = 6
        };
        foreach (var cell in header.CellsUsed())
        {
            var text = cell.GetString().Trim().ToLowerInvariant();
            foreach (var (field, aliases) in HeaderAliases)
                if (aliases.Contains(text)) map[field] = cell.Address.ColumnNumber;
        }
        return map;
    }

    private static bool TryGetCode(IXLCell cell, out long code)
    {
        code = 0;
        if (cell.DataType == XLDataType.Number)
        {
            var d = cell.GetDouble();
            if (d <= 0 || d != Math.Floor(d)) return false;
            code = (long)d;
            return true;
        }
        return long.TryParse(cell.GetString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out code) && code > 0;
    }

    private static decimal? GetDecimal(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
        return decimal.TryParse(cell.GetString().Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
