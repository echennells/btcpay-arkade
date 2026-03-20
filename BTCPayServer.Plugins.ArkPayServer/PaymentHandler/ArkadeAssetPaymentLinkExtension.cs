using BTCPayServer.Payments;
using BTCPayServer.Plugins.ArkPayServer.Lightning;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public class ArkadeAssetPaymentLinkExtension : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = ArkadePlugin.ArkadeAssetPaymentMethodId;

    public string GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        var amount = prompt.Calculate().Due;
        var assetId = prompt.Details?["AssetId"]?.ToString();

        var builder = ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount);

        // Include asset ID so the payer's wallet knows which asset is expected
        if (!string.IsNullOrEmpty(assetId))
            builder.WithCustomParameter("assetid", assetId);

        return builder.Build();
    }
}
