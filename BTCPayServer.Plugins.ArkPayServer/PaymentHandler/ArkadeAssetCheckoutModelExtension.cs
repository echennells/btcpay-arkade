using BTCPayServer.Data;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeAssetCheckoutModelExtension : ICheckoutModelExtension
{
    private readonly IPaymentLinkExtension _paymentLinkExtension;

    public ArkadeAssetCheckoutModelExtension(IEnumerable<IPaymentLinkExtension> paymentLinkExtensions)
    {
        _paymentLinkExtension =
            paymentLinkExtensions
                .SingleOrDefault(p => p.PaymentMethodId == ArkadePlugin.ArkadeAssetPaymentMethodId) ??
            throw new InvalidOperationException("ArkadeAssetPaymentLinkExtension not found in DI");
    }

    public PaymentMethodId PaymentMethodId => ArkadePlugin.ArkadeAssetPaymentMethodId;

    public string Image => "arkade.svg";

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: ArkadeAssetPaymentMethodHandler })
            return;

        context.Model.CheckoutBodyComponentName = ArkadePlugin.AssetCheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;

        var paymentLink =
            _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper)
            ?? throw new Exception("Failed to generate Arkade asset payment link");

        context.Model.InvoiceBitcoinUrlQR =
            paymentLink
                .ToUpperInvariant()
                .Replace("BITCOIN:", "bitcoin:")
                .Replace("ARK=", "ark=");
        context.Model.InvoiceBitcoinUrl = paymentLink;
    }
}
