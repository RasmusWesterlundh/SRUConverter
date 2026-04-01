using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Implemented by every broker-specific reader.
/// Implementations are registered in <see cref="BrokerRegistry"/>.
///
/// The broker pipeline has two stages:
///
/// 1. <see cref="GetTradeEventsAsync"/> — crypto brokers emit raw Buy/Sell events.
///    All events from all brokers are sorted chronologically and processed through
///    a single <see cref="CryptoHoldingsState"/> for correct cross-broker
///    genomsnittsmetoden (as required by Skatteverket).
///
/// 2. <see cref="GetDirectRowsAsync"/> — brokers emit pre-computed K4 rows that
///    bypass the shared state (Avanza stocks, Kraken margin trades).
/// </summary>
public interface IBrokerReader
{
    /// <summary>Display name shown in the UI, e.g. "Avanza".</summary>
    string BrokerName { get; }

    /// <summary>
    /// File extensions this reader accepts, e.g. [".csv"] or [".csv", ".xlsx"].
    /// </summary>
    string[] SupportedExtensions { get; }

    /// <summary>Short description shown when prompting for files.</summary>
    string FilePrompt { get; }

    /// <summary>
    /// One-paragraph explanation of what export file is needed and how to obtain it.
    /// Shown when the user presses ? in the broker selection screen.
    /// </summary>
    string HelpText { get; }

    /// <summary>URL to the broker's help page. Empty string if none.</summary>
    string HelpUrl { get; }

    /// <summary>
    /// Validates that <paramref name="filePath"/> can be read by this broker reader.
    /// Returns <c>null</c> on success, or a user-friendly error message on failure.
    /// </summary>
    Task<string?> ValidateFileAsync(string filePath);

    /// <summary>
    /// Returns raw chronological trade events for the shared cross-broker
    /// genomsnittsmetoden. Crypto brokers (Revolut, Kraken spot) implement this.
    /// Default: empty list — non-crypto brokers (Avanza) do not participate.
    /// </summary>
    Task<List<TradeEvent>> GetTradeEventsAsync(
        IEnumerable<string> filePaths, RiksbankService riksbank)
        => Task.FromResult(new List<TradeEvent>());

    /// <summary>
    /// Returns K4 rows that bypass the shared genomsnittsmetoden.
    /// Used for: Avanza stocks (pre-computed SEK values), Kraken margin trades.
    /// Default: empty list.
    /// </summary>
    Task<List<K4Row>> GetDirectRowsAsync(
        IEnumerable<string> filePaths, RiksbankService riksbank)
        => Task.FromResult(new List<K4Row>());
}

