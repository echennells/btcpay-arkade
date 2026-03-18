namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkBalancesViewModel
{
    public decimal AvailableBalance { get; set; }
    public decimal LockedBalance { get; set; }
    public decimal RecoverableBalance { get; set; }
    public decimal UnspendableBalance { get; set; }
    public List<AssetBalanceViewModel> AssetBalances { get; set; } = [];
}

public class AssetBalanceViewModel
{
    public string AssetId { get; set; } = "";
    public string? Name { get; set; }
    public string? Ticker { get; set; }
    public ulong Amount { get; set; }
    public int Decimals { get; set; }
    public string DisplayName => Ticker ?? Name ?? TruncatedAssetId;
    public string TruncatedAssetId => AssetId.Length > 16 ? $"{AssetId[..8]}...{AssetId[^8..]}" : AssetId;

    public string FormattedAmount
    {
        get
        {
            if (Decimals <= 0) return Amount.ToString();
            var divisor = 1m;
            for (var i = 0; i < Decimals; i++) divisor *= 10m;
            return ((decimal)Amount / divisor).ToString($"F{Decimals}");
        }
    }
}
