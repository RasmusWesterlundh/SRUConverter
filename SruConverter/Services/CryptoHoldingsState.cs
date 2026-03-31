namespace SruConverter.Services;

/// <summary>
/// Shared per-asset average-cost state implementing Skatteverket's genomsnittsmetoden.
///
/// A single instance is created in Program.cs and passed to all broker readers
/// so that purchases on different exchanges are aggregated before the cost basis
/// is computed for any disposal event.
///
/// Per Skatteverket: all purchases, sales and exchanges of the same type of crypto
/// across all centralized/decentralized exchanges and wallets must be included in
/// the genomsnittsmetoden calculation.
/// </summary>
public sealed class CryptoHoldingsState
{
    private readonly Dictionary<string, AssetHolding> _holdings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records a purchase, updating the running average cost per unit.
    /// Total cost = ValueSek + FeeSek (fee is added to cost basis per Skatteverket).
    /// </summary>
    public void Buy(string asset, decimal qty, decimal totalCostSek)
        => GetOrCreate(asset).Buy(qty, totalCostSek);

    /// <summary>
    /// Deducts <paramref name="qty"/> from holdings and returns the SEK cost basis.
    /// Also returns whether there was sufficient tracked history.
    /// </summary>
    public (long CostSek, bool Sufficient) Sell(string asset, decimal qty)
    {
        var h = GetOrCreate(asset);
        bool sufficient = h.HeldQty >= qty * 0.999m;
        return (h.Sell(qty), sufficient);
    }

    private AssetHolding GetOrCreate(string asset)
    {
        if (!_holdings.TryGetValue(asset, out var h))
            _holdings[asset] = h = new AssetHolding();
        return h;
    }
}

/// <summary>Per-asset running average-cost state.</summary>
public sealed class AssetHolding
{
    public decimal HeldQty    { get; private set; }
    public decimal AvgCostSek { get; private set; }

    /// <summary>
    /// Records a purchase and recomputes the weighted average cost per unit.
    /// </summary>
    public void Buy(decimal qty, decimal totalCostSek)
    {
        if (qty <= 0m) return;
        AvgCostSek = HeldQty == 0m
            ? totalCostSek / qty
            : (HeldQty * AvgCostSek + totalCostSek) / (HeldQty + qty);
        HeldQty += qty;
    }

    /// <summary>
    /// Deducts <paramref name="qty"/> from holdings.
    /// Returns the SEK cost basis for this lot (qty × avg cost per unit).
    /// </summary>
    public long Sell(decimal qty)
    {
        if (qty <= 0m) return 0L;
        var costBasis = (long)Math.Round(qty * AvgCostSek, MidpointRounding.AwayFromZero);
        HeldQty = Math.Max(0m, HeldQty - qty);
        return costBasis;
    }
}
