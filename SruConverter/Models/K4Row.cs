namespace SruConverter.Models;

public class K4Row
{
    public string Sektion { get; set; } = string.Empty; // "A" or "D"
    public decimal Antal { get; set; }
    public string Beteckning { get; set; } = string.Empty;
    public string Datum { get; set; } = string.Empty;
    public long Forsaljningspris { get; set; }
    public long Omkostnadsbelopp { get; set; }
    public long Vinst { get; set; }
    public long Forlust { get; set; }
}
