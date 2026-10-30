using System.Globalization;
using System.Text;
using PrimaFarm.AssetFinder.Web.Models;

namespace PrimaFarm.AssetFinder.Web.Services.Import;

/// <summary>
/// Reads the fixed-asset master extract produced on IBM i (AS/400) from the FAMAST physical file
/// (CPYTOIMPF to the IFS, then FTP in ASCII mode). Fixed-width records:
///
///   H FAMAST yyyyMMdd                                   header  (record type 'H')
///   D + fields below                                    detail  (record type 'D')
///   T nnnnnnnnn                                         trailer (record type 'T', detail count)
///
///   Detail layout (1-based positions, as in the DDS):
///     1      1  Record type 'D'
///     2-13  12  FAASCD  asset code        zoned decimal, zero padded
///    14-28  15  FAFARM  farm
///    29-33   5  FADEPT  department
///    34-73  40  FANAME  asset name
///    74-133 60  FADESC  detail name
///   134-144 11  FAQTY   quantity          zoned decimal, 2 implied decimals, '-' in last position if negative
///   145-194 50  FARMKS  remarks
///
/// The trailer count is a control total: a truncated transfer is rejected instead of half-loaded.
/// </summary>
public static class As400AssetReader
{
    public const int DetailLength = 194;

    public static RegisterReadResult Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        var rows = new List<AssetRow>();
        var warnings = new List<string>();
        int? trailerCount = null;
        var sawHeader = false;
        var lineNo = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Length == 0) continue;
            switch (line[0])
            {
                case 'H':
                    if (!line.AsSpan(1).TrimStart().StartsWith("FAMAST"))
                        throw new BusinessRuleException("This is not a FAMAST fixed-asset extract (header record missing).");
                    sawHeader = true;
                    break;
                case 'D':
                    rows.Add(ParseDetail(line.PadRight(DetailLength), lineNo));
                    break;
                case 'T':
                    if (!int.TryParse(line.AsSpan(1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                        throw new BusinessRuleException($"Line {lineNo}: trailer record count is not numeric.");
                    trailerCount = count;
                    break;
                default:
                    warnings.Add($"Line {lineNo}: unknown record type '{line[0]}' - skipped.");
                    break;
            }
        }

        if (!sawHeader) throw new BusinessRuleException("This is not a FAMAST fixed-asset extract (header record missing).");
        if (trailerCount is null) throw new BusinessRuleException("The extract has no trailer record - the transfer may be incomplete.");
        if (trailerCount != rows.Count)
            throw new BusinessRuleException($"Control total mismatch: trailer says {trailerCount} records but {rows.Count} were read. Re-run the extract.");
        if (rows.Count == 0) throw new BusinessRuleException("The extract contains no asset records.");

        var farms = rows.Select(r => r.Farm).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        return new RegisterReadResult(rows, farms, warnings);
    }

    private static AssetRow ParseDetail(string line, int lineNo)
    {
        string Field(int start, int length) => line.Substring(start - 1, length);

        if (!long.TryParse(Field(2, 12), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code <= 0)
            throw new BusinessRuleException($"Line {lineNo}: asset code '{Field(2, 12).Trim()}' is not numeric.");

        var farm = Text.Clean(Field(14, 15), 50)
            ?? throw new BusinessRuleException($"Line {lineNo}: farm is blank.");

        return new AssetRow(
            code,
            farm,
            Text.Clean(Field(29, 5), 50),
            Text.Clean(Field(34, 40), 200),
            Text.Clean(Field(74, 60), 400),
            ParseZoned(Field(134, 11), lineNo),
            Text.Clean(Field(145, 50), 1000));
    }

    /// <summary>Zoned decimal with 2 implied decimals, e.g. "00000001250" = 12.50 and "0000000125-" = -1.25.</summary>
    internal static decimal? ParseZoned(string raw, int lineNo)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        var negative = raw.EndsWith('-');
        var digits = negative ? raw[..^1] : raw;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var units))
            throw new BusinessRuleException($"Line {lineNo}: quantity '{raw}' is not a zoned decimal.");
        var value = units / 100m;
        return negative ? -value : value;
    }
}
