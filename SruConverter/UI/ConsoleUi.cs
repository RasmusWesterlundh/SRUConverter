using SruConverter.Brokers;
using SruConverter.Models;

namespace SruConverter.UI;

/// <summary>
/// Interactive console UI for SruConverter.
/// Collects all runtime configuration from the user — no config files required.
/// Uses only System.Console; no external NuGet dependencies.
/// </summary>
public static class ConsoleUi
{
    // ── Colour helpers ────────────────────────────────────────────────────────
    private static void Write(string text, ConsoleColor fg)
    {
        Console.ForegroundColor = fg;
        Console.Write(text);
        Console.ResetColor();
    }
    private static void WriteLine(string text, ConsoleColor fg) { Write(text, fg); Console.WriteLine(); }

    // ── Logo ──────────────────────────────────────────────────────────────────
    public static void ShowLogo()
    {
        Console.Clear();
        WriteLine(@"  ____  ____  _   _     ____                          _             ", ConsoleColor.Cyan);
        WriteLine(@" / ___||  _ \| | | |   / ___|___  _ ____   _____ _ __| |_ ___ _ __ ", ConsoleColor.Cyan);
        WriteLine(@" \___ \| |_) | | | |  | |   / _ \| '_ \ \ / / _ \ '__| __/ _ \ '__|", ConsoleColor.Cyan);
        WriteLine(@"  ___) |  _ <| |_| |  | |__| (_) | | | \ V /  __/ |  | ||  __/ |   ", ConsoleColor.Cyan);
        WriteLine(@" |____/|_| \_\\___/    \____\___/|_| |_|\_/ \___|_|   \__\___|_|   ", ConsoleColor.Cyan);
        Console.WriteLine();
        WriteLine("K4 Generator; Swedish Skatteverket K4 (2025P4)", ConsoleColor.DarkCyan);
        Console.WriteLine();
    }

    // ── Personal info — save/load ─────────────────────────────────────────────

    private static string PersonalDetailsPath =>
        Path.Combine(AppContext.BaseDirectory, "PersonalDetails.json");

    private static PersonInfo? LoadSavedPersonInfo()
    {
        try
        {
            if (!File.Exists(PersonalDetailsPath)) return null;
            var json = File.ReadAllText(PersonalDetailsPath);
            return System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.PersonInfo);
        }
        catch { return null; }
    }

    private static void SavePersonInfo(PersonInfo p)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(p, AppJsonContext.Default.PersonInfo);
            File.WriteAllText(PersonalDetailsPath, json);
        }
        catch (Exception ex)
        {
            WriteLine($"  Warning: could not save personal details: {ex.Message}", ConsoleColor.Yellow);
        }
    }

    // ── Personal info ─────────────────────────────────────────────────────────

    /// <summary>
    /// If a previously saved PersonalDetails.json is found, offers to reuse it.
    /// Otherwise (or if the user declines) collects details interactively and saves them.
    /// Returns (person, usedSaved) — the caller skips PromptYear if usedSaved is true.
    /// </summary>
    public static PersonInfo CollectPersonInfo()
    {
        var saved = LoadSavedPersonInfo();
        if (saved != null)
        {
            WriteLine("── Sparade uppgifter / Saved personal details ──────────────", ConsoleColor.Yellow);
            Console.WriteLine();
            Write("  Personnr   : ", ConsoleColor.DarkGray); WriteLine(saved.Personnummer, ConsoleColor.White);
            Write("  Name       : ", ConsoleColor.DarkGray); WriteLine(saved.Namn,         ConsoleColor.White);
            Write("  Address    : ", ConsoleColor.DarkGray); WriteLine(saved.Adress,        ConsoleColor.White);
            Write("  Postnummer : ", ConsoleColor.DarkGray); WriteLine(saved.Postnummer,    ConsoleColor.White);
            Write("  Postort    : ", ConsoleColor.DarkGray); WriteLine(saved.Postort,       ConsoleColor.White);
            if (!string.IsNullOrEmpty(saved.Epost))
            { Write("  E-post     : ", ConsoleColor.DarkGray); WriteLine(saved.Epost,       ConsoleColor.White); }
            Console.WriteLine();
            Write("  Use these details? [Y/n]: ", ConsoleColor.Yellow);
            var ans = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
            Console.WriteLine();
            if (ans == "" || ans == "y" || ans == "yes")
                return saved;
        }
        else
        {
            WriteLine("── Dina uppgifter (Your personal details) ──────────────────", ConsoleColor.Yellow);
            Console.WriteLine();
        }

        var personnummer = PromptValidated(
            "Personnummer (YYYYMMDD-XXXX): ",
            s =>
            {
                var digits = s.Replace("-", "");
                return digits.Length == 12 && digits.All(char.IsDigit);
            },
            "Must be 12 digits, e.g. 19900101-1234 or 199001011234",
            transform: s => s.Replace("-", ""));

        var namn    = Prompt("Namn (Full name): ");
        var adress  = Prompt("Adress (Street address): ");
        var postnr  = PromptValidated(
            "Postnummer (123 45): ",
            s =>
            {
                var digits = s.Replace(" ", "");
                return digits.Length == 5 && digits.All(char.IsDigit);
            },
            "Must be 5 digits, e.g. 11122 or 111 22",
            transform: s => s.Replace(" ", ""));
        var postort = Prompt("Postort (City): ");
        var epost   = Prompt("E-post (Email, optional): ", optional: true);

        Console.WriteLine();
        var person = new PersonInfo
        {
            Personnummer = personnummer,
            Namn         = namn,
            Adress       = adress,
            Postnummer   = postnr,
            Postort      = postort,
            Epost        = epost,
        };

        SavePersonInfo(person);
        return person;
    }

    // ── Tax year ─────────────────────────────────────────────────────────────
    public static int PromptYear()
    {
        return int.Parse(PromptValidated(
            $"Inkomstår / Tax year (default {DateTime.Now.Year - 1}): ",
            s => s == "" || (int.TryParse(s, out var y) && y >= 2020 && y <= DateTime.Now.Year),
            $"Enter a year between 2020 and {DateTime.Now.Year}, or press Enter for default.",
            allowEmpty: true,
            emptyDefault: (DateTime.Now.Year - 1).ToString()));
    }

    // ── Broker multi-select ───────────────────────────────────────────────────
    public static IReadOnlyList<IBrokerReader> SelectBrokers(IReadOnlyList<IBrokerReader> brokers)
    {
        WriteLine("── Välj mäklare / Select brokers ───────────────────────────", ConsoleColor.Yellow);
        WriteLine("  Arrow keys to move  ·  Space to toggle  ·  Enter to confirm  ·  ? for help", ConsoleColor.DarkGray);
        Console.WriteLine();

        var selected = new bool[brokers.Count];
        int cursor   = 0;

        void Render()
        {
            for (int i = 0; i < brokers.Count; i++)
                Console.Write("\r\x1b[1A");
            Console.Write("\r");

            for (int i = 0; i < brokers.Count; i++)
            {
                var check    = selected[i] ? "[x]" : "[ ]";
                var arrow    = i == cursor ? "> " : "  ";
                var nameClr  = selected[i] ? ConsoleColor.Green : ConsoleColor.White;
                var arrowClr = i == cursor ? ConsoleColor.Cyan  : ConsoleColor.DarkGray;
                Write(arrow, arrowClr);
                Write(check + " ", selected[i] ? ConsoleColor.Green : ConsoleColor.DarkGray);
                Write(brokers[i].BrokerName, nameClr);
                Write("  ", ConsoleColor.DarkGray);
                WriteLine("[?]", i == cursor ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray);
            }
        }

        // Initial render
        for (int i = 0; i < brokers.Count; i++)
        {
            Write(i == cursor ? "> " : "  ", ConsoleColor.Cyan);
            Write("[ ] ", ConsoleColor.DarkGray);
            Write(brokers[i].BrokerName, ConsoleColor.White);
            Write("  ", ConsoleColor.DarkGray);
            WriteLine("[?]", i == cursor ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray);
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.KeyChar == '?')
            {
                ShowBrokerHelp(brokers[cursor]);
                Console.Clear();
                ShowLogo();
                WriteLine("── Välj mäklare / Select brokers ───────────────────────────", ConsoleColor.Yellow);
                WriteLine("  Arrow keys to move  ·  Space to toggle  ·  Enter to confirm  ·  ? for help", ConsoleColor.DarkGray);
                Console.WriteLine();
                for (int i = 0; i < brokers.Count; i++)
                {
                    Write(i == cursor ? "> " : "  ", i == cursor ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
                    Write(selected[i] ? "[x] " : "[ ] ", selected[i] ? ConsoleColor.Green : ConsoleColor.DarkGray);
                    Write(brokers[i].BrokerName, selected[i] ? ConsoleColor.Green : ConsoleColor.White);
                    Write("  ", ConsoleColor.DarkGray);
                    WriteLine("[?]", i == cursor ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray);
                }
                continue;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    cursor = (cursor - 1 + brokers.Count) % brokers.Count;
                    Render();
                    break;
                case ConsoleKey.DownArrow:
                    cursor = (cursor + 1) % brokers.Count;
                    Render();
                    break;
                case ConsoleKey.Spacebar:
                    selected[cursor] = !selected[cursor];
                    Render();
                    break;
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var chosen = brokers.Where((_, i) => selected[i]).ToList();
                    if (chosen.Count == 0) { WriteLine("  Please select at least one broker.", ConsoleColor.Red); break; }
                    return chosen;
            }
        }
    }

    private static void ShowBrokerHelp(IBrokerReader broker)
    {
        Console.Clear();
        WriteLine($"── Help: {broker.BrokerName} ────────────────────────────────────", ConsoleColor.Yellow);
        Console.WriteLine();
        WriteLine("  How to get the required export file:", ConsoleColor.DarkGray);
        Console.WriteLine();

        // Word-wrap HelpText at ~72 chars
        const int wrap = 72;
        var words = broker.HelpText.Split(' ');
        var line  = new System.Text.StringBuilder("  ");
        foreach (var word in words)
        {
            if (line.Length + word.Length + 1 > wrap + 2)
            {
                WriteLine(line.ToString(), ConsoleColor.White);
                line.Clear();
                line.Append("  ");
            }
            if (line.Length > 2) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 2) WriteLine(line.ToString(), ConsoleColor.White);

        if (!string.IsNullOrEmpty(broker.HelpUrl))
        {
            Console.WriteLine();
            Write("  More info: ", ConsoleColor.DarkGray);
            WriteLine(broker.HelpUrl, ConsoleColor.Cyan);
        }

        Console.WriteLine();
        Write("  Accepted file formats: ", ConsoleColor.DarkGray);
        WriteLine(string.Join(", ", broker.SupportedExtensions), ConsoleColor.White);
        Console.WriteLine();
        WriteLine("  Press any key to return...", ConsoleColor.DarkGray);
        Console.ReadKey(intercept: true);
    }

    // ── Per-broker file collection ────────────────────────────────────────────
    public static async Task<List<string>> CollectFilesAsync(IBrokerReader broker)
    {
        WriteLine($"── {broker.BrokerName}: {broker.FilePrompt} ─────────────────", ConsoleColor.Yellow);
        var exts = string.Join(", ", broker.SupportedExtensions);
        WriteLine($"  Accepted formats: {exts}", ConsoleColor.DarkGray);
        WriteLine("  Enter full file path(s). Tab-completes paths. Press Enter on an empty line when done.", ConsoleColor.DarkGray);
        Console.WriteLine();

        var files = new List<string>();
        while (true)
        {
            var input = ReadPath($"  File {files.Count + 1} (or Enter to finish): ").Trim();

            // Strip one layer of surrounding quotes added by Windows Explorer
            if (input.Length >= 2 && input[0] == '"' && input[^1] == '"')
                input = input[1..^1].Trim();

            if (string.IsNullOrEmpty(input))
            {
                if (files.Count == 0)
                    WriteLine("  At least one file is required.", ConsoleColor.Red);
                else
                    break;
                continue;
            }

            if (!File.Exists(input))
            {
                WriteLine($"  File not found: {input}", ConsoleColor.Red);
                continue;
            }

            // Validate via broker's own converter/validator
            Write("  Validating...", ConsoleColor.DarkGray);
            var error = await broker.ValidateFileAsync(input);
            // Clear the "Validating..." line
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");

            if (error != null)
            {
                WriteLine($"  ✗ {error}", ConsoleColor.Red);
                continue;
            }

            files.Add(input);
            WriteLine($"  ✓ {Path.GetFileName(input)}", ConsoleColor.Green);
        }

        Console.WriteLine();
        return files;
    }

    // ── Output directory ──────────────────────────────────────────────────────
    public static string PromptOutputDir(string defaultDir)
    {
        WriteLine("── Output folder ───────────────────────────────────────────", ConsoleColor.Yellow);
        Write("  Default: ", ConsoleColor.White);
        WriteLine(defaultDir, ConsoleColor.Cyan);
        var input = ReadPath("  Enter a different path (or press Enter to use default): ").Trim();
        Console.WriteLine();

        if (input.Length >= 2 && input[0] == '"' && input[^1] == '"')
            input = input[1..^1].Trim();

        if (!string.IsNullOrEmpty(input))
        {
            Directory.CreateDirectory(input);
            return Path.GetFullPath(input);
        }
        Directory.CreateDirectory(defaultDir);
        return Path.GetFullPath(defaultDir);
    }

    // ── Summary + confirm ─────────────────────────────────────────────────────
    public static bool ConfirmSummary(PersonInfo person, int year,
        IEnumerable<(IBrokerReader broker, List<string> files)> selections,
        string outputDir)
    {
        WriteLine("── Summary ─────────────────────────────────────────────────", ConsoleColor.Yellow);
        Console.WriteLine();
        Write("  Tax year   : ", ConsoleColor.DarkGray); WriteLine(year.ToString(),        ConsoleColor.White);
        Write("  Name       : ", ConsoleColor.DarkGray); WriteLine(person.Namn,            ConsoleColor.White);
        Write("  Personnr   : ", ConsoleColor.DarkGray); WriteLine(person.Personnummer,    ConsoleColor.White);
        Write("  Output dir : ", ConsoleColor.DarkGray); WriteLine(outputDir,              ConsoleColor.Cyan);
        Console.WriteLine();
        foreach (var (broker, files) in selections)
        {
            Write($"  {broker.BrokerName,-12}: ", ConsoleColor.DarkGray);
            WriteLine($"{files.Count} file(s)", ConsoleColor.White);
            foreach (var f in files)
                WriteLine($"    • {Path.GetFileName(f)}", ConsoleColor.DarkGray);
        }
        Console.WriteLine();

        Write("  Generate SRU files? [Y/n]: ", ConsoleColor.Yellow);
        var ans = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
        Console.WriteLine();
        return ans == "" || ans == "y" || ans == "yes";
    }

    // ── Done ──────────────────────────────────────────────────────────────────
    public static void ShowDone(string outputDir, int blanketterCount)
    {
        Console.WriteLine();
        WriteLine("  ✓ Done!", ConsoleColor.Green);
        WriteLine($"  {blanketterCount} K4 blanketter written to:", ConsoleColor.White);
        WriteLine($"  {outputDir}", ConsoleColor.Cyan);
        Console.WriteLine();
        WriteLine("  Files: BLANKETTER.SRU  +  INFO.SRU", ConsoleColor.White);
        Console.WriteLine();
    }

    // ── Tab-completing path reader ────────────────────────────────────────────

    /// <summary>
    /// Reads a file path from the console with Tab completion.
    /// A "In: &lt;folder&gt;" context line is shown above the prompt and kept live
    /// as the user types — it updates whenever the directory portion changes.
    /// Tab cycles through matching paths; Shift+Tab cycles backwards.
    /// </summary>
    private static string ReadPath(string prompt)
    {
        var buffer     = new System.Text.StringBuilder();
        var matches    = new List<string>();
        int matchIndex = -1;
        string shownContextDir = "";

        string GetContextDir()
        {
            var current = buffer.ToString();
            if (string.IsNullOrEmpty(current))
                return Directory.GetCurrentDirectory();
            try
            {
                var full = Path.GetFullPath(current);
                return Directory.Exists(full) ? full
                       : (Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory());
            }
            catch { return Directory.GetCurrentDirectory(); }
        }

        // Capture the row of the context line before first render so we can
        // jump back to it reliably (avoids ANSI cursor-up fragility).
        int contextRow = 0;
        try { contextRow = Console.CursorTop; } catch { /* non-interactive stdout */ }

        int SafeWidth() { try { return Math.Max(Console.WindowWidth - 1, 1); } catch { return 79; } }

        // Redraws both the context line and the input line in-place.
        void Redraw()
        {
            var ctx = GetContextDir();
            shownContextDir = ctx;

            try { Console.SetCursorPosition(0, contextRow); }
            catch { Console.Write("\r"); }  // fallback: best-effort

            // Overwrite context line
            Console.Write("\r" + new string(' ', SafeWidth()) + "\r");
            Write("  In: ", ConsoleColor.DarkGray);
            Write(ctx, ConsoleColor.DarkCyan);
            Console.WriteLine();

            // Overwrite input line
            Console.Write("\r" + new string(' ', SafeWidth()) + "\r");
            Write(prompt, ConsoleColor.White);
            Console.Write(buffer.ToString());
        }

        // Initial render: context line + prompt
        shownContextDir = GetContextDir();
        Write("  In: ", ConsoleColor.DarkGray);
        WriteLine(shownContextDir, ConsoleColor.DarkCyan);
        Write(prompt, ConsoleColor.White);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    matches.Clear(); matchIndex = -1;
                    Redraw();
                }
                continue;
            }

            if (key.Key == ConsoleKey.Tab)
            {
                var current = buffer.ToString();

                // Recompute matches when buffer changed since last Tab
                if (matches.Count == 0 || matchIndex < 0)
                {
                    matches.Clear();
                    matchIndex = -1;
                    try
                    {
                        var dir    = Path.GetDirectoryName(current) ?? ".";
                        var prefix = Path.GetFileName(current);
                        if (string.IsNullOrEmpty(dir)) dir = ".";

                        var candidates = new List<string>();
                        if (Directory.Exists(dir))
                        {
                            candidates.AddRange(Directory.GetDirectories(dir, prefix + "*")
                                .Select(d => d + Path.DirectorySeparatorChar));
                            candidates.AddRange(Directory.GetFiles(dir, prefix + "*"));
                        }
                        matches.AddRange(candidates.OrderBy(x => x));
                    }
                    catch { /* ignore FS errors during completion */ }
                }

                if (matches.Count == 0) continue;

                // Cycle forward (Tab) or backward (Shift+Tab)
                if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    matchIndex = (matchIndex - 1 + matches.Count) % matches.Count;
                else
                    matchIndex = (matchIndex + 1) % matches.Count;

                buffer.Clear();
                buffer.Append(matches[matchIndex]);
                Redraw();
                continue;
            }

            // Any other printable character
            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                matches.Clear(); matchIndex = -1;

                var newCtx = GetContextDir();
                if (newCtx != shownContextDir)
                    Redraw();          // directory portion changed — refresh context line
                else
                    Console.Write(key.KeyChar);  // just echo the character
            }
        }
    }

    // ── Private prompt helpers ────────────────────────────────────────────────

    private static string Prompt(string label, bool optional = false)
    {
        while (true)
        {
            Write("  " + label, ConsoleColor.White);
            var input = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrEmpty(input) || optional) return input;
            WriteLine("  This field is required.", ConsoleColor.Red);
        }
    }

    private static string PromptValidated(string label, Func<string, bool> validate,
        string hint, bool allowEmpty = false, string emptyDefault = "",
        Func<string, string>? transform = null)
    {
        while (true)
        {
            Write("  " + label, ConsoleColor.White);
            var raw = Console.ReadLine()?.Trim() ?? "";
            if (allowEmpty && raw == "") return emptyDefault;
            if (validate(raw))
                return transform != null ? transform(raw) : raw;
            WriteLine($"  Invalid — {hint}", ConsoleColor.Red);
        }
    }
}
