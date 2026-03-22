using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using NArk.Core;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NArk.Swaps.Boltz;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadePaymentMethodHandler(
    BTCPayServerEnvironment btcPayServerEnvironment,
    IContractService contractService,
    IClientTransport clientTransport,
    BoltzLimitsValidator? boltzLimitsValidator = null
) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadePaymentMethodId;

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        ArkServerInfo serverInfo;
        try
        {
            serverInfo = await clientTransport.GetServerInfoAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            throw new PaymentMethodUnavailableException("Ark operator unavailable");
        }

        var store = context.Store;

        if (ParsePaymentMethodConfig(store.GetPaymentMethodConfigs()[PaymentMethodId]) is not ArkadePaymentMethodConfig
            arkadePaymentMethodConfig)
        {
            throw new PaymentMethodUnavailableException("Arkade payment method not configured");
        }

        if (!arkadePaymentMethodConfig.AllowSubDustAmounts && Money.Coins(context.Prompt.Calculate().Due) < serverInfo.Dust)
        {
            var dustSats = serverInfo.Dust.ToUnit(MoneyUnit.Satoshi);
            throw new PaymentMethodUnavailableException($"Amount below minimum ({dustSats} sats)");
        }

        var contract = await contractService.DeriveContract(
            arkadePaymentMethodConfig.WalletId,
            NextContractPurpose.Receive,
            metadata: new Dictionary<string, string> { ["Source"] = $"invoice:{context.InvoiceEntity.Id}" },
            cancellationToken: CancellationToken.None);
        var details = new ArkadePromptDetails(arkadePaymentMethodConfig.WalletId, contract);
        var address = contract.GetArkAddress();

        context.Prompt.Destination = address.ToString(btcPayServerEnvironment.NetworkType == ChainName.Mainnet);
        context.Prompt.PaymentMethodFee = 0m;

        context.TrackedDestinations.Add(context.Prompt.Destination);
        context.TrackedDestinations.Add(address.ScriptPubKey.PaymentScript.ToHex());
        context.Prompt.Details = JObject.FromObject(details, Serializer);
    }

    public async Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "BTC";
        context.Prompt.Divisibility = 8;

        // Inject PaymentMethodCriteria for BTC-LNURL so BTCPay's built-in CheckCriteria
        // rejects it for amounts below the Boltz reverse swap minimum.
        // LNURL uses the same Arkade Lightning backend as BTC-LN, but its ConfigurePrompt
        // doesn't check amounts — it only creates a LNURL endpoint. Without this criteria,
        // LNURL would activate for small invoices that can never be fulfilled.
        if (boltzLimitsValidator != null)
        {
            try
            {
                var limits = await boltzLimitsValidator.GetLimitsAsync(isReverse: true);
                if (limits != null)
                {
                    var lnurlId = PaymentTypes.LNURL.GetPaymentMethodId("BTC");
                    var minBtc = Money.Satoshis(limits.MinAmount).ToDecimal(MoneyUnit.BTC);
                    var criteria = context.StoreBlob.PaymentMethodCriteria;

                    if (!criteria.Any(c => c.PaymentMethod == lnurlId))
                    {
                        // Atomic: assign a new list so concurrent enumerators on the old list are safe
                        var updated = new List<PaymentMethodCriteria>(criteria)
                        {
                            new()
                            {
                                PaymentMethod = lnurlId,
                                Value = new CurrencyValue { Value = minBtc, Currency = "BTC" },
                                Above = true
                            }
                        };
                        context.StoreBlob.PaymentMethodCriteria = updated;
                    }
                }
            }
            catch
            {
                // If Boltz limits are unavailable, don't block invoice creation
            }
        }
    }

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public ArkadePromptDetails ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<ArkadePromptDetails>(Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details);
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<ArkadePaymentMethodConfig>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentMethodHandler)}");
    }

    public ArkadePaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<ArkadePaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(ArkadePaymentData)}");
    }
    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    public void StripDetailsForNonOwner(object details)
    {
    }
}
