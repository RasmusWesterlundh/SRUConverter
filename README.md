# SRU Converter

A cross-platform .NET console app that generates Swedish tax declaration files (`.SRU`) for **K4** from broker export files.

Supports **Avanza** (stocks & ETFs), **Revolut** (crypto), and **Kraken** (crypto spot, swaps, and margin trades). Trades in foreign currencies are automatically converted to actual day-of-trade exchange rates based on [Riksbanken's public API](https://www.riksbank.se/sv/statistik/sok-rantor--valutakurser/).

---

## Output

The program writes two files to `./output/` (relative to wherever you run it):

| File | Contents |
|---|---|
| `BLANKETTER.SRU` | K4 declarations — Sektion A (listed securities) and/or Sektion D (crypto) |
| `INFO.SRU` | Personal details header required by Skatteverket |

These files are uploaded to Skatteverket via **Mina sidor → Inkomstdeklaration → Bifoga fil**.

---

## Usage

Download the latest release from the [Releases page](../../releases) and run the executable. The program will guide you through:

1. Entering your personal details (personnummer, name, address)
2. Selecting which brokers to include
3. Providing the export file(s) for each broker
4. Confirming the output directory

No installation required — it's a self-contained single-file executable.

---

## Broker export files

### Avanza

**Sektion A** (listed shares and ETFs).

Export from: **Min ekonomi → Transaktioner → Exportera till Excel**

Select date range **1 Jan – 31 Dec** of the tax year. The `.xlsx` file contains all buy/sell transactions.

### Revolut

**Sektion D** (crypto).

Two files are needed:

**1. Crypto Account Statement**
Export from: **Profile → Documents → Crypto → Crypto Account Statement**
Select date range **1 Jan – 31 Dec**.

**2. Trading Account Statement**
Export from: **Profile → Documents → Stock trading → Trading Account Statement**
Select date range **1 Jan – 31 Dec**.

> **Note on "Send" transactions:** Revolut crypto sends with a non-zero SEK value are treated as taxable disposals (Skatteverket considers paying with crypto a taxable event). If any sends were transfers to your own wallet on another exchange, those rows will be over-reported — verify each one manually.

> **Note on Learn rewards:** Revolut "Learn" crypto rewards are imported as purchases at cost 0 SEK. You must separately declare their market value as **Tjänsteinkomst** (income from service).

### Kraken

**Sektion D** (crypto spot trades, crypto-to-crypto swaps, and margin/leveraged trades).

Two files are needed:

**1. Spot & Margin Trades**
Export from: **History → Export → Trades** — date range **1 Jan – 31 Dec**.
File is named `kraken_spot_trades_*.csv`.

**2. Ledger (for crypto swaps)**
Export from: **History → Export → Ledgers** — date range **1 Jan – 31 Dec**.
File is named `kraken_stocks_etfs_ledgers_*.csv`.

---

## Architecture

```
IBrokerReader
├── GetTradeEventsAsync()   → raw Buy/Sell events (crypto)
└── GetDirectRowsAsync()    → pre-computed K4 rows (stocks, margin)
```

All crypto trade events from all brokers are merged, sorted chronologically, and processed through a single `CryptoHoldingsState` that implements **genomsnittsmetoden** (weighted average cost) as required by Skatteverket. This ensures the correct cost basis even when the same asset has been bought across multiple exchanges.

Direct rows (Avanza stocks, Kraken margin trades) bypass this shared state and are appended to the output as-is.

### Adding a broker

Implement `IBrokerReader` and register the class in `BrokerRegistry.cs`. The two-stage pipeline means:
- Crypto spot brokers: implement `GetTradeEventsAsync`
- Stock/pre-computed brokers: implement `GetDirectRowsAsync`
- Brokers with both: implement both

---

## Building from source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
cd c#/SruConverter
dotnet build
dotnet run
```

To publish a self-contained Windows executable:

```sh
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true
```

---

## Disclaimer

This tool is provided as-is. Always verify the generated `.SRU` files against your own records before submitting to Skatteverket. The author takes no responsibility for incorrect tax declarations.
