namespace AceleCoreAgent.Sender;

public class FolderInfo
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string SourceName { get; set; } = "";
    public int BatchNo { get; set; }
    public int? ManufactureYear { get; set; }  // e.g. 2021 from "Batch 16(2021)"
    public string OriginalBatchLabel { get; set; } = ""; // raw folder name for notes
}

public class FileNameInfo
{
    public double? CapacityAh { get; set; }
    public int RackPosition { get; set; }
}

public static class FolderParser
{
    // Handles multiple folder structure formats:
    //
    // New agreed format:
    //   2026\09 - SEPTEMBER\M-KOPA\Batch 01\
    //
    // Old/current formats:
    //   9. SEPTEMBER\CELLS\M-KOPA\Batch 16(2021)\
    //   09 - SEPTEMBER\M-KOPA\Batch 01\
    //   SEPTEMBER\M-KOPA\Batch 01\
    //   M-KOPA\Batch 16\
    //
    public static FolderInfo? Parse(string folderPath, string watchRoot)
    {
        try
        {
            var relative = Path.GetRelativePath(watchRoot, folderPath);
            var parts = relative
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .ToArray();

            if (parts.Length < 2) return null;

            // ── Find the batch folder (last segment) ────────────────────────
            var batchSegment = parts[parts.Length - 1];
            var batchInfo = ParseBatchSegment(batchSegment);
            if (batchInfo == null) return null;

            // ── Find the source folder (second to last, skip "CELLS" etc.) ──
            string? sourceName = null;
            int sourceIndex = -1;
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                var part = parts[i].ToUpperInvariant();
                // Skip known non-source folders
                if (part == "CELLS" || part == "DATA" || part == "TESTING" ||
                    part == "TEST" || part == "FILES" || part == "BATCHES")
                    continue;

                // Skip year folders (4-digit numbers)
                if (System.Text.RegularExpressions.Regex.IsMatch(parts[i], @"^\d{4}$"))
                    continue;

                // Skip month folders
                if (IsMonthFolder(parts[i]))
                    continue;

                sourceName = parts[i];
                sourceIndex = i;
                break;
            }

            if (sourceName == null) return null;

            // ── Find month folder (somewhere before source) ──────────────────
            int month = DateTime.Now.Month; // default to current month
            for (int i = sourceIndex - 1; i >= 0; i--)
            {
                var m = ExtractMonth(parts[i]);
                if (m.HasValue) { month = m.Value; break; }
            }

            // ── Find year (4-digit folder or current year) ───────────────────
            int year = DateTime.Now.Year; // default to current year
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int y) && y > 2000 && y < 2100)
                {
                    year = y;
                    break;
                }
            }

            return new FolderInfo
            {
                Year = year,
                Month = month,
                SourceName = sourceName,
                BatchNo = batchInfo.Value.BatchNo,
                ManufactureYear = batchInfo.Value.ManufactureYear,
                OriginalBatchLabel = batchSegment,
            };
        }
        catch { return null; }
    }

    // Parses batch folder names like:
    //   "Batch 01"         → BatchNo=1, ManufactureYear=null
    //   "Batch 16(2021)"   → BatchNo=16, ManufactureYear=2021
    //   "Session 01"       → BatchNo=1, ManufactureYear=null
    //   "BATCH-3"          → BatchNo=3, ManufactureYear=null
    //   "01"               → BatchNo=1, ManufactureYear=null
    private static (int BatchNo, int? ManufactureYear)? ParseBatchSegment(string segment)
    {
        // Extract manufacture year from parentheses e.g. (2021)
        int? manufactureYear = null;
        var yearMatch = System.Text.RegularExpressions.Regex.Match(segment, @"\((\d{4})\)");
        if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int my))
            manufactureYear = my;

        // Extract batch number — first standalone number in the segment
        var numMatch = System.Text.RegularExpressions.Regex.Match(segment, @"(\d+)");
        if (!numMatch.Success) return null;

        int batchNo = int.Parse(numMatch.Groups[1].Value);

        // Sanity check — batch number should be reasonable
        if (batchNo > 9999) return null;

        return (batchNo, manufactureYear);
    }

    // Checks if a folder segment looks like a month folder
    // e.g. "9. SEPTEMBER", "09 - SEPTEMBER", "SEPTEMBER", "09"
    private static bool IsMonthFolder(string segment)
    {
        return ExtractMonth(segment).HasValue;
    }

    // Extracts month number from various month folder formats
    private static int? ExtractMonth(string segment)
    {
        var upper = segment.ToUpperInvariant();

        // Named months
        string[] months = {
            "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE",
            "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER"
        };
        for (int i = 0; i < months.Length; i++)
        {
            if (upper.Contains(months[i]))
                return i + 1;
        }

        // Numeric month only e.g. "09", "9"
        if (System.Text.RegularExpressions.Regex.IsMatch(segment.Trim(), @"^\d{1,2}$"))
        {
            if (int.TryParse(segment.Trim(), out int m) && m >= 1 && m <= 12)
                return m;
        }

        return null;
    }

    // Parses filenames like:
    //   MKP-3.2Ah-0001.xlsx
    //   DEL-2.8Ah-0201.xlsx
    //   MKP-0001.xlsx         (no capacity)
    //   0001.xlsx             (no source prefix)
    public static FileNameInfo? ParseFileName(string fileName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var parts = name.Split('-');
            if (parts.Length < 1) return null;

            // Last segment is always rack position
            var lastPart = parts[parts.Length - 1].Trim();
            if (!int.TryParse(lastPart, out int rackPos)) return null;

            // Look for capacity (contains "Ah")
            double? capacity = null;
            foreach (var part in parts)
            {
                var clean = part.Replace("Ah", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("ah", "")
                               .Trim();
                if (double.TryParse(clean, out double cap) && cap > 0 && cap < 100)
                {
                    capacity = cap;
                    break;
                }
            }

            return new FileNameInfo
            {
                CapacityAh = capacity,
                RackPosition = rackPos,
            };
        }
        catch { return null; }
    }

    // TC format: TC-MKP-260901-01-0001
    public static string GenerateTC(string sourceCode, DateTime date, int batchNo, int rackPosition)
    {
        string dateStr = date.ToString("yyMMdd");
        string batch = batchNo.ToString("D2");
        string pos = rackPosition.ToString("D4");
        return $"TC-{sourceCode}-{dateStr}-{batch}-{pos}";
    }
}