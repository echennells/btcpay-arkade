namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// How the asset's price relates to fiat currencies.
/// </summary>
public enum AssetPricingMode
{
    /// <summary>
    /// Asset is pegged to a fiat currency (e.g. 1 USDT = 1 USD).
    /// BTCPay converts the invoice price to asset amount using PegRate.
    /// </summary>
    Stablecoin,

    /// <summary>
    /// Asset has no fiat peg (e.g. BEPSI loyalty token).
    /// The invoice amount IS the asset amount — no conversion.
    /// </summary>
    StoreCoin
}

public record AcceptedAsset(
    string AssetId,
    string? DisplayName = null,
    AssetPricingMode PricingMode = AssetPricingMode.Stablecoin,
    string PegCurrency = "USD",
    decimal PegRate = 1m);

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    List<AcceptedAsset>? AcceptedAssets = null);
