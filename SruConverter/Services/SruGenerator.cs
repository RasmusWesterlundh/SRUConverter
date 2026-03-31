using System.Text;
using SruConverter.Models;

namespace SruConverter.Services;

public class SruGenerator
{
    // Sektion A: max 9 rows per blankett, field counter starts at 10 (codes 3100–3185)
    private const int MaxSektionAPerPage = 9;
    // Sektion C: max 7 rows per blankett, field counter starts at 31 (codes 3310–3375)
    private const int MaxSektionCPerPage = 7;
    // Sektion D: max 7 rows per blankett, field counter starts at 41 (codes 3410–3475)
    private const int MaxSektionDPerPage = 7;

    private readonly PersonInfo _person;
    private readonly int _year;

    public SruGenerator(PersonInfo person, int year)
    {
        _person = person;
        _year = year;
    }

    public int Generate(List<K4Row> rows, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        var sektionA = rows.Where(r => r.Sektion == "A").ToList();
        var sektionC = rows.Where(r => r.Sektion == "C").ToList();
        var sektionD = rows.Where(r => r.Sektion == "D").ToList();

        var blankets = BuildBlankets(sektionA, sektionC, sektionD);

        WriteBlanketter(blankets, Path.Combine(outputDir, "BLANKETTER.SRU"));
        WriteInfo(Path.Combine(outputDir, "INFO.SRU"));

        Console.WriteLine($"Sektion A: {sektionA.Count} rows");
        Console.WriteLine($"Sektion C: {sektionC.Count} rows");
        Console.WriteLine($"Sektion D: {sektionD.Count} rows");
        Console.WriteLine($"K4 blanketter: {blankets.Count}");
        Console.WriteLine($"Output written to: {outputDir}");
        return blankets.Count;
    }

    // ── Blankett building ────────────────────────────────────────────────────

    private List<Blankett> BuildBlankets(List<K4Row> sektionA, List<K4Row> sektionC, List<K4Row> sektionD)
    {
        var blankets = new List<Blankett>();
        int blankettNumber = 1;

        // Sektion A pages
        for (int i = 0; i < sektionA.Count; i += MaxSektionAPerPage)
        {
            var pageRows = sektionA.Skip(i).Take(MaxSektionAPerPage).ToList();
            if (pageRows.Count == 0) break;

            bool isLastAPage = i + MaxSektionAPerPage >= sektionA.Count;
            blankets.Add(new Blankett
            {
                Number = blankettNumber++,
                SektionARows = pageRows,
                IncludeSektionASummary = isLastAPage,
                TotalSektionA = isLastAPage ? Summarize(sektionA) : null,
            });
        }

        // Sektion C pages (field codes 3310–3375, summary 3400–3404)
        for (int i = 0; i < sektionC.Count; i += MaxSektionCPerPage)
        {
            var pageRows = sektionC.Skip(i).Take(MaxSektionCPerPage).ToList();
            if (pageRows.Count == 0) break;

            bool isLastCPage = i + MaxSektionCPerPage >= sektionC.Count;
            blankets.Add(new Blankett
            {
                Number = blankettNumber++,
                SektionCRows = pageRows,
                IncludeSektionCSummary = isLastCPage,
                TotalSektionC = isLastCPage ? Summarize(sektionC) : null,
            });
        }

        // Sektion D pages (field codes 3410–3475, summary 3500–3504)
        for (int i = 0; i < sektionD.Count; i += MaxSektionDPerPage)
        {
            var pageRows = sektionD.Skip(i).Take(MaxSektionDPerPage).ToList();
            if (pageRows.Count == 0) break;

            bool isLastDPage = i + MaxSektionDPerPage >= sektionD.Count;
            blankets.Add(new Blankett
            {
                Number = blankettNumber++,
                SektionDRows = pageRows,
                IncludeSektionDSummary = isLastDPage,
                TotalSektionD = isLastDPage ? Summarize(sektionD) : null,
            });
        }

        return blankets;
    }

    private static SectionTotal Summarize(List<K4Row> rows) => new()
    {
        Forsaljningspris = rows.Sum(r => r.Forsaljningspris),
        Omkostnadsbelopp = rows.Sum(r => r.Omkostnadsbelopp),
        Vinst  = rows.Sum(r => r.Vinst),
        Forlust = rows.Sum(r => r.Forlust),
    };

    // ── BLANKETTER.SRU ───────────────────────────────────────────────────────

    private void WriteBlanketter(List<Blankett> blankets, string filePath)
    {
        // Swedish tax authority requires ISO-8859-1
        var enc = Encoding.GetEncoding("ISO-8859-1");
        var now = DateTime.Now;
        var dateStr = now.ToString("yyyyMMdd");
        var timeStr = now.ToString("HHmmss");

        using var writer = new StreamWriter(filePath, append: false, encoding: enc);

        foreach (var b in blankets)
        {
            writer.WriteLine($"#BLANKETT K4-{_year}P4");
            writer.WriteLine($"#IDENTITET {_person.Personnummer} {dateStr} {timeStr}");
            writer.WriteLine($"#NAMN {_person.Namn}");

            // Sektion A rows (counter 10–18 → field codes 3100–3185)
            int counter = 10;
            foreach (var row in b.SektionARows)
            {
                WriteRow(writer, counter, row);
                counter++;
            }

            // Sektion A summary (3300–3305)
            if (b.IncludeSektionASummary && b.TotalSektionA != null)
            {
                var t = b.TotalSektionA;
                writer.WriteLine($"#UPPGIFT 3300 {t.Forsaljningspris}");
                writer.WriteLine($"#UPPGIFT 3301 {t.Omkostnadsbelopp}");
                if (t.Vinst > 0)   writer.WriteLine($"#UPPGIFT 3304 {t.Vinst}");
                if (t.Forlust > 0) writer.WriteLine($"#UPPGIFT 3305 {t.Forlust}");
            }

            // Sektion C rows (counter 31–37 → field codes 3310–3375)
            counter = 31;
            foreach (var row in b.SektionCRows)
            {
                WriteRow(writer, counter, row);
                counter++;
            }

            // Sektion C summary (3400–3404)
            if (b.IncludeSektionCSummary && b.TotalSektionC != null)
            {
                var t = b.TotalSektionC;
                writer.WriteLine($"#UPPGIFT 3400 {t.Forsaljningspris}");
                writer.WriteLine($"#UPPGIFT 3401 {t.Omkostnadsbelopp}");
                if (t.Vinst > 0)   writer.WriteLine($"#UPPGIFT 3403 {t.Vinst}");
                if (t.Forlust > 0) writer.WriteLine($"#UPPGIFT 3404 {t.Forlust}");
            }

            // Sektion D rows (counter 41–47 → field codes 3410–3475, Antal is Decimal12_8)
            counter = 41;
            foreach (var row in b.SektionDRows)
            {
                WriteRow(writer, counter, row, integerAntal: false);
                counter++;
            }

            // Sektion D summary (3500–3504)
            if (b.IncludeSektionDSummary && b.TotalSektionD != null)
            {
                var t = b.TotalSektionD;
                writer.WriteLine($"#UPPGIFT 3500 {t.Forsaljningspris}");
                writer.WriteLine($"#UPPGIFT 3501 {t.Omkostnadsbelopp}");
                if (t.Vinst > 0)   writer.WriteLine($"#UPPGIFT 3503 {t.Vinst}");
                if (t.Forlust > 0) writer.WriteLine($"#UPPGIFT 3504 {t.Forlust}");
            }

            writer.WriteLine($"#UPPGIFT 7014 {b.Number}");
            writer.WriteLine("#BLANKETTSLUT");
        }

        writer.WriteLine("#FIL_SLUT");
    }

    private static void WriteRow(StreamWriter writer, int counter, K4Row row, bool integerAntal = true)
    {
        // Sektion A & C: Antal must be Numeriskt_B (integer). Round, minimum 1 if > 0.
        // Sektion D: Antal is Decimal12_8 (decimals allowed).
        string antalStr;
        if (integerAntal)
        {
            var rounded = (long)Math.Round(row.Antal, MidpointRounding.AwayFromZero);
            if (rounded == 0 && row.Antal > 0) rounded = 1; // preserve non-zero row
            antalStr = rounded.ToString();
        }
        else
        {
            antalStr = row.Antal == Math.Floor(row.Antal)
                ? ((long)row.Antal).ToString()
                : row.Antal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        writer.WriteLine($"#UPPGIFT 3{counter}0 {antalStr}");
        writer.WriteLine($"#UPPGIFT 3{counter}1 {row.Beteckning}");
        writer.WriteLine($"#UPPGIFT 3{counter}2 {row.Forsaljningspris}");
        writer.WriteLine($"#UPPGIFT 3{counter}3 {row.Omkostnadsbelopp}");
        if (row.Vinst > 0)   writer.WriteLine($"#UPPGIFT 3{counter}4 {row.Vinst}");
        if (row.Forlust > 0) writer.WriteLine($"#UPPGIFT 3{counter}5 {row.Forlust}");
    }

    // ── INFO.SRU ─────────────────────────────────────────────────────────────

    private void WriteInfo(string filePath)
    {
        var enc = Encoding.GetEncoding("ISO-8859-1");
        using var writer = new StreamWriter(filePath, append: false, encoding: enc);

        writer.WriteLine("#DATABESKRIVNING_START");
        writer.WriteLine("#PRODUKT SRU");
        writer.WriteLine("#FILNAMN BLANKETTER.SRU");
        writer.WriteLine("#DATABESKRIVNING_SLUT");
        writer.WriteLine("#MEDIELEV_START");
        writer.WriteLine($"#ORGNR {_person.Personnummer}");
        writer.WriteLine($"#NAMN {_person.Namn}");
        writer.WriteLine($"#ADRESS {_person.Adress}");
        writer.WriteLine($"#POSTNR {_person.Postnummer}");
        writer.WriteLine($"#POSTORT {_person.Postort}");
        writer.WriteLine($"#EMAIL {_person.Epost}");
        writer.WriteLine("#MEDIELEV_SLUT");
    }

    // ── Inner types ──────────────────────────────────────────────────────────

    private class Blankett
    {
        public int Number { get; set; }
        public List<K4Row> SektionARows { get; set; } = new();
        public List<K4Row> SektionCRows { get; set; } = new();
        public List<K4Row> SektionDRows { get; set; } = new();
        public bool IncludeSektionASummary { get; set; }
        public bool IncludeSektionCSummary { get; set; }
        public bool IncludeSektionDSummary { get; set; }
        public SectionTotal? TotalSektionA { get; set; }
        public SectionTotal? TotalSektionC { get; set; }
        public SectionTotal? TotalSektionD { get; set; }
    }

    private class SectionTotal
    {
        public long Forsaljningspris { get; set; }
        public long Omkostnadsbelopp { get; set; }
        public long Vinst  { get; set; }
        public long Forlust { get; set; }
    }
}
