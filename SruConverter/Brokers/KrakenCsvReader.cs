using System.Globalization;
using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Reads Kraken export files and converts them to TradeEvents (spot) or K4 rows (margin).
///
/// Supports two Kraken CSV formats (auto-detected per file):
///
/// 1. Trades CSV  (kraken_spot_trades_*.csv)
///    Columns: txid, ordertxid, pair, aclass, subclass, time, type, ordertype,
///             price, cost, fee, vol, margin, misc, ledgers, ...
///    - Spot trades (margin = 0): emitted as TradeEvents for shared genomsnittsmetoden.
///    - Margin/leveraged trades (margin > 0): emitted as direct K4 rows via GetDirectRowsAsync.
///
/// 2. Stocks and ETFs Ledger CSV  (kraken_stocks_etfs_ledgers_*.csv)
///    Columns: txid, refid, time, type, subtype, aclass, subclass, asset, wallet,
///             amount, fee, balance
///    - spend/receive pairs with the same refid represent a completed trade.
///    - Supported trade types:
///        fiat  to crypto   = TradeEvent.Buy
///        crypto to fiat    = TradeEvent.Sell
///        crypto to crypto  = TradeEvent.Sell + TradeEvent.Buy (swap)
///    - earn, deposit, withdrawal, and other non-trade types are ignored.
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
        "Log in to Kraken and go to Account Settings -> Documents -> New Export. " +
        "Two export types are useful for K4:\n\n" +
        "  * Trades (kraken_spot_trades_*.csv): covers spot buy/sell and margin/leveraged trades. " +
        "Select 'Trades', choose your date range (recommend 'All time' for accurate cost basis), " +
        "select All pairs and fields, and choose CSV format.\n\n" +
        "  * Stocks & ETFs Ledger (kraken_stocks_etfs_ledgers_*.csv): covers crypto conversions " +
        "(e.g. buying/selling meme coins or stablecoins) that do NOT appear in the Trades export. " +
        "Select 'Ledger', choose your date range, and choose CSV format.\n\n" +
        "You can add both files at the same time - this reader handles each format automatically. " +
        "Note: staking rewards (earn) are not imported here as they are treated as income, not capital gains.";

    public string HelpUrl => "https://www.kraken.com/c/account-settings/documents";

    // -- Validation ------------------------------------------------------------

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

    // -- GetTradeEventsAsync: spot trades from both CSV formats ----------------

    public async Task<List<TradeEvent>> GetTradeEventsAsync(
        IEnumerable<string> filePaths, RiksbankService riksbank)
    {
        var events     = new List<TradeEvent>();
        var loggedKeys = new HashSet<string>();
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

        // Build a merged chronological timeline of spot trades + ledger entries.
        var timeline = new List<(DateTime Time, object Entry)>();
        foreach (var t in tradeRows)
            if (t.Margin == 0m) // spot only - margin goes to GetDirectRowsAsync
                timeline.Add((t.Time, t));
        foreach (var l in ledgerRows)
            timeline.Add((l.Time, l));
        timeline.Sort((a, b) => a.Time.CompareTo(b.Time));

        foreach (var (_, entry) in timeline)
        {
            if (entry is KrakenTrade trade)
            {
                var (baseAsset, quoteCurrency) = ParsePair(trade.Pair);
                var date = DateOnly.FromDateTime(trade.Time);
                decimal rate = await GetRate(riksbank, quoteCurrency, date, loggedKeys);

                if (trade.Type == "buy")
                {
                    events.Add(new TradeEvent
                    {
                        Timestamp = trade.Time,
                        Asset     = baseAsset,
                        Quantity  = trade.Vol,
                        ValueSek  = trade.Cost * rate,
                        FeeSek    = trade.Fee  * rate,
                        Kind      = TradeKind.Buy,
                        Source    = "Kraken",
                    });
                }
                else if (trade.Type == "sell")
                {
                    events.Add(new TradeEvent
                    {
                        Timestamp = trade.Time,
                        Asset     = baseAsset,
                        Quantity  = trade.Vol,
                        ValueSek  = trade.Cost * rate,
                        FeeSek    = trade.Fee  * rate,
                        Kind      = TradeKind.Sell,
                        Source    = "Kraken",
                    });
                }
            }
            else if (entry is KrakenLedgerEntry ledger)
            {
                var evts = await BuildLedgerEvents(ledger, riksbank, loggedKeys);
                events.AddRange(evts);
            }
        }

        return events;
    }

    // -- GetDirectRowsAsync: margin trades from Trades CSV only ----------------

    public async Task<List<K4Row>> GetDirectRowsAsync(
        IEnumerable<string> filePaths, RiksbankService riksbank)
    {
        var rows       = new List<K4Row>();
        var loggedKeys = new HashSet<string>();

        foreach (var path in filePaths)
        {
            var lines     = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            var firstLine = lines.Length > 0 ? lines[0] : "";
            if (!IsTradesHeader(firstLine)) continue;

            var tradeRows = ParseTradesFile(lines);
            tradeRows.Sort((a, b) => a.Time.CompareTo(b.Time));

            foreach (var t in tradeRows)
            {
                if (t.Margin == 0m) continue; // spot handled by GetTradeEventsAsync

                // Only initiating legs with a realised net P&L.
                bool isClosingLeg = t.Misc.Contains("closing", StringComparison.OrdinalIgnoreCase);
                if (isClosingLeg) continue;

                if (!decimal.TryParse(t.Net, NumberStyles.Any, CultureInfo.InvariantCulture, out var netPnl))
                    continue; // position not yet closed

                var (baseAsset, quoteCurrency) = ParsePair(t.Pair);
                var date = DateOnly.FromDateTime(t.Time);
                decimal rate = await GetRate(riksbank, quoteCurrency, date, loggedKeys);

                long proceedsSek, costSek;
                if (t.Type == "sell")
                {
                    // Opened a short.
                    proceedsSek = RoundSek((t.Cost - t.Fee) * rate);
                    costSek     = netPnl < 0
                        ? proceedsSek + RoundSek(Math.Abs(netPnl) * rate)
                        : proceedsSek - RoundSek(netPnl * rate);
                }
                else // buy (opened a long, now closed)
                {
                    costSek     = RoundSek((t.Cost + t.Fee) * rate);
                    proceedsSek = netPnl > 0
                        ? costSek + RoundSek(netPnl * rate)
                        : costSek - RoundSek(Math.Abs(netPnl) * rate);
                }

                rows.Add(MakeRow(baseAsset + " (havstang)", t.Vol, date, proceedsSek, costSek));
            }
        }

        return rows;
    }

    // -- Ledger event builder --------------------------------------------------

    private async Task<List<TradeEvent>> BuildLedgerEvents(
        KrakenLedgerEntry swap, RiksbankService riksbank, HashSet<string> loggedKeys)
    {
        var events = new List<TradeEvent>();
        var date   = DateOnly.FromDateTime(swap.Time);

        string  spendAsset   = swap.SpendAsset;
        string  receiveAsset = swap.ReceiveAsset;
        decimal spendAmt     = swap.SpendAmount;
        decimal spendFee     = swap.SpendFee;
        decimal receiveAmt   = swap.ReceiveAmount;
        decimal receiveFee   = swap.ReceiveFee;

        bool spendIsFiat   = IsFiatAsset(spendAsset);
        bool receiveIsFiat = IsFiatAsset(receiveAsset);

        if (spendIsFiat && !receiveIsFiat)
        {
            // BUY: spent fiat, acquired crypto.
            string  fiat     = NormaliseAsset(spendAsset);
            decimal fiatRate = await GetRate(riksbank, fiat, date, loggedKeys);
            events.Add(new TradeEvent
            {
                Timestamp = swap.Time,
                Asset     = receiveAsset,
                Quantity  = receiveAmt,
                ValueSek  = spendAmt * fiatRate,
                FeeSek    = spendFee * fiatRate,
                Kind      = TradeKind.Buy,
                Source    = "Kraken",
            });
        }
        else if (!spendIsFiat && receiveIsFiat)
        {
            // SELL: disposed of crypto, received fiat.
            string  fiat     = NormaliseAsset(receiveAsset);
            decimal fiatRate = await GetRate(riksbank, fiat, date, loggedKeys);
            events.Add(new TradeEvent
            {
                Timestamp = swap.Time,
                Asset     = spendAsset,
                Quantity  = spendAmt,
                ValueSek  = receiveAmt * fiatRate,
                FeeSek    = receiveFee * fiatRate,
                Kind      = TradeKind.Sell,
                Source    = "Kraken",
            });
        }
        else if (!spendIsFiat && !receiveIsFiat)
        {
            // SWAP: crypto to crypto - taxable disposal of spend asset + acquisition of receive asset.
            Console.WriteLine(
                $"  NOTE: Crypto swap {spendAsset}->{receiveAsset} on {date} - " +
                "treating as SELL of source and BUY of target at spot value.");

            // Fee is paid in spend asset, so the full disposal quantity is spendAmt + spendFee.
            decimal totalSpendQty = spendAmt + spendFee;

            // Determine SEK value: prefer receive side if it is a stablecoin, else spend side.
            decimal proceedsDecSek = 0m;
            string? receiveQuote   = TryGetFiatEquivalent(receiveAsset);
            string? spendQuote     = TryGetFiatEquivalent(spendAsset);

            if (receiveQuote != null)
            {
                decimal rate   = await GetRate(riksbank, receiveQuote, date, loggedKeys);
                proceedsDecSek = receiveAmt * rate;
            }
            else if (spendQuote != null)
            {
                decimal rate   = await GetRate(riksbank, spendQuote, date, loggedKeys);
                proceedsDecSek = spendAmt * rate;
            }
            else
            {
                Console.WriteLine(
                    $"  WARNING: Cannot determine SEK value for swap {spendAsset}->{receiveAsset}. " +
                    "Neither asset is a recognised stablecoin. " +
                    "You may need to enter this trade manually on K4.");
            }

            // SELL of spend asset - fee is already included in Quantity, so FeeSek = 0.
            events.Add(new TradeEvent
            {
                Timestamp = swap.Time,
                Asset     = spendAsset,
                Quantity  = totalSpendQty,
                ValueSek  = proceedsDecSek,
                FeeSek    = 0m,
                Kind      = TradeKind.Sell,
                Source    = "Kraken",
            });

            // BUY of receive asset: cost basis = market value at time of swap.
            events.Add(new TradeEvent
            {
                Timestamp = swap.Time,
                Asset     = receiveAsset,
                Quantity  = receiveAmt,
                ValueSek  = proceedsDecSek,
                FeeSek    = 0m,
                Kind      = TradeKind.Buy,
                Source    = "Kraken",
            });
        }
        // else: fiat->fiat or unhandled - ignore

        return events;
    }

    // -- File parsers ----------------------------------------------------------

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
            if (subtype is "dustsweeping") continue; // tiny dust conversions - negligible, skip

            var timeStr = Get("time");
            if (!TryParseKrakenDateTime(timeStr, out var dt)) continue;

            if (!decimal.TryParse(Get("amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                continue;

            allRows.Add(new RawLedgerRow
            {
                RefId    = Get("refid"),
                Time     = dt,
                Type     = type,
                Asset    = NormaliseAsset(Get("asset")),
                Subclass = Get("subclass").ToLowerInvariant(),
                Amount   = amount,
                Fee      = CsvHelper.ParseDecimal(Get("fee")),
            });
        }

        // Group by refid and pair up spend -> receive
        var result  = new List<KrakenLedgerEntry>();
        var grouped = allRows.GroupBy(r => r.RefId);

        foreach (var group in grouped)
        {
            var spend   = group.FirstOrDefault(r => r.Amount < 0);
            var receive = group.FirstOrDefault(r => r.Amount > 0);
            if (spend == null || receive == null) continue;

            result.Add(new KrakenLedgerEntry
            {
                Time          = receive.Time,
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

    // -- Pair parsing ----------------------------------------------------------

    private static (string Base, string Quote) ParsePair(string pair)
    {
        // Modern format: "BTC/EUR"
        var slash = pair.IndexOf('/');
        if (slash > 0)
            return (pair[..slash].Trim().ToUpperInvariant(),
                    pair[(slash + 1)..].Trim().ToUpperInvariant());

        // Legacy Kraken format: "XXBTZUSD", "XETHZEUR", etc.
        foreach (var (suffix, code) in KrakenLegacyQuotes)
            if (pair.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return (NormaliseKrakenBase(pair[..^suffix.Length]), code);

        // Unknown pair - return as-is
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
            // Strip single leading X for anything else (e.g. XDOT -> DOT)
            _ when s.StartsWith('X') && s.Length > 2 => s[1..],
            _ => s,
        };
    }

    private static readonly (string Suffix, string Code)[] KrakenLegacyQuotes =
    [
        ("ZUSD", "USD"), ("ZEUR", "EUR"), ("ZGBP", "GBP"), ("ZCAD", "CAD"),
        ("ZJPY", "JPY"), ("ZCHF", "CHF"), ("ZAUD", "AUD"),
        ("USDT", "USD"), ("USDC", "USD"), ("DAI",  "USD"),
        ("USD",  "USD"), ("EUR",  "EUR"), ("GBP",  "GBP"), ("CAD",  "CAD"),
        ("JPY",  "JPY"), ("CHF",  "CHF"), ("AUD",  "AUD"), ("SEK",  "SEK"),
    ];

    // -- Asset helpers ---------------------------------------------------------

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

    // -- Rate helper -----------------------------------------------------------

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

    // -- K4 row factory --------------------------------------------------------

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

    // -- Date parsing ----------------------------------------------------------

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

    // -- Header detection ------------------------------------------------------

    private static bool IsTradesHeader(string line) =>
        line.Contains("pair",   StringComparison.OrdinalIgnoreCase) &&
        line.Contains("vol",    StringComparison.OrdinalIgnoreCase) &&
        line.Contains("margin", StringComparison.OrdinalIgnoreCase);

    private static bool IsLedgerHeader(string line) =>
        line.Contains("refid",  StringComparison.OrdinalIgnoreCase) &&
        line.Contains("asset",  StringComparison.OrdinalIgnoreCase) &&
        line.Contains("amount", StringComparison.OrdinalIgnoreCase);

    // -- Private types ---------------------------------------------------------

    private sealed class KrakenTrade
    {
        public DateTime Time   { get; init; }
        public string   Pair   { get; init; } = "";
        public string   Type   { get; init; } = "";
        public decimal  Cost   { get; init; }
        public decimal  Fee    { get; init; }
        public decimal  Vol    { get; init; }
        public decimal  Margin { get; init; }
        public string   Misc   { get; init; } = "";
        public string   Net    { get; init; } = "";
    }

    private sealed class KrakenLedgerEntry
    {
        public DateTime Time          { get; init; }
        public string   SpendAsset    { get; init; } = "";
        public decimal  SpendAmount   { get; init; }
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