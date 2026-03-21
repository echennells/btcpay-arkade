using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Payouts;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Plugins.ArkPayServer.Services;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.Wallets;
using NArk.Core.Transport;
using NBitcoin;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

public class ArkAutomatedPayoutProcessor: BaseAutomatedPayoutProcessor<ArkAutomatedPayoutBlob>
{
    private readonly IClientTransport _clientTransport;
    private readonly ArkadeSpendingService _arkSpendingService;
    private readonly PayoutMethodHandlerDictionary _payoutMethodHandlers;
    private readonly PaymentMethodHandlerDictionary _paymentHandlers;
    private readonly BTCPayNetworkJsonSerializerSettings _jsonSerializerSettings;

    public ArkAutomatedPayoutProcessor(
        IClientTransport clientTransport,
        ILoggerFactory logger,
        StoreRepository storeRepository,
        PayoutProcessorData payoutProcessorSettings,
        ApplicationDbContextFactory applicationDbContextFactory,
        PaymentMethodHandlerDictionary paymentHandlers,
        IPluginHookService pluginHookService,
        EventAggregator eventAggregator,
        ArkadeSpendingService arkSpendingService,
        PayoutMethodHandlerDictionary payoutMethodHandlers,
        BTCPayNetworkJsonSerializerSettings jsonSerializerSettings,
        IWalletProvider walletProvider
    )
        : base(ArkadePlugin.ArkadePaymentMethodId, logger, storeRepository, payoutProcessorSettings, applicationDbContextFactory, paymentHandlers, pluginHookService, eventAggregator)
    {
        _clientTransport = clientTransport;
        _arkSpendingService = arkSpendingService;
        _payoutMethodHandlers = payoutMethodHandlers;
        _paymentHandlers = paymentHandlers;
        _jsonSerializerSettings = jsonSerializerSettings;
    }

    protected override async Task Process(object paymentMethodConfig, List<PayoutData> payouts)
    {
        var payoutHandler = (ArkPayoutHandler)_payoutMethodHandlers[ArkadePlugin.ArkadePayoutMethodId];

        var terms = await _clientTransport.GetServerInfoAsync();

        var storeData = await _storeRepository.FindStore(PayoutProcessorSettings.StoreId) ??
            throw new InvalidOperationException("Could not find store by StoreId");

        // Look up store's accepted assets for asset payout detection
        var arkConfig = storeData.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
            ArkadePlugin.ArkadePaymentMethodId, _paymentHandlers);

        foreach (var payout in payouts)
        {
            if (payoutHandler.PayoutLocker.LockOrNullAsync(payout.Id, 0) is { } locker && await locker is {} disposable)
            {
                using (disposable)
                {
                    if (payout.GetPayoutMethodId() != PayoutMethodId)
                        continue;

                    if (payout.Proof is not null)
                        continue;

                    // Check if this is an asset payout by matching OriginalCurrency against accepted asset tickers
                    AcceptedAsset? matchedAsset = null;
                    if (payout.OriginalCurrency is not null && payout.OriginalCurrency != "BTC" &&
                        arkConfig?.AcceptedAssets is { Count: > 0 })
                    {
                        matchedAsset = arkConfig.AcceptedAssets.FirstOrDefault(a =>
                            string.Equals(a.Ticker, payout.OriginalCurrency, StringComparison.OrdinalIgnoreCase));
                    }

                    if (matchedAsset is null)
                    {
                        // Regular BTC payout — check dust threshold
                        var amount = new Money(payout.Amount.Value, MoneyUnit.BTC);
                        if (amount < terms.Dust)
                        {
                            payout.State = PayoutState.Cancelled;
                            continue;
                        }
                    }

                    var blob = payout.GetBlob(_jsonSerializerSettings);
                    var claim = await payoutHandler.ParseClaimDestination(blob.Destination, CancellationToken.None);
                    var destinationBip21 = await payoutHandler.TryGenerateBip21(payout, claim);

                    if (destinationBip21 is not null)
                    {
                        try
                        {
                            string? txId;
                            if (matchedAsset is not null)
                            {
                                // Asset payout: convert display amount to native units
                                var decimals = matchedAsset.Decimals ?? 0;
                                var multiplier = (decimal)Math.Pow(10, decimals);
                                var nativeAmount = (ulong)(payout.OriginalAmount * multiplier);

                                txId = await _arkSpendingService.Spend(storeData, destinationBip21,
                                    matchedAsset.AssetId, nativeAmount, CancellationToken.None);
                            }
                            else
                            {
                                txId = await _arkSpendingService.Spend(storeData, destinationBip21, CancellationToken.None);
                            }

                            payoutHandler.SetProofBlob(payout, new ArkPayoutProof { TransactionId = uint256.Parse(txId) });
                            if (!string.IsNullOrEmpty(txId))
                                payout.State = PayoutState.Completed;
                        }
                        catch (Exception e)
                        {
                            Logs.PayServer.LogError(e, "Failed to process Arkade payout {PayoutId}", payout.Id);
                        }
                    }
                    else
                        payout.State = PayoutState.Cancelled;
                }
            }
        }
    }
}