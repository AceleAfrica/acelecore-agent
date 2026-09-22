namespace AceleCoreAgent.Sender;

public class FolderInfo
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string SourceName { get; set; } = "";
    public int BatchNo { get; set; }
}

public class FileNameInfo
{
    public double? CapacityAh { get; set; }
    public int RackPosition { get; set; }
}

public static class FolderParser
{
    // Parses: ...2026\09 - SEPTEMBER\M-KOPA\Batch 01\
    public static FolderInfo? Parse(string folderPath, string watchRoot)
    {
        try
        {
            var relative = Path.GetRelativePath(watchRoot, folderPath);
            var parts = relative.Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

            // Find year segment
            int yearIndex = -1;
            int year = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (int.TryParse(parts[i], out int y) && y > 2000 && y < 2100)
                {
                    year = y;
                    yearIndex = i;
                    break;
                }
            }

            if (yearIndex < 0 || yearIndex + 3 >= parts.Length)
                return null;

            // Month segment e.g. "09 - SEPTEMBER"
            var monthSegment = parts[yearIndex + 1];
            var monthMatch = System.Text.RegularExpressions.Regex.Match(monthSegment, @"^(\d+)");
            if (!monthMatch.Success) return null;
            int month = int.Parse(monthMatch.Groups[1].Value);

            // Source folder e.g. "M-KOPA"
            string sourceName = parts[yearIndex + 2].Trim();

            // Batch folder e.g. "Batch 01", "Session 01"
            var batchSegment = parts[yearIndex + 3];
            var batchMatch = System.Text.RegularExpressions.Regex.Match(batchSegment, @"(\d+)");
            int batchNo = batchMatch.Success ? int.Parse(batchMatch.Value) : 1;

            return new FolderInfo
            {
                Year = year,
                Month = month,
                SourceName = sourceName,
                BatchNo = batchNo,
            };
        }
        catch { return null; }
    }

    // Parses: MKP-3.2Ah-0001.xlsx
    public static FileNameInfo? ParseFileName(string fileName)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var parts = name.Split('-');
            if (parts.Length < 3) return null;

            var capacityStr = parts[1].Replace("Ah", "", StringComparison.OrdinalIgnoreCase).Trim();
            var rackStr = parts[parts.Length - 1].Trim();

            return new FileNameInfo
            {
                CapacityAh = double.TryParse(capacityStr, out var cap) ? cap : null,
                RackPosition = int.TryParse(rackStr, out var pos) ? pos : 0,
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