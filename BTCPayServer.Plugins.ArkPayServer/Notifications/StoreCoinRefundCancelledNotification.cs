using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Controllers;
using BTCPayServer.Services.Notifications;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.ArkPayServer.Notifications;

/// <summary>
/// Notification raised when a store-coin refund payout is cancelled by
/// <see cref="Payouts.Ark.ArkPayoutHandler.TrackClaim"/>. Store coins have no
/// BTC exchange rate, so they cannot flow through BTCPay's payout pipeline,
/// and the merchant must issue refunds manually from the Ark wallet.
/// </summary>
public class StoreCoinRefundCancelledNotification : BaseNotification
{
    private const string TYPE = "arkade-storecoin-refund-cancelled";

    internal class Handler : NotificationHandler<StoreCoinRefundCancelledNotification>
    {
        private readonly LinkGenerator _linkGenerator;
        private readonly BTCPayServerOptions _options;
        private IStringLocalizer StringLocalizer { get; }

        public Handler(LinkGenerator linkGenerator, BTCPayServerOptions options, IStringLocalizer stringLocalizer)
        {
            _linkGenerator = linkGenerator;
            _options = options;
            StringLocalizer = stringLocalizer;
        }

        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta =>
            [(TYPE, StringLocalizer["Arkade store-coin refund cancelled"])];

        protected override void FillViewModel(StoreCoinRefundCancelledNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = StringLocalizer[
                "Refund cancelled: {0} cannot be refunded through BTCPay because store coins have no BTC exchange rate. Issue this refund manually from your Ark wallet.",
                notification.Currency ?? "store coin"];
            vm.ActionLink = _linkGenerator.GetPathByAction(
                nameof(UIStorePullPaymentsController.Payouts),
                "UIStorePullPayments",
                new
                {
                    storeId = notification.StoreId,
                    payoutMethodId = notification.PaymentMethod,
                    payoutState = PayoutState.Cancelled
                },
                _options.RootPath);
        }
    }

    public string PayoutId { get; set; }
    public string StoreId { get; set; }
    public string PaymentMethod { get; set; }
    public string Currency { get; set; }

    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
