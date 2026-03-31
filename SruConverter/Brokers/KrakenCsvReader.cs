using System.Globalization;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Reads Kraken export files and converts them to K4 rows.
///
/// Supports two Kraken CSV formats (auto-detected per file):
///
/// 1. Trades CSV  (kraken_spot_trades_*.csv)
///    Columns: txid, ordertxid, pair, aclass, subclass, time, type, ordertype,
///             price, cost, fee, vol, margin, misc, ledgers, ...
///    - Spot trades (margin = 0): genomsnittsmetoden (Swedish average-cost method)
///    - Margin/leveraged trades (margin > 0): uses the `net` P&L column for
///      closed positions; open/closing legs without a net value are skipped.
///
/// 2. Stocks &amp; ETFs Ledger CSV  (kraken_stocks_etfs_ledgers_*.csv)
///    Columns: txid, refid, time, type, subtype, aclass, subclass, asset, wallet,
///             amount, fee, balance
///    - spend/receive pairs with the same refid represent a completed trade.
///    - Supported trade types:
///        fiat  → crypto   = BUY  (updates genomsnittsmetoden state)
///        crypto → fiat    = SELL (emits K4 row)
///        crypto → crypto  = SWAP (SELL of source asset + BUY of target asset)
///    - earn, deposit, withdrawal, and other non-trade types are ignored.
///
/// Multiple files of either type can be provided together; average-cost state is
/// shared across all files so cost basis is calculated correctly across accounts.
/// For accurate cost basis, export the full trade history ("All time") from Kraken.
///
/// Per Skatteverket, all crypto assets are reported in K4 Sektion D.
/// </summary>
public class KrakenCsvReader : IBrokerReader
{
    public string   BrokerName          => "Kraken";
    public string[] SupportedExtensions => [".csv"];
    public string   FilePrompt          =>
        "Trades CSV and/or Stocks & ETFs Ledger CSV (kraken_spot_trades_*.csv / kraken_stocks_etfs_ledgers_*.csv)";

    public string HelpText =>
        "Log in to Kraken and go to Account Settings → Documents → New Export. " +
        "Two export types are useful for K4:\n\n" +
        "  • Trades (kraken_spot_trades_*.csv): covers spot buy/sell and margin/leveraged trades. " +
        "Select 'Trades', choose your date range (recommend 'All time' for accurate cost basis), " +
        "select All pairs and fields, and choose CSV format.\n\n" +
        "  • Stocks & ETFs Ledger (kraken_stocks_etfs_ledgers_*.csv): covers crypto conversions " +
        "(e.g. buying/selling meme coins or stablecoins) that do NOT appear in the Trades export. " +
        "Select 'Ledger', choose your date range, and choose CSV format.\n\n" +
        "You can add both files at the same time — this reader handles each format automatically. " +
        "Note: staking rewards (earn) are not imported here as they are treated as income, not capital gains.";

    public string HelpUrl => "https://www.kraken.com/c/account-settings/documents";

    // ── Validation ────────────────────────────────────────────────────────────

    public Task<string?> ValidateFileAsync(string filePath)
    {
        try
        {
            var firstLine = File.ReadLines(filePath).FirstOrDefault() ?? "";
            if (IsTradesHeader(firstLine) || IsLedgerHeader(firstLine))
                return Task.FromResult<string?>(null);

            return Task.FromResult<string?>(
                "Could not recognise this as a Kraken export. " +
                "Expected a Trades CSV (with 'pair', 'vol', 'margin' columns) " +
                "or a Ledger CSV (with 'refid', 'asset', 'amount' columns).");
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"Could not read file: {ex.Message}");
        }
    }

    // ── Main reader ───────────────────────────────────────────────────────────

    public async Task<List<K4Row>> ReadAsync(IEnumerable<string> filePaths, RiksbankService riksbank)
    {
        // Shared average-cost state across all files (one per base asset).
        var avgCost    = new Dictionary<string, AssetState>(StringComparer.OrdinalIgnoreCase);
        var rows       = new List<K4Row>();
        var loggedKeys = new HashSet<string>();

        // ── Pass 1: collect + sort all trades so chronological order is correct
        //    even when the user provides files covering different date ranges.
        var tradeRows  = new List<KrakenTrade>();
        var ledgerRows = new List<KrakenLedgerEntry>();

        foreach (var path in filePaths)
        {
            var lines     = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            var firstLine = lines.Length > 0 ? lines[0] : "";

            if (IsTradesHeader(firstLine))
                tradeRows.AddRange(ParseTradesFile(lines));
            else if (IsLedgerHeader(firstLine))
                ledgerRows.AddRange(ParseLedgerFile(lines));
            else
                Console.WriteLine($"  WARNING: Skipping unrecognised file format: {Path.GetFileName(path)}");
        }

        tradeRows.Sort((a, b) => a.Time.CompareTo(b.Time));

        // ── Pass 2: process chronologically ──────────────────────────────────

        // Interleave trades + ledger entries by timestamp.
        // Build a merged timeline so avg-cost state is consistent.
        var timeline = new List<(DateTime Time, object Entry)>();
        foreach (var t in tradeRows)   timeline.Add((t.Time,                   t));
        foreach (var l in ledgerRows)  timeline.Add((l.Time,                   l));
        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));

        foreach (var (_, entry) in timeline)
        {
            if (entry is KrakenTrade trade)
                await ProcessTrade(trade, avgCost, rows, riksbank, loggedKeys);
            else if (entry is KrakenLedgerEntry ledger)
                await ProcessLedgerSwap(ledger, avgCost, rows, riksbank, loggedKeys);
        }

        return rows;
    }

    // ── Trades processing ─────────────────────────────────────────────────────

    private async Task ProcessTrade(
        KrakenTrade t,
        Dictionary<string, AssetState> avgCost,
        List<K4Row> rows,
        RiksbankService riksbank,
        HashSet<string> loggedKeys)
    {
        var (baseAsset, quoteCurrency) = ParsePair(t.Pair);
        var date = DateOnly.FromDateTime(t.Time);
        bool isMargin = t.Margin > 0m;

        // ── Margin trade ─────────────────────────────────────────────────────
        if (isMargin)
        {
            // Only the initiating leg has a net P&L; closing legs have no net value.
            bool isClosingLeg = t.Misc.Contains("closing", StringComparison.OrdinalIgnoreCase);
            if (isClosingLeg) return;

            if (!decimal.TryParse(t.Net, NumberStyles.Any, CultureInfo.InvariantCulture, out var netPnl))
                return; // position not yet closed — skip

            decimal rate = await GetRate(riksbank, quoteCurrency, date, loggedKeys);

            long proceedsSek, costSek;
            if (t.Type == "sell")
            {
                // Opened a short: received (cost − fee) fiat, later bought back at different price.
                // net = total P&L of the closed position in quote currency (negative = loss).
                proceedsSek = RoundSek((t.Cost - t.Fee) * rate);
                costSek     = netPnl < 0
                    ? proceedsSek + RoundSek(Math.Abs(netPnl) * rate)  // loss: cost to close was higher
                    : proceedsSek - RoundSek(netPnl * rate);            // gain: cost to close was lower
            }
            else // buy (opened a long, now closed)
            {
                costSek     = RoundSek((t.Cost + t.Fee) * rate);
                proceedsSek = netPnl > 0
                    ? costSek + RoundSek(netPnl * rate)   // gain
                    : costSek - RoundSek(Math.Abs(netPnl) * rate); // loss
            }

            rows.Add(MakeRow(baseAsset + " (hävstång)", t.Vol, date, proceedsSek, costSek));
            return;
        }

        // ── Spot trade ────────────────────────────────────────────────────────
        decimal spotRate = await GetRate(riksbank, quoteCurrency, date, loggedKeys);

        if (t.Type == "buy")
        {
            // Total acquisition cost includes the trading fee.
            var totalCostSek = (t.Cost + t.Fee) * spotRate;
            GetOrAdd(avgCost, baseAsset).Buy(t.Vol, totalCostSek);
        }
        else if (t.Type == "sell")
        {
            // Net proceeds after deducting fee.
            var proceedsSek  = RoundSek((t.Cost - t.Fee) * spotRate);
            var state        = GetOrAdd(avgCost, baseAsset);

            if (state.HeldQty < t.Vol * 0.999m)
                Console.WriteLine(
                    $"  WARNING: Selling {t.Vol:F8} {baseAsset} but only {state.HeldQty:F8} tracked. " +
                    "Export full trade history ('All time') for an accurate cost basis.");

            var costSek = state.Sell(t.Vol);
            rows.Add(MakeRow(baseAsset, t.Vol, date, proceedsSek, costSek));
        }
    }

    // ── Ledger swap processing ────────────────────────────────────────────────

    private async Task ProcessLedgerSwap(
        KrakenLedgerEntry swap,
        Dictionary<string, AssetState> avgCost,
        List<K4Row> rows,
        RiksbankService riksbank,
        HashSet<string> loggedKeys)
    {
        // Each KrakenLedgerEntry represents one complete spend→receive pair.
        var date  = DateOnly.FromDateTime(swap.Time);

        string spendAsset   = swap.SpendAsset;
        string receiveAsset = swap.ReceiveAsset;
        decimal spendAmt    = swap.SpendAmount;   // absolute (positive) value
        decimal spendFee    = swap.SpendFee;
        decimal receiveAmt  = swap.ReceiveAmount;
        decimal receiveFee  = swap.ReceiveFee;

        bool spendIsFiat   = IsFiatAsset(spendAsset);
        bool receiveIsFiat = IsFiatAsset(receiveAsset);

        if (spendIsFiat && !receiveIsFiat)
        {
            // BUY: spent fiat, acquired crypto
            string fiat     = NormaliseAsset(spendAsset);
            decimal fiatRate = await GetRate(riksbank, fiat, date, loggedKeys);
            var totalCostSek = (spendAmt + spendFee) * fiatRate;
            GetOrAdd(avgCost, receiveAsset).Buy(receiveAmt, totalCostSek);
        }
        else if (!spendIsFiat && receiveIsFiat)
        {
            // SELL: disposed of crypto, received fiat
            string fiat      = NormaliseAsset(receiveAsset);
            decimal fiatRate = await GetRate(riksbank, fiat, date, loggedKeys);
            var proceedsSek  = RoundSek((receiveAmt - receiveFee) * fiatRate);
            var state        = GetOrAdd(avgCost, spendAsset);

            if (state.HeldQty < spendAmt * 0.999m)
                Console.WriteLine(
                    $"  WARNING: Selling {spendAmt:F8} {spendAsset} but only {state.HeldQty:F8} tracked. " +
                    "Consider exporting full history for accurate cost basis.");

            var costSek = state.Sell(spendAmt);
            rows.Add(MakeRow(spendAsset, spendAmt, date, proceedsSek, costSek));
        }
        else if (!spendIsFiat && !receiveIsFiat)
        {
            // SWAP: crypto → crypto — simultaneously a taxable disposal and a new acquisition.
            // Per Skatteverket: Försäljningspris of swapped-away asset = market value of
            // received asset in SEK. Same amount becomes Omkostnadsbelopp of received asset.
            //
            // Fee is paid in the spend asset and is also a disposal (deducted from holdings).
            // Total spend-asset disposed = spendAmt + spendFee.
            // Proceeds = market value of what was *received* (fee does not come back as receive asset).
            Console.WriteLine(
                $"  NOTE: Crypto swap {spendAsset}→{receiveAsset} on {date} — " +
                "treating as SELL of source and BUY of target at spot value.");

            var totalSpendQty = spendAmt + spendFee; // fee is paid in the spend asset

            // Determine SEK proceeds:
            // 1. If receive side is a stablecoin (USDT/USDC/…), use receiveAmt × stablecoinRate.
            // 2. Fall back to spend side if it is a stablecoin — spendAmt × rate (fee excluded;
            //    per Skatteverket proceeds = value of what you received, not what you gave away).
            // 3. Otherwise warn — the user must enter this trade manually.
            long proceedsSek = 0L;
            string? receiveQuote = TryGetFiatEquivalent(receiveAsset);
            string? spendQuote   = TryGetFiatEquivalent(spendAsset);

            if (receiveQuote != null)
            {
                // Prefer receive-side: we know the exact fiat value of what was received.
                decimal rate = await GetRate(riksbank, receiveQuote, date, loggedKeys);
                proceedsSek  = RoundSek(receiveAmt * rate);
            }
            else if (spendQuote != null)
            {
                // Spend side is a stablecoin: proceeds ≈ spendAmt × rate.
                // (The fee portion reduced net gain; it is captured via higher totalSpendQty cost.)
                decimal rate = await GetRate(riksbank, spendQuote, date, loggedKeys);
                proceedsSek  = RoundSek(spendAmt * rate);
            }
            else
            {
                Console.WriteLine(
                    $"  WARNING: Cannot determine SEK value for swap {spendAsset}→{receiveAsset}. " +
                    "Neither asset is a recognised stablecoin. " +
                    "You may need to enter this trade manually on K4.");
            }

            // SELL of spend asset (always emit K4 row so it is never silently dropped)
            var stateSpend = GetOrAdd(avgCost, spendAsset);
            if (stateSpend.HeldQty < totalSpendQty * 0.999m)
                Console.WriteLine(
                    $"  WARNING: Disposing of {totalSpendQty:F8} {spendAsset} but only " +
                    $"{stateSpend.HeldQty:F8} tracked.");

            var costSek = stateSpend.Sell(totalSpendQty);
            rows.Add(MakeRow(spendAsset, totalSpendQty, date, proceedsSek, costSek));

            // BUY of receive asset: cost basis = proceeds (market value at time of swap).
            GetOrAdd(avgCost, receiveAsset).Buy(receiveAmt, proceedsSek);
        }
        // else: fiat→fiat or unhandled — ignore
    }

    // ── File parsers ──────────────────────────────────────────────────────────

    private static List<KrakenTrade> ParseTradesFile(string[] lines)
    {
        var result  = new List<KrakenTrade>();
        var colIdx  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var cols = CsvHelper.SplitLine(raw.Trim());
            if (cols.Count == 0) continue;

            if (colIdx.Count == 0)
            {
                // First non-empty line is the header
                for (int i = 0; i < cols.Count; i++)
                    colIdx[cols[i].Trim()] = i;
                continue;
            }

            string Get(string col) =>
                colIdx.TryGetValue(col, out int i) && i < cols.Count ? cols[i].Trim() : "";

            var timeStr = Get("time");
            if (!TryParseKrakenDateTime(timeStr, out var dt)) continue;
            if (!decimal.TryParse(Get("vol"), NumberStyles.Any, CultureInfo.InvariantCulture, out var vol))
                continue;

            result.Add(new KrakenTrade
            {
                Time   = dt,
                Pair   = Get("pair"),
                Type   = Get("type").ToLowerInvariant(),
                Cost   = CsvHelper.ParseDecimal(Get("cost")),
                Fee    = CsvHelper.ParseDecimal(Get("fee")),
                Vol    = vol,
                Margin = CsvHelper.ParseDecimal(Get("margin")),
                Misc   = Get("misc"),
                Net    = Get("net"),
            });
        }

        return result;
    }

    private static List<KrakenLedgerEntry> ParseLedgerFile(string[] lines)
    {
        var allRows = new List<RawLedgerRow>();
        var colIdx  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var cols = CsvHelper.SplitLine(raw.Trim());
            if (cols.Count == 0) continue;

            if (colIdx.Count == 0)
            {
                for (int i = 0; i < cols.Count; i++)
                    colIdx[cols[i].Trim()] = i;
                continue;
            }

            string Get(string col) =>
                colIdx.TryGetValue(col, out int i) && i < cols.Count ? cols[i].Trim() : "";

            var type    = Get("type").ToLowerInvariant();
            var subtype = Get("subtype").ToLowerInvariant();

            // Only process spend/receive entries (actual trades).
            // Skip: deposit, withdrawal, earn (rewards), transfer, adjustment.
            if (type is not ("spend" or "receive")) continue;
            if (subtype is "dustsweeping") continue; // tiny dust conversions — negligible, skip

            var timeStr = Get("time");
            if (!TryParseKrakenDateTime(timeStr, out var dt)) continue;

            if (!decimal.TryParse(Get("amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                continue;

            allRows.Add(new RawLedgerRow
            {
                RefId   = Get("refid"),
                Time    = dt,
                Type    = type,
                Asset   = NormaliseAsset(Get("asset")),
                Subclass = Get("subclass").ToLowerInvariant(),
                Amount  = amount,
                Fee     = CsvHelper.ParseDecimal(Get("fee")),
            });
        }

        // Group by refid and pair up spend → receive
        var result = new List<KrakenLedgerEntry>();
        var grouped = allRows.GroupBy(r => r.RefId);

        foreach (var group in grouped)
        {
            var spend   = group.FirstOrDefault(r => r.Amount < 0);
            var receive = group.FirstOrDefault(r => r.Amount > 0);
            if (spend == null || receive == null) continue;

            result.Add(new KrakenLedgerEntry
            {
                Time          = receive.Time, // use receive time as the "trade time"
                SpendAsset    = spend.Asset,
                SpendAmount   = Math.Abs(spend.Amount),
                SpendFee      = Math.Abs(spend.Fee),
                ReceiveAsset  = receive.Asset,
                ReceiveAmount = receive.Amount,
                ReceiveFee    = Math.Abs(receive.Fee),
            });
        }

        return result;
    }

    // ── Pair parsing ──────────────────────────────────────────────────────────

    private static (string Base, string Quote) ParsePair(string pair)
    {
        // Modern format: "BTC/EUR"
        var slash = pair.IndexOf('/');
        if (slash > 0)
            return (pair[..slash].Trim().ToUpperInvariant(),
                    pair[(slash + 1)..].Trim().ToUpperInvariant());

        // Legacy Kraken format: "XXBTZUSD", "XETHZEUR", etc.
        // Try known 4-char Z-prefixed fiat suffixes first, then 3-char bare
        foreach (var (suffix, code) in KrakenLegacyQuotes)
            if (pair.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (NormaliseKrakenBase(pair[..^suffix.Length]), code);

        // Unknown pair — return as-is and hope for the best
        return (pair.ToUpperInvariant(), "USD");
    }

    private static string NormaliseKrakenBase(string raw)
    {
        var s = raw.ToUpperInvariant();
        return s switch
        {
            "XXBT" or "XBT" => "BTC",
            "XETH"          => "ETH",
            "XLTC"          => "LTC",
            "XXLM"          => "XLM",
            "XXRP"          => "XRP",
            "XXMR"          => "XMR",
            "XZEC"          => "ZEC",
            "XREP"          => "REP",
            // Strip single leading X for anything else (e.g. XDOT → DOT)
            _ when s.StartsWith('X') && s.Length > 2 => s[1..],
            _ => s,
        };
    }

    // Known legacy Kraken quote currencies (try longest / Z-prefixed first)
    private static readonly (string Suffix, string Code)[] KrakenLegacyQuotes =
    [
        ("ZUSD", "USD"), ("ZEUR", "EUR"), ("ZGBP", "GBP"), ("ZCAD", "CAD"),
        ("ZJPY", "JPY"), ("ZCHF", "CHF"), ("ZAUD", "AUD"),
        ("USDT", "USD"), ("USDC", "USD"), ("DAI",  "USD"),
        ("USD",  "USD"), ("EUR",  "EUR"), ("GBP",  "GBP"), ("CAD",  "CAD"),
        ("JPY",  "JPY"), ("CHF",  "CHF"), ("AUD",  "AUD"), ("SEK",  "SEK"),
    ];

    // ── Asset helpers ─────────────────────────────────────────────────────────

    /// Strips Kraken-specific suffixes like ".HOLD" from asset names.
    private static string NormaliseAsset(string asset)
    {
        var dot = asset.IndexOf('.');
        return (dot > 0 ? asset[..dot] : asset).ToUpperInvariant();
    }

    private static bool IsFiatAsset(string asset)
    {
        var a = NormaliseAsset(asset);
        return a is "EUR" or "USD" or "GBP" or "CAD" or "JPY" or "CHF" or "AUD" or "SEK" or "NOK" or "DKK";
    }

    /// Returns the fiat ISO code for stablecoins, or null for true crypto.
    private static string? TryGetFiatEquivalent(string asset)
    {
        var a = NormaliseAsset(asset);
        return a switch
        {
            "USDT" or "USDC" or "DAI" or "TUSD" or "BUSD" or "PYUSD" => "USD",
            "EURT" or "EURS" => "EUR",
            _ => null,
        };
    }

    // ── Rate helper ───────────────────────────────────────────────────────────

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

    // ── K4 row factory ────────────────────────────────────────────────────────

    private static K4Row MakeRow(string beteckning, decimal antal, DateOnly date,
                                 long proceedsSek, long costSek)
    {
        var vinst   = proceedsSek > costSek ? proceedsSek - costSek : 0L;
        var forlust = costSek > proceedsSek ? costSek - proceedsSek : 0L;

        return new K4Row
        {
            Sektion          = "D",
            Antal            = antal,
            Beteckning       = beteckning,
            Datum            = date.ToString("yyyy-MM-dd"),
            Forsaljningspris = proceedsSek,
            Omkostnadsbelopp = costSek,
            Vinst            = vinst,
            Forlust          = forlust,
        };
    }

    private static long RoundSek(decimal amount) =>
        (long)Math.Round(amount, MidpointRounding.AwayFromZero);

    private static AssetState GetOrAdd(Dictionary<string, AssetState> dict, string key)
    {
        if (!dict.TryGetValue(key, out var s)) dict[key] = s = new AssetState();
        return s;
    }

    // ── Date parsing ─────────────────────────────────────────────────────────

    private static readonly string[] KrakenDateFormats =
    [
        "yyyy-MM-dd HH:mm:ss.ffff",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss.ff",
        "yyyy-MM-dd HH:mm:ss.f",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
    ];

    private static bool TryParseKrakenDateTime(string s, out DateTime dt)
    {
        foreach (var fmt in KrakenDateFormats)
            if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out dt)) return true;
        dt = default;
        return false;
    }

    // ── Header detection ─────────────────────────────────────────────────────

    private static bool IsTradesHeader(string line) =>
        line.Contains("pair",   StringComparison.OrdinalIgnoreCase) &&
        line.Contains("vol",    StringComparison.OrdinalIgnoreCase) &&
        line.Contains("margin", StringComparison.OrdinalIgnoreCase);

    private static bool IsLedgerHeader(string line) =>
        line.Contains("refid",  StringComparison.OrdinalIgnoreCase) &&
        line.Contains("asset",  StringComparison.OrdinalIgnoreCase) &&
        line.Contains("amount", StringComparison.OrdinalIgnoreCase);

    // ── Private types ─────────────────────────────────────────────────────────

    private sealed class AssetState
    {
        public decimal HeldQty     { get; private set; }
        public decimal AvgCostSek  { get; private set; }

        /// Records a purchase, updating the running average cost per unit (genomsnittsmetoden).
        public void Buy(decimal qty, decimal totalCostSek)
        {
            if (qty <= 0m) return;
            AvgCostSek = HeldQty == 0m
                ? totalCostSek / qty
                : (HeldQty * AvgCostSek + totalCostSek) / (HeldQty + qty);
            HeldQty += qty;
        }

        /// Deducts a disposal from holdings and returns the SEK cost basis for the lot.
        public long Sell(decimal qty)
        {
            if (qty <= 0m) return 0L;
            var costBasis = RoundSek(qty * AvgCostSek);
            HeldQty = Math.Max(0m, HeldQty - qty);
            return costBasis;
        }
    }

    private sealed class KrakenTrade
    {
        public DateTime Time   { get; init; }
        public string   Pair   { get; init; } = "";
        public string   Type   { get; init; } = "";  // "buy" | "sell"
        public decimal  Cost   { get; init; }        // fiat amount (gross)
        public decimal  Fee    { get; init; }        // fee in quote currency
        public decimal  Vol    { get; init; }        // crypto quantity
        public decimal  Margin { get; init; }        // 0 for spot; > 0 for margin
        public string   Misc   { get; init; } = "";
        public string   Net    { get; init; } = "";  // realized P&L (margin only)
    }

    private sealed class KrakenLedgerEntry
    {
        public DateTime Time          { get; init; }
        public string   SpendAsset    { get; init; } = "";
        public decimal  SpendAmount   { get; init; } // absolute (positive) value
        public decimal  SpendFee      { get; init; }
        public string   ReceiveAsset  { get; init; } = "";
        public decimal  ReceiveAmount { get; init; }
        public decimal  ReceiveFee    { get; init; }
    }

    private sealed class RawLedgerRow
    {
        public string   RefId    { get; init; } = "";
        public DateTime Time     { get; init; }
        public string   Type     { get; init; } = "";
        public string   Asset    { get; init; } = "";
        public string   Subclass { get; init; } = "";
        public decimal  Amount   { get; init; }
        public decimal  Fee      { get; init; }
    }
}
