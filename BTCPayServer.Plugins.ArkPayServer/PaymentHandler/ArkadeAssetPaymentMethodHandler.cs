using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Rating;
using BTCPayServer.Services;
using NArk.Core;
using NArk.Abstractions.Wallets;
using NArk.Abstractions.Contracts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeAssetPaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    IContractService contractService,
    IClientTransport clientTransport,
    AssetMetadataService assetMetadataService
) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadeAssetPaymentMethodId;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        try
        {
            await clientTransport.GetServerInfoAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        var store = context.Store;
        var configs = store.GetPaymentMethodConfigs();

        // Asset config is stored under the BTC ARKADE payment method
        if (!configs.TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var configToken))
            throw new PaymentMethodUnavailableException("Arkade payment method not configured");

        var arkadeConfig = configToken.ToObject<ArkadePaymentMethodConfig>(Serializer);
        if (arkadeConfig is null)
            throw new PaymentMethodUnavailableException("Arkade payment method not configured");

        if (arkadeConfig.AcceptedAssets is not { Count: > 0 })
            throw new PaymentMethodUnavailableException("No assets configured");

        // Build payment options for all accepted assets
        var invoicePrice = context.InvoiceEntity.Price;
        var invoiceCurrency = context.InvoiceEntity.Currency;
        var assetOptions = new List<AssetPaymentOption>();

        foreach (var asset in arkadeConfig.AcceptedAssets)
        {
            var meta = await assetMetadataService.GetAssetMetadata(asset.AssetId);
            var assetTicker = meta?.Ticker ?? "ASSET";
            var assetDecimals = meta?.Decimals ?? 0;

            decimal due;
            if (asset.PricingMode == AssetPricingMode.StoreCoin)
            {
                // Store coin: invoice must be denominated in this asset's ticker
                if (!string.Equals(invoiceCurrency, assetTicker, StringComparison.OrdinalIgnoreCase))
                    continue;
                due = invoicePrice;
            }
            else
            {
                // Stablecoin: convert invoice amount to asset amount using peg rate.
                // For same-currency (USD invoice, USDT pegged to USD): due = price / pegRate
                // For cross-currency (USD invoice, EURT pegged to EUR): convert via forex rate
                if (string.Equals(invoiceCurrency, asset.PegCurrency, StringComparison.OrdinalIgnoreCase))
                {
                    due = asset.PegRate > 0 ? invoicePrice / asset.PegRate : invoicePrice;
                }
                else if (context.InvoiceEntity.TryGetRate(
                    new CurrencyPair(asset.PegCurrency, invoiceCurrency), out var crossRate) && crossRate > 0)
                {
                    // crossRate = pegCurrency per invoiceCurrency (e.g. EUR/USD = 0.92)
                    // priceInPegCurrency = invoicePrice * crossRate
                    // due = priceInPegCurrency / pegRate
                    due = invoicePrice * crossRate / asset.PegRate;
                }
                else
                {
                    // No rate available for this currency pair — skip this asset
                    continue;
                }
            }

            assetOptions.Add(new AssetPaymentOption
            {
                AssetId = asset.AssetId,
                Ticker = assetTicker,
                DisplayName = asset.DisplayName,
                Decimals = assetDecimals,
                Due = due,
                PricingMode = asset.PricingMode.ToString()
            });
        }

        if (assetOptions.Count == 0)
            throw new PaymentMethodUnavailableException("No accepted assets match the invoice currency");

        // Use the first matching asset as the primary (for Prompt.Currency, rate injection, etc.)
        var primaryOption = assetOptions[0];
        var primaryAsset = arkadeConfig.AcceptedAssets.First(a => a.AssetId == primaryOption.AssetId);

        context.Prompt.Currency = primaryOption.Ticker;
        context.Prompt.Divisibility = primaryOption.Decimals;

        if (primaryAsset.PricingMode == AssetPricingMode.StoreCoin)
        {
            if (invoicePrice <= 0m)
                throw new PaymentMethodUnavailableException("Invoice price must be positive for asset payment");
            context.InvoiceEntity.AddRate(new CurrencyPair(primaryOption.Ticker, invoiceCurrency), 1m);
        }

        var contract = await contractService.DeriveContract(
            arkadeConfig.WalletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"asset-invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);

        var details = new ArkadeAssetPromptDetails(arkadeConfig.WalletId, contract, primaryOption.AssetId)
        {
            AssetOptions = assetOptions
        };
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Details = JObject.FromObject(details, Serializer);

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());
    }

    public async Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var store = context.Store;
        var configs = store.GetPaymentMethodConfigs();

        if (!configs.TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var configToken))
            return;

        var arkadeConfig = configToken.ToObject<ArkadePaymentMethodConfig>(Serializer);
        if (arkadeConfig?.AcceptedAssets is not { Count: > 0 })
            return;

        // Request rates for ALL stablecoin asset tickers so BTCPay fetches forex rates
        // we need for cross-currency conversion (e.g. USD invoice → EURT pegged to EUR).
        bool primarySet = false;
        foreach (var asset in arkadeConfig.AcceptedAssets)
        {
            if (asset.PricingMode != AssetPricingMode.Stablecoin)
                continue;

            var metadata = await assetMetadataService.GetAssetMetadata(asset.AssetId);
            var ticker = metadata?.Ticker ?? "ASSET";

            if (!primarySet)
            {
                // First stablecoin sets the prompt currency (BTCPay auto-adds it to RequiredRates)
                context.Prompt.Currency = ticker;
                context.Prompt.Divisibility = metadata?.Decimals ?? 0;
                primarySet = true;
            }
            else
            {
                // Additional stablecoins: explicitly request their rates
                context.RequiredRates.Add(ticker);
            }
        }
        // For store coins only: leave currency null here. We inject it in ConfigurePrompt
        // to avoid BTCPay trying to fetch rates for a non-existent pair.
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public ArkadeAssetPromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadeAssetPromptDetails>(Serializer)!;
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadePaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadeAssetPaymentMethodHandler)}");
    }

    public ArkadeAssetPaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadeAssetPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadeAssetPaymentData)}");
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}
