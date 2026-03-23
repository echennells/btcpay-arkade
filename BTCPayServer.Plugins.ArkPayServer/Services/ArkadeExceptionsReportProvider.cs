using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Reporting;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkadeExceptionsReportProvider(
    InvoiceRepository invoiceRepository,
    ArkadeAssetPaymentMethodHandler assetPaymentMethodHandler,
    DisplayFormatter displayFormatter)
    : ReportProvider
{
    public override string Name => "Arkade Exceptions";

    private static ViewDefinition CreateViewDefinition()
    {
        return new ViewDefinition
        {
            Fields =
            [
                new("Date", "datetime"),
                new("InvoiceId", "invoice_id"),
                new("OrderId", "string"),
                new("ReceivedAsset", "string"),
                new("ReceivedAmount", "amount"),
                new("ExpectedAssets", "string"),
                new("Address", "string")
            ],
            Charts =
            {
                new()
                {
                    Name = "By received asset",
                    Groups = { "ReceivedAsset" },
                    HasGrandTotal = false,
                    Aggregates = { "ReceivedAmount" }
                }
            }
        };
    }

    public override async Task Query(QueryContext queryContext, CancellationToken cancellation)
    {
        queryContext.ViewDefinition = CreateViewDefinition();

        var invoices = await invoiceRepository.GetInvoices(new InvoiceQuery
        {
            StoreId = [queryContext.StoreId],
            StartDate = queryContext.From,
            EndDate = queryContext.To,
            OrderByDesc = false,
        }, cancellation);

        foreach (var invoice in invoices)
        {
            foreach (var payment in invoice.GetPayments(false))
            {
                if (payment.PaymentMethodId != ArkadePlugin.ArkadeAssetPaymentMethodId)
                    continue;
                if (payment.Status != PaymentStatus.Unaccounted)
                    continue;

                var details = assetPaymentMethodHandler.ParsePaymentDetails(payment.Details);
                if (!details.IsMismatchedAsset)
                    continue;

                var data = queryContext.AddData();
                data.Add(payment.ReceivedTime);
                data.Add(invoice.Id);
                data.Add(invoice.Metadata.OrderId);
                data.Add(details.ReceivedTicker ?? details.AssetId);
                data.Add(displayFormatter.ToFormattedAmount(payment.Value, payment.Currency));
                data.Add(details.ExpectedAssetIds);
                data.Add(payment.Destination);
            }
        }
    }
}
