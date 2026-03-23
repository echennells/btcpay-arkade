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

        var stablecoins = config?.AcceptedAssets?
            .Where(a => a.PricingMode == AssetPricingMode.Stablecoin && !string.IsNullOrEmpty(a.Ticker))
            .ToList() ?? new List<AcceptedAsset>();

        if (stablecoins.Count == 0)
        {
            if (blob.FallbackRateSettings?.RateScript?.Contains(RuleMarker) == true)
            {
                blob.FallbackRateSettings = null;
            }
            store.SetStoreBlob(blob);
            return;
        }

        // For each stablecoin we need two rules:
        //   ERICUSDT_X = arkadeassets(ERICUSDT_X)  → provides ERICUSDT_USD = pegRate
        //   BTC_ERICUSDT = coingecko(BTC_USD)      → derives BTC cross-rate for payout approval
        // The X_X catchall resolves BTC_USD etc. via coingecko.
        var rules = new List<string> { RuleMarker };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in stablecoins)
        {
            var ticker = asset.Ticker!.ToUpperInvariant();
            if (!seen.Add(ticker))
                continue;
            var pegCurrency = (asset.PegCurrency ?? "USD").ToUpperInvariant();
            rules.Add($"{ticker}_X = arkadeassets({ticker}_X);");
            if (asset.PegRate == 1m)
                rules.Add($"BTC_{ticker} = kraken(BTC_{pegCurrency});");
            else
                rules.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "BTC_{0} = kraken(BTC_{1}) / {2};", ticker, pegCurrency, asset.PegRate));
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
