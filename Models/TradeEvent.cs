namespace SruConverter.Models;

public enum TradeKind { Buy, Sell }

/// <summary>
/// A single chronological buy or sell event contributed by a broker reader.
/// All monetary values are in SEK.
///
/// These events are collected from all brokers, sorted chronologically, then
/// processed through a single <see cref="SruConverter.Services.CryptoHoldingsState"/>
/// to implement Skatteverket's genomsnittsmetoden across all exchanges at once.
/// </summary>
public record TradeEvent
{
    /// <summary>Date and time of the transaction (used for chronological ordering).</summary>
    public required DateTime  Timestamp       { get; init; }

    /// <summary>Asset symbol, e.g. "BTC", "ETH", "SEI".</summary>
    public required string    Asset           { get; init; }

    /// <summary>Quantity of the asset bought or sold.</summary>
    public required decimal   Quantity        { get; init; }

    /// <summary>
    /// Gross value in SEK:
    ///   Buy  → cost before adding fee (fee added separately to cost basis)
    ///   Sell → gross proceeds before deducting fee
    /// </summary>
    public required decimal   ValueSek        { get; init; }

    /// <summary>Transaction fee in SEK.</summary>
    public required decimal   FeeSek          { get; init; }

    /// <summary>Whether this is an acquisition or a disposal.</summary>
    public required TradeKind Kind            { get; init; }

    /// <summary>Broker display name, used in console log lines.</summary>
    public required string    Source          { get; init; }

    /// <summary>
    /// Pre-computed cost basis in SEK (from the broker's own calculation).
    /// Used as a fallback for Sell events when the shared
    /// <see cref="SruConverter.Services.CryptoHoldingsState"/> has insufficient
    /// history (e.g. Revolut disposals when no crypto-account-statement was provided).
    /// Null when no fallback is available.
    /// </summary>
    public          long?     FallbackCostSek { get; init; }
}
