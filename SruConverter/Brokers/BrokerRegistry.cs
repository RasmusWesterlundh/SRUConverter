namespace SruConverter.Brokers;

/// <summary>
/// Central registry of all supported broker readers.
/// To add support for a new broker, create a class implementing <see cref="IBrokerReader"/>
/// and add an instance here — that is the only change required.
/// </summary>
public static class BrokerRegistry
{
    public static IReadOnlyList<IBrokerReader> All { get; } =
    [
        new AvanzaCsvReader(),
        new RevolutCsvReader(),
    ];
}
