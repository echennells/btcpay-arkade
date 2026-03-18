using System.Threading.Channels;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Plugins.ArkPayServer.Models;
using BTCPayServer.Plugins.ArkPayServer.PaymentHandler;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NArk.Abstractions.VTXOs;
using NArk.Swaps.Abstractions;
using NArk.Core.Transport;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using NArk.Abstractions;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Swaps.Services;
using NArk.Storage.EfCore.Entities;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public class ArkContractInvoiceListener(
    IMemoryCache memoryCache,
    InvoiceRepository invoiceRepository,
    ArkadePaymentMethodHandler arkadePaymentMethodHandler,
    ArkadeAssetPaymentMethodHandler arkadeAssetPaymentMethodHandler,
    AssetMetadataService assetMetadataService,
    IClientTransport clientTransport,
    EventAggregator eventAggregator,
    IContractStorage contractStorage,
    PaymentService paymentService,
    IVtxoStorage vtxoStorage,
    ISwapStorage swapStorage,
    ILogger<ArkContractInvoiceListener> logger)
    : IHostedService
{
    private readonly Channel<string> _checkInvoices = Channel.CreateUnbounded<string>();
    private readonly SemaphoreSlim _paymentLock = new(1, 1);
    private CompositeDisposable _leases = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await QueueMonitoredInvoices(cancellationToken);
        _leases.Add(eventAggregator.SubscribeAsync<InvoiceEvent>(OnInvoiceEvent));

        // Subscribe to NNark's storage events directly
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        swapStorage.SwapsChanged += OnSwapChanged;

        logger.LogInformation("ArkContractInvoiceListener started");

        _ = PollAllInvoices(cancellationToken);
    }

    private async void OnSwapChanged(object? sender, NArk.Swaps.Models.ArkSwap swap)
    {
        try
        {
            // Only process reverse submarine swaps (Lightning -> Ark)
            if (swap.SwapType != NArk.Swaps.Models.ArkSwapType.ReverseSubmarine)
                return;

            var activityState = swap.Status == NArk.Swaps.Models.ArkSwapStatus.Pending
                ? ContractActivityState.Active
                : ContractActivityState.Inactive;
            await contractStorage.UpdateContractActivityState(swap.WalletId, swap.ContractScript, activityState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling swap change for {SwapId}", swap.SwapId);
        }
    }

    private async Task OnInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        memoryCache.Remove(GetCacheKey(invoiceEvent.Invoice.Id));
        _checkInvoices.Writer.TryWrite(invoiceEvent.Invoice.Id);
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        try
        {
            var terms = await clientTransport.GetServerInfoAsync();
            var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
            var script = Script.FromHex(vtxo.Script);
            var address = ArkAddress.FromScriptPubKey(script, serverKey);
            var network = terms.Network;
            var addressStr = address.ToString(network.ChainName == ChainName.Mainnet);

            var hasAssets = vtxo.Assets is { Count: > 0 };

            if (hasAssets)
            {
                var assetInv = await invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadeAssetPaymentMethodId, addressStr)
                               ?? await invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadePaymentMethodId, addressStr);

                if (assetInv?.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId) != null)
                {
                    await HandleAssetPaymentData(vtxo, assetInv);
                }
            }
            else
            {
                var inv = await invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadePaymentMethodId, addressStr)
                          ?? await invoiceRepository.GetInvoiceFromAddress(ArkadePlugin.ArkadeAssetPaymentMethodId, addressStr);

                if (inv?.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId) != null)
                {
                    var vtxoEntity = new VtxoEntity
                    {
                        TransactionId = vtxo.TransactionId,
                        TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
                        Amount = (long)vtxo.Amount,
                        Script = vtxo.Script,
                        SeenAt = vtxo.CreatedAt
                    };
                    await HandlePaymentData(vtxoEntity, inv, arkadePaymentMethodHandler);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling VTXO change for {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
    }

    private Task ReceivedPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
        logger.LogInformation("Invoice {invoiceId} received payment {amount} {currency} {paymentId}",
            invoice.Id, payment.Value, payment.Currency, payment.Id);

        eventAggregator.Publish(
            new InvoiceEvent(invoice, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return Task.CompletedTask;
    }
    
    private async Task HandleAssetPaymentData(ArkVtxo vtxo, InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId);
        if (prompt is null)
        {
            logger.LogWarning("No ARKADE_ASSET prompt on invoice {InvoiceId}", invoice.Id);
            return;
        }

        var promptDetails = arkadeAssetPaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details);

        // Build the set of accepted asset IDs from AssetOptions (multi-asset) or fall back to single AssetId
        var acceptedAssetIds = promptDetails.AssetOptions is { Count: > 0 }
            ? promptDetails.AssetOptions.Select(o => o.AssetId).ToHashSet()
            : new HashSet<string> { promptDetails.AssetId };

        var matchingAsset = vtxo.Assets?.FirstOrDefault(a => acceptedAssetIds.Contains(a.AssetId));
        if (matchingAsset is null)
        {
            logger.LogWarning("No matching asset in VTXO {TxId}:{Index}. Expected one of: {ExpectedAssetIds}",
                vtxo.TransactionId, vtxo.TransactionOutputIndex, string.Join(", ", acceptedAssetIds));
            return;
        }

        var outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";
        var details = new ArkadeAssetPaymentData(outpoint, matchingAsset.AssetId, (long)matchingAsset.Amount);

        // Get asset metadata for proper decimal conversion
        var metadata = await assetMetadataService.GetAssetMetadata(matchingAsset.AssetId);
        var decimals = metadata?.Decimals ?? 0;
        var divisor = 1m;
        for (var i = 0; i < decimals; i++) divisor *= 10m;
        var displayAmount = (decimal)matchingAsset.Amount / divisor;

        await _paymentLock.WaitAsync();
        try
        {
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null) return;

            var pmi = ArkadePlugin.ArkadeAssetPaymentMethodId;
            var paymentData = new PaymentData
            {
                Status = PaymentStatus.Settled,
                Amount = displayAmount,
                Created = vtxo.CreatedAt,
                Id = outpoint,
                Currency = metadata?.Ticker ?? "ASSET",
            }.Set(freshInvoice, arkadeAssetPaymentMethodHandler, details);

            var existing = freshInvoice
                .GetPayments(false)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (existing == null)
            {
                var payment = await paymentService.AddPayment(paymentData);
                if (payment != null)
                    await ReceivedPayment(freshInvoice, payment);
            }
            else
            {
                existing.Status = PaymentStatus.Settled;
                existing.Details = JToken.FromObject(details, arkadeAssetPaymentMethodHandler.Serializer);
                await paymentService.UpdatePayments([existing]);
            }
        }
        finally
        {
            _paymentLock.Release();
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }

    private async Task HandlePaymentData(VtxoEntity vtxo, InvoiceEntity invoice, ArkadePaymentMethodHandler handler)
    {
        var pmi = ArkadePlugin.ArkadePaymentMethodId;
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}");

        // Serialize payment registration to prevent duplicate inserts from concurrent VTXO events
        await _paymentLock.WaitAsync();
        try
        {
            // Re-fetch the invoice inside the lock to get the latest payment state
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null)
                return;

            var paymentData = new PaymentData
            {
                Status = PaymentStatus.Settled,
                Amount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
                Created = vtxo.SeenAt,
                Id = details.Outpoint,
                Currency = "BTC",
            }.Set(freshInvoice, handler, details);

            var alreadyExistingPaymentThatMatches = freshInvoice
                .GetPayments(false)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (alreadyExistingPaymentThatMatches == null)
            {
                var payment = await paymentService.AddPayment(paymentData);
                if (payment != null)
                {
                    await ReceivedPayment(freshInvoice, payment);
                }
            }
            else
            {
                //else update it with the new data
                alreadyExistingPaymentThatMatches.Status = PaymentStatus.Settled;
                alreadyExistingPaymentThatMatches.Details = JToken.FromObject(details, handler.Serializer);
                await paymentService.UpdatePayments([alreadyExistingPaymentThatMatches]);
            }
        }
        finally
        {
            _paymentLock.Release();
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
    }
    
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        swapStorage.SwapsChanged -= OnSwapChanged;
        _leases.Dispose();
        _leases = new CompositeDisposable();
    }

    public async Task ToggleArkadeContract(InvoiceEntity invoice)
    {
        var activityState = invoice.Status == InvoiceStatus.New
            ? ContractActivityState.Active
            : ContractActivityState.Inactive;
        var listenedContract = GetListenedArkadeInvoice(invoice);
        if (listenedContract is null)
            return;

        var serverInfo = await clientTransport.GetServerInfoAsync();
        var contract = listenedContract.Details.GetContract(serverInfo.Network);
        if (contract is null)
        {
            logger.LogWarning("Contract is null for invoice {InvoiceId}", invoice.Id);
            return;
        }

        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        await contractStorage.UpdateContractActivityState(listenedContract.Details.WalletId, script, activityState);
    }

    private ArkadeListenedContract? GetListenedArkadeInvoice(InvoiceEntity invoice)
    {
        // Try plain ARKADE prompt first
        var prompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadePaymentMethodId);
        if (prompt is not null)
        {
            return new ArkadeListenedContract(
                arkadePaymentMethodHandler.ParsePaymentPromptDetails(prompt.Details),
                invoice.Id);
        }

        // Fall back to ARKADE_ASSET prompt (asset-only invoices share the same contract structure)
        var assetPrompt = invoice.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId);
        if (assetPrompt is not null)
        {
            var assetDetails = arkadeAssetPaymentMethodHandler.ParsePaymentPromptDetails(assetPrompt.Details);
            // Convert to ArkadePromptDetails since the contract/wallet fields are identical
            var details = new ArkadePromptDetails(assetDetails.WalletId, assetDetails.ContractString);
            return new ArkadeListenedContract(details, invoice.Id);
        }

        return null;
    }

    private static DateTimeOffset GetExpiration(InvoiceEntity invoice)
    {
        var expiredIn = DateTimeOffset.UtcNow - invoice.ExpirationTime;
        return DateTimeOffset.UtcNow + (expiredIn >= TimeSpan.FromMinutes(5.0) ? expiredIn : TimeSpan.FromMinutes(5.0));
    }

    private string GetCacheKey(string invoiceId)
    {
        return $"{nameof(GetListenedArkadeInvoice)}-{invoiceId}";
    }

    private Task<InvoiceEntity> GetInvoice(string invoiceId)
    {
        return memoryCache.GetOrCreateAsync(GetCacheKey(invoiceId), async cacheEntry =>
        {
            var invoice = await invoiceRepository.GetInvoice(invoiceId);
            if (invoice is null)
                return null;
            cacheEntry.AbsoluteExpiration = GetExpiration(invoice);
            return invoice;
        })!;
    }


    private async Task QueueMonitoredInvoices(CancellationToken cancellation)
    {
        var queued = new HashSet<string>();

        foreach (var invoice in await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadePaymentMethodId,
                     cancellation))
        {
            if (GetListenedArkadeInvoice(invoice) is null) continue;
            _checkInvoices.Writer.TryWrite(invoice.Id);
            memoryCache.Set(GetCacheKey(invoice.Id), invoice, GetExpiration(invoice));
            queued.Add(invoice.Id);
        }

        // Also queue ARKADE_ASSET invoices (asset-only invoices won't have ARKADE prompt)
        foreach (var invoice in await invoiceRepository.GetMonitoredInvoices(ArkadePlugin.ArkadeAssetPaymentMethodId,
                     cancellation))
        {
            if (queued.Contains(invoice.Id)) continue;
            if (GetListenedArkadeInvoice(invoice) is null) continue;
            _checkInvoices.Writer.TryWrite(invoice.Id);
            memoryCache.Set(GetCacheKey(invoice.Id), invoice, GetExpiration(invoice));
            queued.Add(invoice.Id);
        }

        logger.LogDebug("Queued {Count} monitored invoices", queued.Count);
    }

    private async Task PollAllInvoices(CancellationToken cancellation)
    {
        retry:
        if (cancellation.IsCancellationRequested)
            return;
        try
        {
            await foreach (var invoiceId in _checkInvoices.Reader.ReadAllAsync(cancellation))
            {
                logger.LogInformation("Checking for invoice {InvoiceId}", invoiceId);
                var invoice = await GetInvoice(invoiceId);
                await ToggleArkadeContract(invoice);
            }
        }
        catch when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Task.Delay(1000, cancellation);
            logger.LogWarning(ex, "Unhandled error in the Arkade invoice listener.");
            goto retry;
        }
        
        logger.LogInformation("Exiting poll loop.");
    }
}