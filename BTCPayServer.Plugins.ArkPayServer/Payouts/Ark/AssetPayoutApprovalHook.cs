using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Payments;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.ArkPayServer.Payouts.Ark;

/// <summary>
/// Approves asset payouts stuck in AwaitingApproval (BTCPay can't find a BTC rate
/// for store coins). Runs before each automated payout cycle via plugin hook.
/// </summary>
public class AssetPayoutApprovalHook(
    ApplicationDbContextFactory dbContextFactory,
    PaymentMethodHandlerDictionary paymentHandlers,
    ILogger<AssetPayoutApprovalHook> logger
) : IPluginHookAction
{
    public string Hook => "before-automated-payout-processing";

    public async Task Execute(object args)
    {
        logger.LogInformation("AssetPayoutApprovalHook fired, args type: {Type}", args?.GetType().Name);
        if (args is not BeforePayoutActionData data || data.ProcessorData.PayoutMethodId != "ARKADE")
            return;
        logger.LogInformation("AssetPayoutApprovalHook processing store {StoreId}", data.ProcessorData.StoreId);

        var arkConfig = data.Store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
            ArkadePlugin.ArkadePaymentMethodId, paymentHandlers);
        if (arkConfig?.AcceptedAssets is not { Count: > 0 })
            return;

        await using var ctx = dbContextFactory.CreateContext();
        var pending = await PullPaymentHostedService.GetPayouts(
            new PullPaymentHostedService.PayoutQuery
            {
                States = new[] { PayoutState.AwaitingApproval },
                PayoutMethods = new[] { data.ProcessorData.PayoutMethodId },
                Stores = new[] { data.ProcessorData.StoreId }
            }, ctx, CancellationToken.None);

        logger.LogInformation("Store {StoreId}: Found {Count} AwaitingApproval payouts, assets: {Assets}",
            data.ProcessorData.StoreId, pending.Count, string.Join(", ", arkConfig.AcceptedAssets.Select(a => a.Ticker)));
        foreach (var payout in pending)
        {
            if (payout.OriginalCurrency is null || payout.OriginalCurrency == "BTC")
                continue;
            if (arkConfig.AcceptedAssets.Any(a =>
                string.Equals(a.Ticker, payout.OriginalCurrency, StringComparison.OrdinalIgnoreCase)))
            {
                payout.State = PayoutState.AwaitingPayment;
                payout.Amount = payout.OriginalAmount;
            }
        }

        await ctx.SaveChangesAsync();
    }
}
