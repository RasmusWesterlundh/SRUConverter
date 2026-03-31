using SruConverter.Brokers;
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
var allRows = new List<SruConverter.Models.K4Row>();
using var riksbank = new RiksbankService();

foreach (var (broker, files) in selections)
{
    Console.WriteLine($"  Reading {broker.BrokerName}...");
    try
    {
        var rows = await broker.ReadAsync(files, riksbank);
        Console.WriteLine($"    -> {rows.Count} rows");
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
