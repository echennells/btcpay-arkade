using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;

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
        if (context is not { Handler: ArkadeAssetPaymentMethodHandler handler })
            return;

        context.Model.CheckoutBodyComponentName = ArkadePlugin.AssetCheckoutBodyComponentName;
        context.Model.ShowRecommendedFee = false;

        var paymentLink =
            _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper)
            ?? throw new Exception("Failed to generate Arkade asset payment link");

        // QR: uppercase only the bech32m ark address (case-insensitive by spec),
        // leave case-sensitive values like hex asset IDs untouched.
        var dest = context.Prompt.Destination;
        context.Model.InvoiceBitcoinUrlQR = paymentLink.Replace(dest, dest.ToUpperInvariant());
        context.Model.InvoiceBitcoinUrl = paymentLink;

        // Pass asset options to the checkout Vue component via AdditionalData
        var promptDetails = handler.ParsePaymentPromptDetails(context.Prompt.Details);
        if (promptDetails.AssetOptions is { Count: > 0 })
        {
            var serializer = handler.Serializer;
            context.Model.AdditionalData["assetOptions"] = JToken.FromObject(promptDetails.AssetOptions, serializer);

            // Build payment links for each asset option so the checkout JS can swap QR codes
            var paymentLinks = new Dictionary<string, string>();
            foreach (var option in promptDetails.AssetOptions)
            {
                var link = ArkadeBip21Builder.Create()
                    .WithArkAddress(dest)
                    .WithAmount(option.Due)
                    .WithCustomParameter("assetid", option.AssetId)
                    .Build();
                paymentLinks[option.AssetId] = link;
            }
            context.Model.AdditionalData["assetPaymentLinks"] = JToken.FromObject(paymentLinks);
        }

        // Check for mismatched asset payments so the checkout page can warn the customer
        var mismatchedPayments = context.InvoiceEntity.GetPayments(false)
            .Where(p => p.PaymentMethodId == ArkadePlugin.ArkadeAssetPaymentMethodId
                        && p.Status == PaymentStatus.Unaccounted)
            .Select(p =>
            {
                var details = handler.ParsePaymentDetails(p.Details);
                if (!details.IsMismatchedAsset) return null;
                return new { ticker = details.ReceivedTicker ?? "unknown", amount = p.Value };
            })
            .Where(x => x != null)
            .ToList();

        if (mismatchedPayments.Count > 0)
        {
            context.Model.AdditionalData["mismatchedPayments"] = JToken.FromObject(mismatchedPayments);
        }
    }
}
