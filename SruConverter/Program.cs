using SruConverter.Brokers;
using SruConverter.Models;
using SruConverter.Services;
using SruConverter.UI;

// ── Logo ──────────────────────────────────────────────────────────────────────
ConsoleUi.ShowLogo();

// ── Personal info ─────────────────────────────────────────────────────────────
var person = ConsoleUi.CollectPersonInfo();
var year   = ConsoleUi.PromptYear();

// ── Broker selection ──────────────────────────────────────────────────────────
var selectedBrokers = ConsoleUi.SelectBrokers(BrokerRegistry.All);

// ── Per-broker file collection ────────────────────────────────────────────────
var selections = new List<(IBrokerReader broker, List<string> files)>();
foreach (var broker in selectedBrokers)
    selections.Add((broker, await ConsoleUi.CollectFilesAsync(broker)));

// ── Output directory ──────────────────────────────────────────────────────────
var defaultOutput = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "output"));
var outputDir = ConsoleUi.PromptOutputDir(defaultOutput);

// ── Confirm ───────────────────────────────────────────────────────────────────
if (!ConsoleUi.ConfirmSummary(person, year, selections, outputDir))
{
    Console.WriteLine("  Aborted.");
    return 1;
}

// ── Read all broker files ─────────────────────────────────────────────────────
var allRows = new List<K4Row>();
using var riksbank = new RiksbankService();

// ── Collect trade events from all crypto brokers ──────────────────────────────
var allEvents = new List<TradeEvent>();
foreach (var (broker, files) in selections)
{
    Console.WriteLine($"  Collecting events from {broker.BrokerName}...");
    try
    {
        var events = await broker.GetTradeEventsAsync(files, riksbank);
        Console.WriteLine($"    -> {events.Count} events");
        allEvents.AddRange(events);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ERROR reading {broker.BrokerName}: {ex.Message}");
        return 1;
    }
}

// ── Sort chronologically and process with shared genomsnittsmetoden ───────────
allEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

var cryptoState = new CryptoHoldingsState();

foreach (var evt in allEvents)
{
    if (evt.Kind == TradeKind.Buy)
    {
        cryptoState.Buy(evt.Asset, evt.Quantity, evt.ValueSek + evt.FeeSek);
    }
    else // Sell
    {
        // Always update shared state (prior-year sells reduce holdings and affect
        // the running average for subsequent purchases — genomsnittsmetoden spans all years).
        var (computedCost, sufficient) = cryptoState.Sell(evt.Asset, evt.Quantity);

        // Only emit a K4 row for disposals that fall within the declared tax year.
        if (DateOnly.FromDateTime(evt.Timestamp).Year != year) continue;

        long costSek;

        if (sufficient)
        {
            costSek = computedCost;
        }
        else if (evt.FallbackCostSek.HasValue)
        {
            Console.WriteLine(
                $"  NOTE: Using {evt.Source}'s pre-computed cost for {evt.Asset} " +
                "(insufficient buy history in provided files). " +
                "Provide full trade history for accurate genomsnittsmetoden.");
            costSek = evt.FallbackCostSek.Value;
        }
        else
        {
            Console.WriteLine(
                $"  WARNING: No cost basis for {evt.Asset} on " +
                $"{DateOnly.FromDateTime(evt.Timestamp)} — export full trade history.");
            costSek = computedCost; // 0
        }

        var proceedsSek = (long)Math.Round(evt.ValueSek - evt.FeeSek, MidpointRounding.AwayFromZero);
        var vinst   = proceedsSek > costSek ? proceedsSek - costSek : 0L;
        var forlust = costSek > proceedsSek ? costSek - proceedsSek : 0L;

        allRows.Add(new K4Row
        {
            Sektion          = "D",
            Antal            = evt.Quantity,
            Beteckning       = evt.Asset,
            Datum            = DateOnly.FromDateTime(evt.Timestamp).ToString("yyyy-MM-dd"),
            Forsaljningspris = proceedsSek,
            Omkostnadsbelopp = costSek,
            Vinst            = vinst,
            Forlust          = forlust,
        });
    }
}

// ── Collect direct K4 rows (Avanza stocks, Kraken margin trades) ──────────────
foreach (var (broker, files) in selections)
{
    Console.WriteLine($"  Reading direct rows from {broker.BrokerName}...");
    try
    {
        var rows = await broker.GetDirectRowsAsync(files, riksbank);
        if (rows.Count > 0)
            Console.WriteLine($"    -> {rows.Count} direct rows");
        allRows.AddRange(rows);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ERROR reading {broker.BrokerName}: {ex.Message}");
        return 1;
    }
}

// ── Generate SRU ─────────────────────────────────────────────────────────────
var gen            = new SruGenerator(person, year);
var blanketterCount = gen.Generate(allRows, outputDir);
ConsoleUi.ShowDone(outputDir, blanketterCount);

return 0;
