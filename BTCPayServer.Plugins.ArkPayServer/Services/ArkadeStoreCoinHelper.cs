using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Helpers for registering store coin tickers as BTCPay currencies and
/// setting up rate rules so that refund/payout approval can find rates.
/// </summary>
public static class ArkadeStoreCoinHelper
{
    /// <summary>
    /// Marker prefix used in rate scripts to identify plugin-managed rules.
    /// </summary>
    private const string RuleMarkerPrefix = "// arkade-store-coin-rates";

    /// <summary>
    /// Updates the store's fallback rate settings with identity rates (BTC_TICKER = 1)
    /// for each configured store coin. This allows the payout approval system to find
    /// a rate when converting between BTC (the Ark payout handler's currency) and the
    /// store coin ticker.
    /// </summary>
    public static void UpdateStoreCoinRateRules(StoreData store, ArkadePaymentMethodConfig? config)
    {
        var blob = store.GetStoreBlob();

        // Collect all store coin tickers
        var storeCoins = config?.AcceptedAssets?
            .Where(a => a.PricingMode == AssetPricingMode.StoreCoin && !string.IsNullOrEmpty(a.Ticker))
            .Select(a => a.Ticker!.ToUpperInvariant())
            .Distinct()
            .ToList() ?? new List<string>();

        if (storeCoins.Count == 0)
        {
            // Remove fallback if it was ours
            if (blob.FallbackRateSettings?.RateScript?.Contains(RuleMarkerPrefix) == true)
            {
                blob.FallbackRateSettings = null;
            }
            store.SetStoreBlob(blob);
            return;
        }

        // Build identity rate rules for each store coin ticker
        var rules = new List<string> { RuleMarkerPrefix };
        foreach (var ticker in storeCoins)
        {
            // BTC_BEPSI = 1 means "1 BTC = 1 BEPSI" for rate math purposes.
            // This is an identity mapping: the payout amount passes through as-is.
            rules.Add($"BTC_{ticker} = 1;");
            rules.Add($"{ticker}_BTC = 1;");
        }

        blob.FallbackRateSettings ??= new StoreBlob.RateSettings();
        blob.FallbackRateSettings.RateScripting = true;
        blob.FallbackRateSettings.RateScript = string.Join("\n", rules);
        store.SetStoreBlob(blob);
    }

    /// <summary>
    /// Reloads the currency name table so newly-added store coin tickers
    /// are recognized as valid currencies.
    /// </summary>
    public static async Task ReloadCurrencies(IServiceProvider services)
    {
        var currencyNameTable = services.GetService<CurrencyNameTable>();
        if (currencyNameTable != null)
        {
            await currencyNameTable.ReloadCurrencyData(CancellationToken.None);
        }
    }
}
