using System.Globalization;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Reads Revolut crypto export files and emits TradeEvents for the shared genomsnittsmetoden.
///
/// Supports two file formats (auto-detected by header):
///
/// A. crypto-account-statement_*.csv  (raw transactions, values already in SEK)
///    Header: Symbol,Type,Quantity,Price,Value,Fees,Date
///    Handles: Buy, Sell, Learn reward; skips Send and other types.
///
/// B. trading-account-statement_*.csv  (or old Consolidated Statement)
///    Header contains both "Date acquired" and "Date sold" columns.
///    Emits Sell events with FallbackCostSek from Revolut's own cost basis.
///    Rows for assets that also appear in a crypto-account Sell are skipped
///    to avoid double-counting.
///
/// Providing BOTH files gives the most accurate results: the crypto-account file
/// covers acquisitions (Buy) and explicit disposals (Sell), while the trading-account
/// file covers disposals of assets that were transferred out (Send) rather than sold.
///
/// Per Skatteverket, all crypto assets are reported in K4 Sektion D.
/// </summary>
public class RevolutCsvReader : IBrokerReader
{
    public string   BrokerName          => "Revolut";
    public string[] SupportedExtensions => [".csv"];
    public string   FilePrompt          => "Crypto Account Statement and/or Trading Account Statement CSV";

    public string HelpText =>
        "Open the Revolut app and go to Profile -> Documents & Statements -> New Export.\n\n" +
        "  * Crypto Account Statement (crypto-account-statement_*.csv): " +
        "Select 'Account Statement', choose 'Crypto' as the account type. " +
        "For the date range, include your full purchase history for any asset you sold in the " +
        "declared year — prior purchases determine the average cost basis (genomsnittsmetoden), " +
        "even if those purchases were in earlier tax years. Prior-year sales are automatically " +
        "excluded from the K4 output. Export as CSV. " +
        "This file contains raw Buy, Sell, Send, and Learn reward events with values in SEK. " +
        "Send events with a non-zero value are treated as taxable disposals (payment with crypto).\n\n" +
        "  * Trading Account Statement (trading-account-statement_*.csv): " +
        "Same path but choose 'Trading Account' as the account type. " +
        "This file lists disposal events with Revolut's own pre-computed cost basis, used as a " +
        "fallback when the shared average-cost history is insufficient. " +
        "Assets already covered by Sell or Send events in the crypto-account file are " +
        "automatically skipped to avoid double-counting.\n\n" +
        "Providing BOTH files gives the most accurate results: the crypto-account file " +
        "establishes the full acquisition history, while the trading-account file handles " +
        "disposals of assets like BTC that were transferred out (Send) without an explicit Sell.";

    public string HelpUrl => "https://www.revolut.com/help/profile/documents-and-statements/how-do-i-get-a-statement/";

    // -- Validation ------------------------------------------------------------

    public Task<string?> ValidateFileAsync(string filePath)
    {
        try
        {
            var lines = File.ReadLines(filePath, System.Text.Encoding.UTF8).Take(50).ToList();
            var firstLine = lines.FirstOrDefault() ?? "";

            if (IsCryptoAccountHeader(firstLine))
                return Task.FromResult<string?>(null);

            if (lines.Any(l =>
                    l.Contains("Date acquired", StringComparison.OrdinalIgnoreCase) &&
                    l.Contains("Date sold",     StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult<string?>(null);

            // Old consolidated statement: "Date acquired," starts its own line
            if (lines.Any(l => l.TrimStart().StartsWith("Date acquired,", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(
                "Could not recognise this as a Revolut export. " +
                "Expected a Crypto Account Statement (Symbol,Type,Quantity,Price,Value,Fees,Date header) " +
                "or a Trading Account Statement / Consolidated Statement (with 'Date acquired' and 'Date sold' columns).");
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Could not read file: {ex.Message}");
        }
    }

    // -- GetTradeEventsAsync ---------------------------------------------------

    public async Task<List<TradeEvent>> GetTradeEventsAsync(
        IEnumerable<string> filePaths, RiksbankService riksbank)
    {
        var events                  = new List<TradeEvent>();
        var loggedKeys              = new HashSet<string>();
        var assetsWithExplicitSells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cryptoFiles  = new List<string>();
        var tradingFiles = new List<string>();

        // Classify each file by format.
        foreach (var path in filePaths)
        {
            var firstLine = File.ReadLines(path, System.Text.Encoding.UTF8).FirstOrDefault() ?? "";
            if (IsCryptoAccountHeader(firstLine))
                cryptoFiles.Add(path);
            else
                tradingFiles.Add(path);
        }

        // Pass 1: process crypto-account files first, building assetsWithExplicitSells.
        foreach (var path in cryptoFiles)
        {
            var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            var evts  = ParseCryptoAccountFile(lines, assetsWithExplicitSells);
            events.AddRange(evts);
        }

        // Pass 2: process trading-account files, skipping assets already covered above.
        foreach (var path in tradingFiles)
        {
            var lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            var evts  = await ParseTradingAccountFile(lines, assetsWithExplicitSells, riksbank, loggedKeys);
            events.AddRange(evts);
        }

        return events;
    }

    // -- Crypto account statement parser (Format A) ----------------------------

    private static List<TradeEvent> ParseCryptoAccountFile(
        string[] lines,
        HashSet<string> assetsWithExplicitSells)
    {
        var events = new List<TradeEvent>();
        var colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var cols = CsvHelper.SplitLine(raw.Trim());
            if (cols.Count == 0) continue;

            if (colIdx.Count == 0)
            {
                if (!IsCryptoAccountHeader(raw.Trim())) continue;
                for (int i = 0; i < cols.Count; i++)
                    colIdx[cols[i].Trim()] = i;
                continue;
            }

            string Get(string col) =>
                colIdx.TryGetValue(col, out int i) && i < cols.Count ? cols[i].Trim() : "";

            var symbol   = Get("Symbol").ToUpperInvariant();
            var type     = Get("Type").Trim();
            var qtyStr   = Get("Quantity");
            var valueStr = Get("Value");
            var feesStr  = Get("Fees");
            var dateStr  = Get("Date");

            if (string.IsNullOrEmpty(symbol)) continue;

            if (!decimal.TryParse(qtyStr.Replace(",", ""), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var qty) || qty == 0m)
                continue;

            if (!TryParseCryptoDateTime(dateStr, out var timestamp))
            {
                Console.WriteLine($"  WARNING (Revolut): Could not parse date '{dateStr}', skipping row.");
                continue;
            }

            var typeNorm = type.Trim();

            if (typeNorm.Equals("Learn reward", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"  NOTE (Revolut): Learn reward of {qty} {symbol} on " +
                    $"{DateOnly.FromDateTime(timestamp):yyyy-MM-dd} — cost basis set to 0. " +
                    "Declare this amount separately as income (Tjänst) in your tax return.");
                events.Add(new TradeEvent
                {
                    Timestamp = timestamp,
                    Asset     = symbol,
                    Quantity  = qty,
                    ValueSek  = 0m,
                    FeeSek    = 0m,
                    Kind      = TradeKind.Buy,
                    Source    = "Revolut",
                });
            }
            else if (typeNorm.Equals("Buy", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(new TradeEvent
                {
                    Timestamp = timestamp,
                    Asset     = symbol,
                    Quantity  = qty,
                    ValueSek  = ParseSekAmount(valueStr),
                    FeeSek    = ParseSekAmount(feesStr),
                    Kind      = TradeKind.Buy,
                    Source    = "Revolut",
                });
            }
            else if (typeNorm.Equals("Sell", StringComparison.OrdinalIgnoreCase))
            {
                assetsWithExplicitSells.Add(symbol);
                events.Add(new TradeEvent
                {
                    Timestamp = timestamp,
                    Asset     = symbol,
                    Quantity  = qty,
                    ValueSek  = ParseSekAmount(valueStr),
                    FeeSek    = ParseSekAmount(feesStr),
                    Kind      = TradeKind.Sell,
                    Source    = "Revolut",
                });
            }
            else if (typeNorm.Equals("Send", StringComparison.OrdinalIgnoreCase))
            {
                // Per Skatteverket: paying with crypto is a taxable disposal.
                // A "Send" can also be a transfer to your own wallet (not taxable).
                // We treat it as taxable when Revolut reports a non-zero Value.
                // If Value is 0 we assume it's an internal transfer and skip it.
                var sendValue = ParseSekAmount(valueStr);
                if (sendValue > 0m)
                {
                    Console.WriteLine(
                        $"  NOTE (Revolut): 'Send' of {qty} {symbol} on " +
                        $"{DateOnly.FromDateTime(timestamp):yyyy-MM-dd} treated as taxable disposal. " +
                        "If this was a transfer to your own wallet, remove it manually from the SRU output.");
                    assetsWithExplicitSells.Add(symbol);
                    events.Add(new TradeEvent
                    {
                        Timestamp = timestamp,
                        Asset     = symbol,
                        Quantity  = qty,
                        ValueSek  = sendValue,
                        FeeSek    = ParseSekAmount(feesStr),
                        Kind      = TradeKind.Sell,
                        Source    = "Revolut",
                    });
                }
                // Value == 0: internal transfer, skip silently.
            }
            // Other types -> skip
        }

        return events;
    }

    // -- Trading account statement parser (Format B) ---------------------------

    private async Task<List<TradeEvent>> ParseTradingAccountFile(
        string[] lines,
        HashSet<string> assetsWithExplicitSells,
        RiksbankService riksbank,
        HashSet<string> loggedKeys)
    {
        var events = new List<TradeEvent>();
        var colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var cols = CsvHelper.SplitLine(trimmed);
            if (cols.Count == 0) continue;

            // Detect header line: must contain both "Date acquired" and "Date sold".
            if (colIdx.Count == 0)
            {
                if (trimmed.Contains("Date acquired", StringComparison.OrdinalIgnoreCase) &&
                    trimmed.Contains("Date sold",     StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < cols.Count; i++)
                        colIdx[cols[i].Trim()] = i;
                }
                continue;
            }

            string Get(string col) =>
                colIdx.TryGetValue(col, out int i) && i < cols.Count ? cols[i].Trim() : "";

            // Flexible column name detection.
            var dateSoldStr      = Get("Date sold");
            var asset            = (Get("Symbol") is { Length: > 0 } sym ? sym
                                    : Get("Token name")).ToUpperInvariant();
            var qtyStr           = Get("Quantity") is { Length: > 0 } q ? q : Get("Qty");
            var proceedsStr      = Get("Gross proceeds");
            var costStr          = Get("Cost basis");
            var feesStr          = Get("Fees");
            var currencyCode     = Get("Currency");

            if (string.IsNullOrEmpty(asset) || string.IsNullOrEmpty(dateSoldStr)) continue;

            // Skip if this asset had explicit Sell events in the crypto-account file.
            if (assetsWithExplicitSells.Contains(asset)) continue;

            if (!DateOnly.TryParseExact(dateSoldStr, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateSold))
            {
                Console.WriteLine($"  WARNING (Revolut): Could not parse 'Date sold' '{dateSoldStr}', skipping.");
                continue;
            }

            if (!decimal.TryParse(qtyStr.Replace(",", ""), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var qty) || qty == 0m)
                continue;

            if (!decimal.TryParse(proceedsStr.Replace(",", ""), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var proceeds))
                continue;

            decimal.TryParse(costStr.Replace(",", ""), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var costBasis);

            decimal.TryParse(feesStr.Replace(",", ""), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var fees);

            if (string.IsNullOrEmpty(currencyCode)) currencyCode = "USD";

            decimal rate = await GetRate(riksbank, currencyCode, dateSold, loggedKeys);

            var timestamp    = dateSold.ToDateTime(TimeOnly.MinValue);
            var valueSek     = proceeds  * rate;
            var feeSek       = fees      * rate;
            var fallbackCost = (long)Math.Round(costBasis * rate, MidpointRounding.AwayFromZero);

            events.Add(new TradeEvent
            {
                Timestamp       = timestamp,
                Asset           = asset,
                Quantity        = qty,
                ValueSek        = valueSek,
                FeeSek          = feeSek,
                Kind            = TradeKind.Sell,
                Source          = "Revolut",
                FallbackCostSek = fallbackCost,
            });
        }

        return events;
    }

    // -- Helpers ---------------------------------------------------------------

    private static bool IsCryptoAccountHeader(string line) =>
        line.Contains("Symbol",   StringComparison.OrdinalIgnoreCase) &&
        line.Contains("Type",     StringComparison.OrdinalIgnoreCase) &&
        line.Contains("Quantity", StringComparison.OrdinalIgnoreCase) &&
        line.Contains("Fees",     StringComparison.OrdinalIgnoreCase) &&
        line.Contains("Date",     StringComparison.OrdinalIgnoreCase) &&
        !line.Contains("Date acquired", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Strips the " SEK" suffix, commas, narrow no-break spaces (U+202F), then parses as decimal.
    /// </summary>
    private static decimal ParseSekAmount(string s)
    {
        s = s.Replace("\u202F", "")
             .Replace(" SEK", "", StringComparison.OrdinalIgnoreCase)
             .Replace(",", "")
             .Trim();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
    }

    private static readonly string[] CryptoDateFormats =
    [
        "MMM d, yyyy, h:mm:ss tt",
        "MMM dd, yyyy, h:mm:ss tt",
    ];

    private static bool TryParseCryptoDateTime(string s, out DateTime dt)
    {
        // Replace narrow no-break space (U+202F) before AM/PM with a regular space.
        s = s.Replace('\u202F', ' ');
        foreach (var fmt in CryptoDateFormats)
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt)) return true;
        dt = default;
        return false;
    }

    private static async Task<decimal> GetRate(
        RiksbankService riksbank,
        string currency,
        DateOnly date,
        HashSet<string> loggedKeys)
    {
        if (currency is "SEK") return 1m;

        var rate   = await riksbank.GetRateForDateAsync(currency, date);
        var logKey = $"{currency}:{date:yyyy-MM}";
        if (loggedKeys.Add(logKey))
            Console.WriteLine($"  {currency}/SEK {date:yyyy-MM}: {rate}");
        return rate;
    }
}