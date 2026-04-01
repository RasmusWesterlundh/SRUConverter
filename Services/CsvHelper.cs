using System.Globalization;

namespace SruConverter.Services;

/// <summary>
/// Shared CSV parsing utilities used by all broker readers.
/// </summary>
public static class CsvHelper
{
    /// <summary>
    /// Splits a single CSV line respecting RFC 4180 quoting rules
    /// (quoted fields may contain commas and escaped double-quotes).
    /// </summary>
    public static List<string> SplitLine(string line)
    {
        var result = new List<string>();
        int pos = 0;
        while (pos <= line.Length)
        {
            if (pos == line.Length) { result.Add(""); break; }

            if (line[pos] == '"')
            {
                pos++;
                var sb = new System.Text.StringBuilder();
                while (pos < line.Length)
                {
                    if (line[pos] == '"')
                    {
                        pos++;
                        if (pos < line.Length && line[pos] == '"') { sb.Append('"'); pos++; }
                        else break;
                    }
                    else sb.Append(line[pos++]);
                }
                result.Add(sb.ToString());
                if (pos < line.Length && line[pos] == ',') pos++;
            }
            else
            {
                var end = line.IndexOf(',', pos);
                if (end < 0) { result.Add(line[pos..]); break; }
                result.Add(line[pos..end]);
                pos = end + 1;
            }
        }
        return result;
    }

    /// <summary>
    /// Parses an amount string that may carry a currency symbol or ISO code prefix/suffix.
    /// Supported formats: "$1,234.56", "€500", "1234.56 SEK", "USD 1234", "kr 500"
    /// Returns the numeric value and detected ISO 4217 currency code.
    /// Defaults to "USD" if no recognisable currency marker is found.
    /// </summary>
    public static bool TryParseAmount(string raw, out decimal amount, out string currency)
    {
        amount   = 0;
        currency = "USD"; // safe default for Revolut / USD-denominated brokers

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim();

        // Symbol prefixes (longest match first to avoid "kr" eating before 3-char ISO codes)
        foreach (var (sym, code) in CurrencySymbols)
        {
            if (s.StartsWith(sym, StringComparison.OrdinalIgnoreCase))
            {
                currency = code;
                s = s[sym.Length..].Trim();
                break;
            }
        }

        // ISO code prefix: "USD 1234" or "USD1234"
        if (currency == "USD" && s.Length > 3)
        {
            var candidate = s[..3].ToUpperInvariant();
            if (IsKnownIsoCode(candidate)) { currency = candidate; s = s[3..].Trim(); }
        }

        // ISO code suffix: "1234 SEK" or "1234SEK"
        if (s.Length > 3)
        {
            var candidate = s[^3..].ToUpperInvariant();
            if (IsKnownIsoCode(candidate)) { currency = candidate; s = s[..^3].Trim(); }
        }

        // Strip thousand separators and parse
        s = s.Replace(",", "").Trim();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }

    /// <summary>
    /// Parses a plain decimal string (no currency prefix).
    /// Strips commas used as thousands separators.
    /// </summary>
    public static decimal ParseDecimal(string s)
    {
        var cleaned = s.Replace(",", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>
    /// Parses a long integer from a decimal string, rounding to nearest.
    /// </summary>
    public static long ParseLong(string s)
    {
        if (decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return (long)Math.Round(d);
        return 0;
    }

    // ── Private ───────────────────────────────────────────────────────────────

    // Symbol → ISO code. Longer symbols must come before shorter ones that are prefixes of them.
    private static readonly (string Symbol, string Code)[] CurrencySymbols =
    [
        ("NOK", "NOK"), ("DKK", "DKK"), ("SEK", "SEK"), ("EUR", "EUR"),
        ("USD", "USD"), ("GBP", "GBP"), ("CHF", "CHF"), ("JPY", "JPY"),
        ("$",   "USD"), ("€",   "EUR"), ("£",   "GBP"), ("¥",   "JPY"),
        ("kr",  "SEK"),
    ];

    private static bool IsKnownIsoCode(string s) =>
        s is "USD" or "EUR" or "GBP" or "SEK" or "NOK" or "DKK" or "CHF" or
             "JPY" or "CNY" or "CAD" or "AUD" or "NZD" or "HKD" or "SGD" or
             "PLN" or "CZK" or "HUF" or "RON" or "TRY" or "BRL" or "INR" or
             "MXN" or "ZAR" or "THB" or "IDR" or "MYR" or "PHP" or "ILS";
}
