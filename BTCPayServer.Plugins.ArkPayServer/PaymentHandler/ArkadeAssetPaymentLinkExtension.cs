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

        // Build a simple BIP21-style URI with just the ark address
        return ArkadeBip21Builder.Create()
            .WithArkAddress(prompt.Destination)
            .WithAmount(amount)
            .Build();
    }
}
