using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Sets up fallback rate rules so BTCPay's rate engine queries the arkadeassets
/// provider for asset ticker pairs. Without this, the default "X_X = coingecko(X_X)"
/// rule never consults our provider and cross-rates (e.g. BTC_ERICUSDT) fail.
/// </summary>
public static class ArkadeStoreCoinHelper
{
    private const string RuleMarker = "// arkade-asset-rates";

    /// <summary>
    /// Ensures the store's fallback rate settings route asset ticker pairs
    /// to the arkadeassets rate provider. Covers both stablecoins and store coins.
    /// </summary>
    public static void EnsureFallbackRateRules(StoreData store, ArkadePaymentMethodConfig? config)
    {
        var blob = store.GetStoreBlob();

        // Only stablecoins need fallback rate rules — they need BTCPay to query
        // arkadeassets for cross-rates like BTC_ERICUSDT (derived via ERICUSDT_USD peg).
        // Store coins (BEPSI) have no BTC rate and should NOT be in rate rules.
        var stablecoinTickers = config?.AcceptedAssets?
            .Where(a => a.PricingMode == AssetPricingMode.Stablecoin && !string.IsNullOrEmpty(a.Ticker))
            .Select(a => a.Ticker!.ToUpperInvariant())
            .Distinct()
            .ToList() ?? new List<string>();

        if (stablecoinTickers.Count == 0)
        {
            // Remove our fallback if no stablecoins remain
            if (blob.FallbackRateSettings?.RateScript?.Contains(RuleMarker) == true)
            {
                blob.FallbackRateSettings = null;
            }
            store.SetStoreBlob(blob);
            return;
        }

        // Route stablecoin ticker pairs to our provider so BTCPay can resolve
        // cross-rates like BTC_ERICUSDT. The X_X catchall ensures non-asset
        // pairs (like BTC_USD) still resolve via coingecko.
        var rules = new List<string> { RuleMarker };
        foreach (var ticker in stablecoinTickers)
        {
            rules.Add($"{ticker}_X = arkadeassets({ticker}_X);");
            rules.Add($"X_{ticker} = arkadeassets(X_{ticker});");
        }
        rules.Add("X_X = coingecko(X_X);");

        blob.FallbackRateSettings ??= new StoreBlob.RateSettings();
        blob.FallbackRateSettings.RateScripting = true;
        blob.FallbackRateSettings.RateScript = string.Join("\n", rules);
        store.SetStoreBlob(blob);
    }

    public static async Task ReloadCurrencies(IServiceProvider services)
    {
        var currencyNameTable = services.GetService<CurrencyNameTable>();
        if (currencyNameTable != null)
        {
            await currencyNameTable.ReloadCurrencyData(CancellationToken.None);
        }
    }
}
