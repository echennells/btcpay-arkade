using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

/// <summary>
/// Provides currency data for Arkade store coin tickers so BTCPay recognizes
/// them as valid currencies (e.g. for refund custom amounts).
/// </summary>
public class ArkadeCurrencyDataProvider(
    StoreRepository storeRepository,
    AssetMetadataService assetMetadataService,
    ILogger<ArkadeCurrencyDataProvider> logger)
    : CurrencyDataProvider
{
    public async Task<CurrencyData[]> LoadCurrencyData(CancellationToken cancellationToken)
    {
        var currencies = new List<CurrencyData>();

        try
        {
            var stores = await storeRepository.GetStores();
            foreach (var store in stores)
            {
                var configs = store.GetPaymentMethodConfigs();
                if (!configs.TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var configToken))
                    continue;

                var config = configToken.ToObject<ArkadePaymentMethodConfig>(
                    BlobSerializer.CreateSerializer().Serializer);
                if (config?.AcceptedAssets is not { Count: > 0 })
                    continue;

                foreach (var asset in config.AcceptedAssets)
                {
                    // Use persisted ticker if available, otherwise try to fetch
                    var ticker = asset.Ticker;
                    var decimals = asset.Decimals ?? 0;

                    if (string.IsNullOrEmpty(ticker))
                    {
                        try
                        {
                            var metadata = await assetMetadataService.GetAssetMetadata(asset.AssetId, cancellationToken);
                            ticker = metadata?.Ticker;
                            decimals = metadata?.Decimals ?? 0;
                        }
                        catch
                        {
                            // Skip assets we can't resolve
                            continue;
                        }
                    }

                    if (string.IsNullOrEmpty(ticker))
                        continue;

                    currencies.Add(new CurrencyData
                    {
                        Code = ticker.ToUpperInvariant(),
                        Name = asset.DisplayName ?? ticker,
                        Divisibility = decimals,
                        Symbol = ticker.ToUpperInvariant(),
                        Crypto = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load Arkade store coin currencies");
        }

        // Deduplicate by code
        return currencies
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }
}
