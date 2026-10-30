using System.Text;
using ClosedXML.Excel;
using PrimaFarm.AssetFinder.Web.Models;
using PrimaFarm.AssetFinder.Web.Services.Import;

namespace PrimaFarm.AssetFinder.Tests;

public class ExcelAssetReaderTests
{
    private static MemoryStream Workbook(Action<XLWorkbook> build)
    {
        using var wb = new XLWorkbook();
        build(wb);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Finds_header_on_any_row_and_skips_section_titles()
    {
        using var file = Workbook(wb =>
        {
            var ws = wb.Worksheets.Add("Kosgama");
            ws.Cell(1, 1).Value = "FIXED ASSETS SCHEDULE";
            ws.Cell(7, 1).Value = "Asset Code";
            ws.Cell(7, 3).Value = "Asset Name";
            ws.Cell(7, 5).Value = "Asset Quantity";
            ws.Cell(8, 1).Value = "PRODUCTION";                 // section title, not an asset
            ws.Cell(9, 1).Value = 60110630;
            ws.Cell(9, 2).Value = " PRODN ";
            ws.Cell(9, 3).Value = "Feeder Line";
            ws.Cell(9, 5).Value = 2;
            ws.Cell(10, 1).Value = "60110631";                  // code stored as text
            ws.Cell(10, 3).Value = "Drinker Line";
        });

        var result = ExcelAssetReader.Read(file);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new AssetRow(60110630, "Kosgama", "PRODN", "Feeder Line", null, 2m, null), result.Rows[0]);
        Assert.Equal(60110631, result.Rows[1].AssetCode);
        Assert.Equal(["Kosgama"], result.Farms);
    }

    [Fact]
    public void Sheet_without_header_is_skipped_with_warning()
    {
        using var file = Workbook(wb =>
        {
            wb.Worksheets.Add("Notes").Cell(1, 1).Value = "Read me";
            var ws = wb.Worksheets.Add("Meegoda");
            ws.Cell(6, 1).Value = "Asset Code";
            ws.Cell(7, 1).Value = 60150300;
        });

        var result = ExcelAssetReader.Read(file);

        Assert.Single(result.Rows);
        Assert.Contains(result.Warnings, w => w.Contains("Notes"));
    }

    [Fact]
    public void Workbook_with_no_assets_is_rejected()
    {
        using var file = Workbook(wb => wb.Worksheets.Add("Sheet1").Cell(1, 1).Value = "nothing here");
        Assert.Throws<BusinessRuleException>(() => ExcelAssetReader.Read(file));
    }

    [Fact]
    public void Non_excel_content_is_rejected_cleanly()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("not a workbook"));
        Assert.Throws<BusinessRuleException>(() => ExcelAssetReader.Read(file));
    }
}

public class As400AssetReaderTests
{
    private static string Detail(long code, string farm, string qty) =>
        $"D{code:D12}{farm,-15}{"PRODN",-5}{"Feeder Line",-40}{"Pan feeder",-60}{qty,11}{"ok",-50}";

    private static MemoryStream Extract(params string[] lines) =>
        new(Encoding.Latin1.GetBytes(string.Join("\r\n", lines)));

    [Fact]
    public void Reads_fixed_width_records()
    {
        using var file = Extract("HFAMAST    20261030", Detail(60110630, "Kosgama", "00000000250"), "T000000001");

        var row = Assert.Single(As400AssetReader.Read(file).Rows);

        Assert.Equal(new AssetRow(60110630, "Kosgama", "PRODN", "Feeder Line", "Pan feeder", 2.50m, "ok"), row);
    }

    [Fact]
    public void Trailer_count_mismatch_rejects_the_file()
    {
        using var file = Extract("HFAMAST    20261030", Detail(60110630, "Kosgama", "00000000100"), "T000000002");
        var ex = Assert.Throws<BusinessRuleException>(() => As400AssetReader.Read(file));
        Assert.Contains("Control total", ex.Message);
    }

    [Fact]
    public void Missing_trailer_rejects_the_file()
    {
        using var file = Extract("HFAMAST    20261030", Detail(60110630, "Kosgama", "00000000100"));
        Assert.Throws<BusinessRuleException>(() => As400AssetReader.Read(file));
    }

    [Theory]
    [InlineData("00000001250", 12.50)]
    [InlineData("0000000125-", -1.25)]
    [InlineData("00000000000", 0)]
    public void Parses_zoned_decimal_with_implied_decimals(string raw, double expected) =>
        Assert.Equal((decimal)expected, As400AssetReader.ParseZoned(raw, 1));

    [Fact]
    public void Sample_file_in_repo_is_valid()
    {
        var path = Path.Combine(RepoRoot(), "samples", "as400_famast_sample.txt");
        using var file = File.OpenRead(path);
        Assert.Equal(5, As400AssetReader.Read(file).Rows.Count);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PrimaFarm.AssetFinder.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
