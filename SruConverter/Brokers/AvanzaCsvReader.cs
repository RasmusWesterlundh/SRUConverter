using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using ExcelDataReader;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Reads Avanza's K4 export — accepts both the canonical CSV format and the raw .xlsx
/// export directly from Avanza. No manual pre-conversion step required.
///
/// CSV format (8 columns, header row):
///   Sektion, Antal, Beteckning, Datum, Försäljningspris, Omkostnadsbelopp, Vinst, Förlust
///
/// xlsx format: one sheet per K4 section ("Sektion A", "Sektion D", …).
///   Columns are mapped by name; extra columns (Schablonmetoden, etc.) are ignored.
///   The Sektion value is derived from the sheet name.
///
/// All amounts are in SEK — no currency conversion needed.
/// Multiple files can be supplied for users with more than one Avanza account.
/// </summary>
public class AvanzaCsvReader : IBrokerReader
{
    public string   BrokerName          => "Avanza";
    public string[] SupportedExtensions => [".csv", ".xlsx"];
    public string   FilePrompt          => "Avanza K4 export (.csv or .xlsx)";

    public string HelpText =>
        "Log in to Avanza and navigate to Skatt (Tax) → K4-underlag. " +
        "Download your K4 document as an Excel file (.xlsx). " +
        "You can provide the .xlsx directly — no manual conversion needed. " +
        "If you have multiple Avanza accounts, download a separate K4 file for each " +
        "and add them one by one when prompted.";

    public string HelpUrl => "https://www.avanza.se/min-profil/deklaration-kontobesked/avanza-k4.html";

    private static readonly string[] CanonicalHeaders =
        ["Sektion", "Antal", "Beteckning", "Datum",
         "Försäljningspris", "Omkostnadsbelopp", "Vinst", "Förlust"];

    // ── Validation ────────────────────────────────────────────────────────────

    public Task<string?> ValidateFileAsync(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            return ext == ".xlsx" ? ValidateXlsxAsync(filePath) : ValidateCsvAsync(filePath);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Could not read file: {ex.Message}");
        }
    }

    private static Task<string?> ValidateCsvAsync(string path)
    {
        var firstLine = File.ReadLines(path, System.Text.Encoding.UTF8).FirstOrDefault() ?? "";
        var cols = CsvHelper.SplitLine(firstLine);

        // Accept if the first row has the canonical 8 headers (in order)
        var headers = cols.Select(c => c.Trim()).ToList();
        for (int i = 0; i < CanonicalHeaders.Length; i++)
        {
            if (i >= headers.Count || !headers[i].Equals(CanonicalHeaders[i], StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<string?>(
                    $"Unexpected CSV header at column {i + 1}. " +
                    $"Expected '{CanonicalHeaders[i]}', got '{(i < headers.Count ? headers[i] : "(missing)")}'.\n" +
                    $"Expected headers: {string.Join(", ", CanonicalHeaders)}");
        }
        return Task.FromResult<string?>(null);
    }

    private static Task<string?> ValidateXlsxAsync(string path)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var ds = reader.AsDataSet(new ExcelDataSetConfiguration { UseColumnDataType = false });

        var sektionSheets = ds.Tables.Cast<DataTable>()
            .Where(t => ParseSektionFromSheetName(t.TableName) != null)
            .ToList();

        if (sektionSheets.Count == 0)
            return Task.FromResult<string?>(
                "No sheets named 'Sektion A', 'Sektion D', etc. found. " +
                "Is this an Avanza K4 export?");

        foreach (var sheet in sektionSheets)
        {
            if (sheet.Rows.Count < 2)
                return Task.FromResult<string?>(
                    $"Sheet '{sheet.TableName}' has no data rows.");
        }

        return Task.FromResult<string?>(null);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public Task<List<K4Row>> GetDirectRowsAsync(IEnumerable<string> filePaths, RiksbankService _)
    {
        var rows = new List<K4Row>();
        foreach (var path in filePaths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".xlsx")
                rows.AddRange(ReadXlsx(path));
            else
                rows.AddRange(ReadCsv(path));
        }
        return Task.FromResult(rows);
    }

    private static IEnumerable<K4Row> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var cols = CsvHelper.SplitLine(line);
            if (cols.Count < 8) continue;

            if (!decimal.TryParse(cols[1], NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var antal)) continue;

            yield return new K4Row
            {
                Sektion          = cols[0].Trim().ToUpperInvariant(),
                Antal            = antal,
                Beteckning       = cols[2].Trim(),
                Datum            = cols[3].Trim(),
                Forsaljningspris = CsvHelper.ParseLong(cols[4]),
                Omkostnadsbelopp = CsvHelper.ParseLong(cols[5]),
                Vinst            = CsvHelper.ParseLong(cols[6]),
                Forlust          = CsvHelper.ParseLong(cols[7]),
            };
        }
    }

    private static IEnumerable<K4Row> ReadXlsx(string path)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var ds = reader.AsDataSet(new ExcelDataSetConfiguration { UseColumnDataType = false });

        foreach (DataTable sheet in ds.Tables)
        {
            var sektion = ParseSektionFromSheetName(sheet.TableName);
            if (sektion == null) continue;

            // First row = headers; build name→index map
            if (sheet.Rows.Count < 1) continue;
            var headerRow = sheet.Rows[0];
            var colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < sheet.Columns.Count; c++)
            {
                var h = headerRow[c]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(h)) colIdx[h] = c;
            }

            // Verify required columns exist
            string[] required = ["Antal", "Beteckning", "Datum",
                                  "Försäljningspris", "Omkostnadsbelopp", "Vinst", "Förlust"];
            var missing = required.Where(r => !colIdx.ContainsKey(r)).ToList();
            if (missing.Count > 0) continue; // skip unexpected sheet layout

            for (int r = 1; r < sheet.Rows.Count; r++)
            {
                var row = sheet.Rows[r];
                var antalStr = row[colIdx["Antal"]]?.ToString()?.Trim() ?? "";
                if (!decimal.TryParse(antalStr, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var antal)) continue;

                yield return new K4Row
                {
                    Sektion          = sektion,
                    Antal            = antal,
                    Beteckning       = row[colIdx["Beteckning"]]?.ToString()?.Trim() ?? "",
                    Datum            = row[colIdx["Datum"]]?.ToString()?.Trim() ?? "",
                    Forsaljningspris = ParseXlsxLong(row[colIdx["Försäljningspris"]]),
                    Omkostnadsbelopp = ParseXlsxLong(row[colIdx["Omkostnadsbelopp"]]),
                    Vinst            = ParseXlsxLong(row[colIdx["Vinst"]]),
                    Forlust          = ParseXlsxLong(row[colIdx["Förlust"]]),
                };
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex SektionPattern =
        new(@"\bSektion\s+([A-Za-z])\b", RegexOptions.IgnoreCase);

    private static string? ParseSektionFromSheetName(string name)
    {
        var m = SektionPattern.Match(name);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static long ParseXlsxLong(object? cell)
    {
        if (cell == null) return 0;
        var s = cell.ToString()?.Trim() ?? "";
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return (long)Math.Round(d);
        return 0;
    }
}
