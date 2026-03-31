using System.Text.Json.Serialization;

namespace SruConverter.Services;

/// <summary>JSON model for a single Riksbanken daily observation.</summary>
internal class Observation
{
    [JsonPropertyName("date")]  public string?  Date  { get; set; }
    [JsonPropertyName("value")] public decimal? Value { get; set; }
}

/// <summary>JSON model for a Riksbanken aggregate (weekly/monthly/yearly).</summary>
internal class ObservationAggregate
{
    [JsonPropertyName("year")]    public int?     Year    { get; set; }
    [JsonPropertyName("seqNr")]   public int?     SeqNr   { get; set; }
    [JsonPropertyName("average")] public decimal? Average { get; set; }
}
