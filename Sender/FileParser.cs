using ExcelDataReader;
using AceleCoreAgent.Core;

namespace AceleCoreAgent.Sender;

public class ParsedTestResult
{
    public string CellSerial { get; set; } = "";
    public string OriginalSerial { get; set; } = "";
    public bool IsPack { get; set; }
    public DateTime TestDate { get; set; }
    public double CapacityAh { get; set; }
    public double? EnergyWh { get; set; }
    public double? DcirMohm { get; set; }
    public double? OnsetVoltage { get; set; }
    public double? EndVoltage { get; set; }
}

public static class FileParser
{
    private const double MAX_CELL_CHARGE_VOLTAGE = 6.0;

    static FileParser()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    // Safely converts any cell value to string — handles TimeSpan values
    // that ExcelDataReader creates when reading time-formatted cells
    private static string? SafeCellString(object? cellValue)
    {
        if (cellValue == null) return null;
        if (cellValue is TimeSpan ts) return ts.ToString(@"hh\:mm\:ss");
        if (cellValue is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
        return cellValue.ToString();
    }

    public static ParsedTestResult? Parse(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var dataset = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                UseColumnDataType = false,
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false }
            });

            var testSheet = dataset.Tables["test"];
            var stepSheet = dataset.Tables["step"];

            if (testSheet == null || stepSheet == null)
                return null;

            string? serial = null;
            DateTime testDate = DateTime.Now;

            for (int r = 0; r < Math.Min(10, testSheet.Rows.Count); r++)
            {
                for (int c = 0; c < Math.Min(15, testSheet.Columns.Count); c++)
                {
                    var cellVal = SafeCellString(testSheet.Rows[r][c])?.Trim().ToLower();

                    if (cellVal == "barcode" && c + 2 < testSheet.Columns.Count)
                    {
                        var val = SafeCellString(testSheet.Rows[r][c + 2])?.Trim();
                        if (!string.IsNullOrEmpty(val) && val != "-")
                            serial = val;
                    }

                    if ((cellVal == "start time" || cellVal == "starting time") && c + 2 < testSheet.Columns.Count)
                    {
                        var dateStr = SafeCellString(testSheet.Rows[r][c + 2])?.Trim();
                        if (!string.IsNullOrEmpty(dateStr) &&
                            !System.Text.RegularExpressions.Regex.IsMatch(dateStr, @"^\d{1,2}:\d{2}:\d{2}$") &&
                            DateTime.TryParse(dateStr, out var dt))
                        {
                            testDate = dt;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(serial))
                serial = Path.GetFileNameWithoutExtension(filePath);

            if (stepSheet.Rows.Count < 2) return null;
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headerRow = stepSheet.Rows[0];
            for (int c = 0; c < stepSheet.Columns.Count; c++)
            {
                var h = SafeCellString(headerRow[c])?.Trim();
                if (!string.IsNullOrEmpty(h))
                    headers[h] = c;
            }

            int? stepTypeCol = FindCol(headers, "Step Type");
            int? capCol = FindCol(headers, "Capacity(Ah)");
            int? energyCol = FindCol(headers, "Energy(Wh)");
            int? onsetCol = FindCol(headers, "Oneset Volt.(V)", "Start Volt(V)");
            int? endVoltCol = FindCol(headers, "End Voltage(V)", "End Volt(V)");
            int? chgEndCol = FindCol(headers, "End of Chg.Volt.(V)", "Chg End Volt(V)");
            int? dcirCol = FindCol(headers, "DCIR(mΩ)");

            if (stepTypeCol == null || capCol == null) return null;

            double peakVoltage = 0;
            int dischargeRowIndex = -1;

            for (int r = 1; r < stepSheet.Rows.Count; r++)
            {
                var row = stepSheet.Rows[r];
                var stepType = SafeCellString(row[stepTypeCol.Value])?.Trim().ToLower() ?? "";

                foreach (var col in new[] { onsetCol, endVoltCol, chgEndCol }.Where(c => c.HasValue))
                {
                    if (double.TryParse(SafeCellString(row[col!.Value]), out var v) && v > peakVoltage)
                        peakVoltage = v;
                }

                if (stepType.Contains("dchg"))
                    dischargeRowIndex = r;
            }

            var isPack = peakVoltage > MAX_CELL_CHARGE_VOLTAGE;

            if (dischargeRowIndex < 0)
                return new ParsedTestResult
                {
                    CellSerial = serial,
                    OriginalSerial = serial,
                    IsPack = isPack,
                    TestDate = testDate
                };

            var dRow = stepSheet.Rows[dischargeRowIndex];
            double.TryParse(SafeCellString(dRow[capCol.Value]), out var capacityAh);
            double.TryParse(energyCol.HasValue ? SafeCellString(dRow[energyCol.Value]) : null, out var energyWh);
            double.TryParse(onsetCol.HasValue ? SafeCellString(dRow[onsetCol.Value]) : null, out var onset);
            double.TryParse(endVoltCol.HasValue ? SafeCellString(dRow[endVoltCol.Value]) : null, out var endV);
            double.TryParse(dcirCol.HasValue ? SafeCellString(dRow[dcirCol.Value]) : null, out var dcir);

            return new ParsedTestResult
            {
                CellSerial = serial,
                OriginalSerial = serial,
                IsPack = isPack,
                TestDate = testDate,
                CapacityAh = capacityAh,
                EnergyWh = energyWh > 0 ? energyWh : null,
                OnsetVoltage = onset > 0 ? onset : null,
                EndVoltage = endV > 0 ? endV : null,
                DcirMohm = dcir > 0 ? dcir : null,
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"Parse error: {Path.GetFileName(filePath)}: {ex.Message}", Logger.LogLevel.Error);
            return null;
        }
    }

    private static int? FindCol(Dictionary<string, int> headers, params string[] names)
    {
        foreach (var name in names)
        {
            if (headers.TryGetValue(name, out var col)) return col;
            var fuzzy = headers.Keys.FirstOrDefault(k =>
                k.Replace(".", "").Replace("_", " ").ToLower().Contains(
                    name.Replace(".", "").Replace("_", " ").ToLower()));
            if (fuzzy != null) return headers[fuzzy];
        }
        return null;
    }
}