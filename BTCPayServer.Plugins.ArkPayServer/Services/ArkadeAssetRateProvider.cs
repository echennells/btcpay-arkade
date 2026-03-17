using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Provides exchange rates for Arkade assets configured on stores.
/// For stablecoins (e.g. USDT pegged to USD at 1:1), returns the peg rate.
/// For store coins (e.g. BEPSI), returns an identity rate (1:1 to itself).
/// </summary>
public class ArkadeAssetRateProvider(
    StoreRepository storeRepository,
    AssetMetadataService assetMetadataService,
    ILogger<ArkadeAssetRateProvider> logger)
    : IContextualRateProvider
{
    public RateSourceInfo RateSourceInfo => new("arkadeassets", "Arkade Assets", "https://arkade.computer");

    public async Task<PairRate[]> GetRatesAsync(IRateContext context, CancellationToken cancellationToken)
    {
        var rates = new List<PairRate>();

        if (context is not IHasStoreIdRateContext storeContext)
            return rates.ToArray();

        try
        {
            var store = await storeRepository.FindStore(storeContext.StoreId);
            if (store is null)
                return rates.ToArray();

            var configs = store.GetPaymentMethodConfigs();
            if (!configs.TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var configToken))
                return rates.ToArray();

            var config = configToken.ToObject<ArkadePaymentMethodConfig>(
                BlobSerializer.CreateSerializer().Serializer);
            if (config?.AcceptedAssets is not { Count: > 0 })
                return rates.ToArray();

            foreach (var asset in config.AcceptedAssets)
            {
                var metadata = await assetMetadataService.GetAssetMetadata(asset.AssetId, cancellationToken);
                var ticker = metadata?.Ticker ?? "ASSET";

                if (asset.PricingMode == AssetPricingMode.Stablecoin)
                {
                    // e.g. USDT pegged to USD at rate 1 → USDT_USD = 1
                    // e.g. GOLD pegged to USD at rate 2000 → GOLD_USD = 2000
                    var pair = new CurrencyPair(ticker, asset.PegCurrency);
                    rates.Add(new PairRate(pair, new BidAsk(asset.PegRate)));
                }
                else
                {
                    // Store coin: identity rate, ticker to itself
                    // The invoice will be denominated directly in the asset
                    var pair = new CurrencyPair(ticker, ticker);
                    rates.Add(new PairRate(pair, new BidAsk(1m)));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build Arkade asset rates for store {StoreId}", storeContext.StoreId);
        }

        return rates.ToArray();
    }

    public Task<PairRate[]> GetRatesAsync(CancellationToken cancellationToken)
    {
        // Contextual provider — requires store context
        return Task.FromResult(Array.Empty<PairRate>());
    }
}
