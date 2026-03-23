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
        ArkServerInfo serverInfo;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            serverInfo = await clientTransport.GetServerInfoAsync(cts.Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        // Reject invoices whose BTC equivalent is below the Ark dust threshold.
        // Such invoices can't settle via Lightning (Boltz minimum) or Arkade BTC,
        // and the reverse swap triggered by asset payment would also fail.
        {
            var price = context.InvoiceEntity.Price;
            var currency = context.InvoiceEntity.Currency;
            if (string.Equals(currency, "BTC", StringComparison.OrdinalIgnoreCase))
            {
                if (Money.Coins(price) < serverInfo.Dust)
                    throw new PaymentMethodUnavailableException("Amount too small");
            }
            else if (context.InvoiceEntity.TryGetRate("BTC", out var btcRate) && btcRate > 0)
            {
                var btcEquivalent = price / btcRate;
                if (Money.Coins(btcEquivalent) < serverInfo.Dust)
                    throw new PaymentMethodUnavailableException("Amount too small");
            }
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
                // Skip assets with invalid PegRate — a zero or negative rate would
                // cause division by zero or nonsensical conversion results.
                if (asset.PegRate <= 0)
                    continue;

                // Stablecoin: convert invoice amount to asset amount using peg rate.
                // For same-currency (USD invoice, USDT pegged to USD): due = price / pegRate
                // For cross-currency (USD invoice, EURT pegged to EUR): convert via forex rate
                if (string.Equals(invoiceCurrency, asset.PegCurrency, StringComparison.OrdinalIgnoreCase))
                {
                    due = invoicePrice / asset.PegRate;
                }
                else if (context.InvoiceEntity.TryGetRate(
                    new CurrencyPair(asset.PegCurrency, invoiceCurrency), out var crossRate) && crossRate > 0)
                {
                    // crossRate = price of 1 pegCurrency IN invoiceCurrency (e.g. EUR/USD = 1.09 means 1 EUR = 1.09 USD)
                    // To convert: priceInPegCurrency = invoicePrice / crossRate
                    // due = priceInPegCurrency / pegRate
                    due = invoicePrice / crossRate / asset.PegRate;
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
            // Rate BEPSI_BEPSI = 1 is already provided by ArkadeAssetRateProvider
            // and resolved during BeforeFetchingRates — no need to inject it again.
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

        // If the invoice is denominated in a store coin, set prompt currency to that
        // ticker so BTCPay fetches TICKER_TICKER = 1 (identity) instead of trying
        // ERICUSDT_TICKER which doesn't exist.
        var invoiceCurrency = context.InvoiceEntity.Currency;
        foreach (var asset in arkadeConfig.AcceptedAssets)
        {
            if (asset.PricingMode != AssetPricingMode.StoreCoin)
                continue;
            var meta = await assetMetadataService.GetAssetMetadata(asset.AssetId);
            var ticker = meta?.Ticker;
            if (ticker != null && string.Equals(ticker, invoiceCurrency, StringComparison.OrdinalIgnoreCase))
            {
                context.Prompt.Currency = ticker;
                context.Prompt.Divisibility = meta?.Decimals ?? 0;
                return; // store coin invoice — no stablecoin rates needed
            }
        }

        // Non-store-coin invoice: request rates for stablecoin tickers so BTCPay
        // fetches cross-rates (e.g. USD invoice → ERICUSDT pegged to USD).
        bool primarySet = false;
        foreach (var asset in arkadeConfig.AcceptedAssets)
        {
            if (asset.PricingMode != AssetPricingMode.Stablecoin)
                continue;

            var metadata = await assetMetadataService.GetAssetMetadata(asset.AssetId);
            var ticker = metadata?.Ticker ?? "ASSET";

            if (!primarySet)
            {
                context.Prompt.Currency = ticker;
                context.Prompt.Divisibility = metadata?.Decimals ?? 0;
                primarySet = true;
            }
            else
            {
                context.RequiredRates.Add(ticker);
            }
        }
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
        if (details is ArkadeAssetPromptDetails promptDetails)
        {
            promptDetails.WalletId = null;
            promptDetails.ContractString = null;
        }
    }
}
