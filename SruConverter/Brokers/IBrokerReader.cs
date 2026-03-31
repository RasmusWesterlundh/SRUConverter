using SruConverter.Models;
using SruConverter.Services;

namespace SruConverter.Brokers;

/// <summary>
/// Implemented by every broker-specific reader.
/// Implementations are registered in <see cref="BrokerRegistry"/>.
/// </summary>
public interface IBrokerReader
{
    /// <summary>Display name shown in the UI, e.g. "Avanza".</summary>
    string BrokerName { get; }

    /// <summary>
    /// File extensions this reader accepts, e.g. [".csv"] or [".csv", ".xlsx"].
    /// Used to filter the file-open dialog hint.
    /// </summary>
    string[] SupportedExtensions { get; }

    /// <summary>Short description shown when prompting for files.</summary>
    string FilePrompt { get; }

    /// <summary>
    /// One-paragraph explanation of what export file is needed and how to obtain it
    /// from this broker. Shown when the user presses ? in the broker selection screen.
    /// </summary>
    string HelpText { get; }

    /// <summary>
    /// URL to the broker's help page for exporting the required data.
    /// Empty string if no specific URL is available.
    /// </summary>
    string HelpUrl { get; }

    /// <summary>
    /// Validates that <paramref name="filePath"/> can be read by this broker reader.
    /// Returns <c>null</c> on success, or a user-friendly error message on failure.
    /// Called during UI file collection so the user gets immediate feedback.
    /// </summary>
    Task<string?> ValidateFileAsync(string filePath);

    /// <summary>
    /// Reads one or more export files from this broker and returns a flat list of K4 rows.
    /// Multiple files are supported to cover multiple accounts at the same broker.
    /// Foreign-currency amounts must be converted to SEK using <paramref name="riksbank"/>.
    /// Only called after <see cref="ValidateFileAsync"/> has returned <c>null</c> for each file.
    /// </summary>
    Task<List<K4Row>> ReadAsync(IEnumerable<string> filePaths, RiksbankService riksbank);
}
