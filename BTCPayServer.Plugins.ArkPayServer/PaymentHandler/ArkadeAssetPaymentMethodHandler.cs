using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Services;
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

        var acceptedAsset = arkadeConfig.AcceptedAssets[0];
        var metadata = await assetMetadataService.GetAssetMetadata(acceptedAsset.AssetId);
        var ticker = metadata?.Ticker ?? "ASSET";
        var decimals = metadata?.Decimals ?? 0;

        context.Prompt.Currency = ticker;
        context.Prompt.Divisibility = decimals;

        if (acceptedAsset.PricingMode == AssetPricingMode.StoreCoin)
        {
            // Store coin: invoice price IS the asset amount, no conversion.
            // Inject identity rate so BTCPay calculates: due = price / 1 = price
            var invoicePrice = context.InvoiceEntity.Price;
            if (invoicePrice <= 0m)
                throw new PaymentMethodUnavailableException("Invoice price must be positive for asset payment");

#pragma warning disable CS0618
            context.InvoiceEntity.Rates[ticker] = 1m;
#pragma warning restore CS0618
        }
        // For stablecoins: rate is already provided by ArkadeAssetRateProvider
        // via BTCPay's normal rate fetching flow. No injection needed here.

        var contract = await contractService.DeriveContract(
            arkadeConfig.WalletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"asset-invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);

        var details = new ArkadeAssetPromptDetails(arkadeConfig.WalletId, contract, acceptedAsset.AssetId);
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Details = JObject.FromObject(details, Serializer);

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());
    }

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        var store = context.Store;
        var configs = store.GetPaymentMethodConfigs();

        if (!configs.TryGetValue(ArkadePlugin.ArkadePaymentMethodId, out var configToken))
            return Task.CompletedTask;

        var arkadeConfig = configToken.ToObject<ArkadePaymentMethodConfig>(Serializer);
        if (arkadeConfig?.AcceptedAssets is not { Count: > 0 })
            return Task.CompletedTask;

        var acceptedAsset = arkadeConfig.AcceptedAssets[0];

        if (acceptedAsset.PricingMode == AssetPricingMode.Stablecoin)
        {
            // For stablecoins, we need to fetch the asset metadata to get the ticker,
            // then set the prompt currency so BTCPay adds it to RequiredRates.
            // The ArkadeAssetRateProvider will supply the rate (e.g. USDT_USD = 1).
            var metadata = assetMetadataService.GetAssetMetadata(acceptedAsset.AssetId)
                .GetAwaiter().GetResult();
            var ticker = metadata?.Ticker ?? "ASSET";

            context.Prompt.Currency = ticker;
            context.Prompt.Divisibility = metadata?.Decimals ?? 0;
        }
        // For store coins: leave currency null here. We inject it in ConfigurePrompt
        // to avoid BTCPay trying to fetch rates for a non-existent pair.

        return Task.CompletedTask;
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
