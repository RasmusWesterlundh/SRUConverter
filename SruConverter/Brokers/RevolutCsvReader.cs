using System.Globalization;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Reads Revolut's "Consolidated Statement" CSV export for crypto trades.
/// The file contains a summary block at the top followed by a
/// "Transactions for Crypto" section with one row per disposal event.
///
/// Expected columns (after the "Date acquired," header line):
///   Date acquired, Date sold, Token name, Qty, Cost basis, Gross proceeds, Gross PnL
///
/// Amounts carry a currency prefix (e.g. "$1,234.56", "€500") and are detected
/// per-row, so mixed-currency reports are handled correctly.
/// Foreign amounts are converted to SEK using Riksbanken's daily rate for the
/// sale date; falls back to the monthly average if the date is a non-banking day.
///
/// Per Skatteverket, crypto is reported in K4 Sektion D.
/// </summary>
public class RevolutCsvReader : IBrokerReader
{
    public string   BrokerName          => "Revolut";
    public string[] SupportedExtensions => [".csv"];
    public string   FilePrompt          => "Consolidated Statement CSV export (consolidated-statement_*.csv)";

    public string HelpText =>
        "Open the Revolut app and go to Profile (bottom right) → Documents → Statements. " +
        "Tap 'Consolidated statement', select the tax year, and choose CSV as the format. " +
        "The exported file is named 'consolidated-statement_YYYY-MM-DD_YYYY-MM-DD_*.csv'. " +
        "Note: only crypto trades are imported from this statement; " +
        "other asset types are not yet supported.";

    public string HelpUrl => "https://help.revolut.com/en-SE/help/profile/documents-and-statements/how-do-i-get-a-statement/";

    public Task<string?> ValidateFileAsync(string filePath)
    {
        try
        {
            var found = File.ReadLines(filePath, System.Text.Encoding.UTF8)
                .Take(50)
                .Any(l => l.TrimStart().StartsWith("Date acquired,", StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(found
                ? null
                : (string?)"Could not find a 'Date acquired,' header in the first 50 lines. " +
                           "Is this a Revolut Consolidated Statement CSV export?");
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Could not read file: {ex.Message}");
        }
    }

    public async Task<List<K4Row>> ReadAsync(IEnumerable<string> filePaths, RiksbankService riksbank)
    {
        var rows       = new List<K4Row>();
        var loggedKeys = new HashSet<string>(); // suppress duplicate rate log lines

        foreach (var path in filePaths)
        {
            var lines           = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            bool inTransactions = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("Date acquired,"))
                {
                    inTransactions = true;
                    continue;
                }

                if (!inTransactions) continue;

                var cols = CsvHelper.SplitLine(trimmed);
                if (cols.Count < 7) continue;

                var dateSoldStr = cols[1].Trim();
                var token       = cols[2].Trim().ToUpperInvariant();
                var qty         = CsvHelper.ParseDecimal(cols[3]);

                if (!CsvHelper.TryParseAmount(cols[4], out var cost,     out var costCurrency) ||
                    !CsvHelper.TryParseAmount(cols[5], out var proceeds, out var proceedsCurrency))
                {
                    Console.WriteLine($"  WARNING: Could not parse amounts for {token}, skipping row.");
                    continue;
                }

                if (costCurrency != proceedsCurrency)
                    Console.WriteLine($"  WARNING: {token} — cost in {costCurrency} but proceeds in " +
                                      $"{proceedsCurrency}. Using proceeds currency for conversion.");

                var currency = proceedsCurrency;

                if (!TryParseDate(dateSoldStr, out var dateSold))
                {
                    Console.WriteLine($"  WARNING: Could not parse date '{dateSoldStr}', skipping row.");
                    continue;
                }

                // Get SEK rate for this row's specific currency and sale date.
                // RiksbankService bulk-caches the full year on first call per currency, so this is fast.
                decimal rate;
                if (currency == "SEK")
                {
                    rate = 1m;
                }
                else
                {
                    rate = await riksbank.GetRateForDateAsync(currency, dateSold);
                    var logKey = $"{currency}:{dateSold:yyyy-MM}";
                    if (loggedKeys.Add(logKey))
                        Console.WriteLine($"  {currency}/SEK {dateSold:yyyy-MM}: {rate}");
                }

                var costSek     = (long)Math.Round(cost     * rate, MidpointRounding.AwayFromZero);
                var proceedsSek = (long)Math.Round(proceeds * rate, MidpointRounding.AwayFromZero);
                var vinst       = proceedsSek > costSek ? proceedsSek - costSek : 0L;
                var forlust     = costSek > proceedsSek ? costSek - proceedsSek : 0L;

                rows.Add(new K4Row
                {
                    Sektion          = "D",  // Skatteverket: crypto → K4 Sektion D
                    Antal            = qty,
                    Beteckning       = token,
                    Datum            = dateSold.ToString("yyyy-MM-dd"),
                    Forsaljningspris = proceedsSek,
                    Omkostnadsbelopp = costSek,
                    Vinst            = vinst,
                    Forlust          = forlust,
                });
            }
        }

        return rows;
    }

    // ── Date parsing ─────────────────────────────────────────────────────────

    private static readonly string[] DateFormats =
    [
        "MMM d, yyyy",    // Revolut: "Jan 17, 2025"
        "MMM dd, yyyy",   // Revolut: "Jan 17, 2025" (zero-padded day)
        "yyyy-MM-dd",     // ISO 8601
        "dd/MM/yyyy",     // European
        "M/d/yyyy",       // US
        "d/M/yyyy",       // EU alternative
    ];

    private static bool TryParseDate(string s, out DateOnly date)
    {
        foreach (var fmt in DateFormats)
            if (DateOnly.TryParseExact(s, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date)) return true;
        date = default;
        return false;
    }
}
