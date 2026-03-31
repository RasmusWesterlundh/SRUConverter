using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SruConverter.Services;

/// <summary>
/// Calls Riksbanken's SWEA API to look up exchange rates (SEK per foreign currency unit).
/// Series IDs follow the pattern SEK{CURRENCY}PMI (e.g. SEKUSDPMI).
/// Rates are cached in memory; only 2 API calls are made per currency per year
/// (one bulk daily fetch, one bulk monthly-average fetch).
/// </summary>
public class RiksbankService : IDisposable
{
    private const string BaseUrl = "https://api.riksbank.se/swea/v1/";
    private readonly HttpClient _http;
    private readonly Dictionary<string, decimal> _dailyCache   = new();  // key: "SEKUSDPMI:2025-01-17"
    private readonly Dictionary<string, decimal> _monthlyCache = new();  // key: "SEKUSDPMI:2025-01"
    private readonly HashSet<string> _fetchedYears = new();

    public RiksbankService()
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Returns the SEK rate for <paramref name="currency"/> on <paramref name="date"/>.
    /// Uses the exact daily rate when available (banking days); falls back to the
    /// monthly average when the date falls on a weekend or public holiday.
    /// Both daily and monthly data are bulk-fetched once per year per currency.
    /// </summary>
    public async Task<decimal> GetRateForDateAsync(string currency, DateOnly date)
    {
        var seriesId = $"SEK{currency.ToUpperInvariant()}PMI";
        await EnsureYearCachedAsync(seriesId, date.Year);

        // 1. Exact banking-day rate
        var dailyKey = $"{seriesId}:{date:yyyy-MM-dd}";
        if (_dailyCache.TryGetValue(dailyKey, out var exact)) return exact;

        // 2. Monthly average fallback (weekend / public holiday)
        var monthKey = $"{seriesId}:{date:yyyy-MM}";
        if (_monthlyCache.TryGetValue(monthKey, out var monthly)) return monthly;

        throw new Exception(
            $"Could not find a rate for {currency} on {date:yyyy-MM-dd} " +
            $"(tried daily rate and monthly average for {date:yyyy-MM}). " +
            $"Series: '{seriesId}'.");
    }

    /// <summary>Returns the annual average SEK rate for a currency in a given year.</summary>
    public async Task<decimal> GetAnnualAverageAsync(string currency, int year)
    {
        var seriesId = $"SEK{currency.ToUpperInvariant()}PMI";
        await EnsureYearCachedAsync(seriesId, year);

        // Sum monthly averages weighted by observation count would be ideal, but the
        // annual-aggregate endpoint is a single call and gives the authoritative figure.
        var url = $"ObservationAggregates/{seriesId}/Y/{year}-01-01/{year}-12-31";
        var aggs = await _http.GetFromJsonAsync<List<ObservationAggregate>>(url);
        var match = aggs?.FirstOrDefault(a => a.Year == year);
        if (match?.Average == null)
            throw new Exception($"No annual average found for {currency} in {year}.");
        return match.Average.Value;
    }

    public void Dispose() => _http.Dispose();

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task EnsureYearCachedAsync(string seriesId, int year)
    {
        var yearKey = $"{seriesId}:{year}";
        if (_fetchedYears.Contains(yearKey)) return;
        _fetchedYears.Add(yearKey); // mark before awaiting to avoid double-fetch

        await FetchDailyRatesAsync(seriesId, year);
        await FetchMonthlyAveragesAsync(seriesId, year);
    }

    private async Task FetchDailyRatesAsync(string seriesId, int year)
    {
        var response = await _http.GetAsync($"Observations/{seriesId}/{year}-01-01/{year}-12-31");
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync();
        var obs = System.Text.Json.JsonSerializer.Deserialize<List<Observation>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (obs == null) return;
        foreach (var o in obs)
            if (o.Date != null && o.Value.HasValue)
                _dailyCache[$"{seriesId}:{o.Date}"] = o.Value.Value;
    }

    private async Task FetchMonthlyAveragesAsync(string seriesId, int year)
    {
        var response = await _http.GetAsync($"ObservationAggregates/{seriesId}/M/{year}-01-01/{year}-12-31");
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync();
        var aggs = System.Text.Json.JsonSerializer.Deserialize<List<ObservationAggregate>>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (aggs == null) return;
        foreach (var a in aggs)
            if (a.SeqNr.HasValue && a.Average.HasValue)
            {
                var monthKey = $"{seriesId}:{year}-{a.SeqNr.Value:00}";
                _monthlyCache[monthKey] = a.Average.Value;
            }
    }

    // ── JSON models ───────────────────────────────────────────────────────────

    private class Observation
    {
        [JsonPropertyName("date")]  public string?  Date  { get; set; }
        [JsonPropertyName("value")] public decimal? Value { get; set; }
    }

    private class ObservationAggregate
    {
        [JsonPropertyName("year")]    public int?     Year    { get; set; }
        [JsonPropertyName("seqNr")]   public int?     SeqNr   { get; set; }
        [JsonPropertyName("average")] public decimal? Average { get; set; }
    }
}

