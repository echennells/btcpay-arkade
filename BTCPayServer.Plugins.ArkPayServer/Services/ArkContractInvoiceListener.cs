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
    private CancellationTokenSource _cts = new();
    private CompositeDisposable _leases = new();
    private int _inflightHandlers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await QueueMonitoredInvoices(_cts.Token);
        _leases.Add(eventAggregator.SubscribeAsync<InvoiceEvent>(OnInvoiceEvent));

        // Subscribe to NNark's storage events directly
        vtxoStorage.VtxosChanged += OnVtxoChanged;
        swapStorage.SwapsChanged += OnSwapChanged;

        logger.LogInformation("ArkContractInvoiceListener started");

        _ = PollAllInvoices(_cts.Token);
    }

    private async void OnSwapChanged(object? sender, NArk.Swaps.Models.ArkSwap swap)
    {
        Interlocked.Increment(ref _inflightHandlers);
        try
        {
            if (_cts.IsCancellationRequested)
                return;

            // Only process reverse submarine swaps (Lightning -> Ark)
            if (swap.SwapType != NArk.Swaps.Models.ArkSwapType.ReverseSubmarine)
                return;

            var activityState = swap.Status == NArk.Swaps.Models.ArkSwapStatus.Pending
                ? ContractActivityState.Active
                : ContractActivityState.Inactive;
            await contractStorage.UpdateContractActivityState(swap.WalletId, swap.ContractScript, activityState);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling swap change for {SwapId}", swap.SwapId);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightHandlers);
        }
    }

    private async Task OnInvoiceEvent(InvoiceEvent invoiceEvent)
    {
        memoryCache.Remove(GetCacheKey(invoiceEvent.Invoice.Id));
        _checkInvoices.Writer.TryWrite(invoiceEvent.Invoice.Id);
    }

    private async void OnVtxoChanged(object? sender, ArkVtxo vtxo)
    {
        Interlocked.Increment(ref _inflightHandlers);
        try
        {
            if (_cts.IsCancellationRequested)
                return;

            var terms = await clientTransport.GetServerInfoAsync();
            var network = terms.Network;
            var script = Script.FromHex(vtxo.Script);
            var hasAssets = vtxo.Assets is { Count: > 0 };

            // Try to find the invoice by address — handle both Ark and boarding contracts
            InvoiceEntity? inv = null;
            string? paymentDestination = null;

            // Check if this is a boarding contract (P2TR on-chain address)
            var contracts = await contractStorage.GetContracts(
                scripts: [vtxo.Script],
                contractTypes: [NArk.Core.Contracts.ArkBoardingContract.ContractType],
                cancellationToken: CancellationToken.None);

            var isBoarding = contracts.Count > 0;
            if (isBoarding)
            {
                // Boarding VTXO: look up invoice by P2TR Bitcoin address
                var btcAddress = script.GetDestinationAddress(network);
                if (btcAddress is not null)
                {
                    paymentDestination = btcAddress.ToString();
                    inv = await invoiceRepository.GetInvoiceFromAddress(
                        ArkadePlugin.ArkadePaymentMethodId, paymentDestination);
                }
            }
            else
            {
                // Standard Ark VTXO: look up invoice by Ark address
                var serverKey = terms.SignerKey.Extract().XOnlyPubKey;
                var address = ArkAddress.FromScriptPubKey(script, serverKey);
                paymentDestination = address.ToString(network.ChainName == ChainName.Mainnet);
                inv = await invoiceRepository.GetInvoiceFromAddress(
                    hasAssets ? ArkadePlugin.ArkadeAssetPaymentMethodId : ArkadePlugin.ArkadePaymentMethodId,
                    paymentDestination)
                    ?? await invoiceRepository.GetInvoiceFromAddress(
                        hasAssets ? ArkadePlugin.ArkadePaymentMethodId : ArkadePlugin.ArkadeAssetPaymentMethodId,
                        paymentDestination);
            }

            if (inv is null || inv.Status != InvoiceStatus.New)
                return;

            if (hasAssets && inv.GetPaymentPrompt(ArkadePlugin.ArkadeAssetPaymentMethodId) != null)
            {
                await HandleAssetPaymentData(vtxo, inv);
                return;
            }

            // Boarding payments: Processing until confirmed, then Settled
            var isConfirmed = !isBoarding || vtxo.Metadata?.GetValueOrDefault("Confirmed") == "True";

            // Map NNark's ArkVtxo to plugin's VtxoEntity entity
            var vtxoEntity = new VtxoEntity
            {
                TransactionId = vtxo.TransactionId,
                TransactionOutputIndex = (int)vtxo.TransactionOutputIndex,
                Amount = (long)vtxo.Amount,
                Script = vtxo.Script,
                SeenAt = vtxo.CreatedAt
            };
            await HandlePaymentData(vtxoEntity, inv, arkadePaymentMethodHandler, paymentDestination, isConfirmed);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling VTXO change for {TxId}:{Index}", vtxo.TransactionId, vtxo.TransactionOutputIndex);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightHandlers);
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
        var isMismatch = matchingAsset is null;

        if (isMismatch)
        {
            // Wrong asset sent — record it as Unaccounted so it shows on the invoice
            // and in the Exceptions report, but doesn't satisfy the invoice.
            matchingAsset = vtxo.Assets?.FirstOrDefault();
            if (matchingAsset is null)
                return;

            logger.LogWarning("Mismatched asset in VTXO {TxId}:{Index}. Received {ReceivedAssetId}, expected one of: {ExpectedAssetIds}",
                vtxo.TransactionId, vtxo.TransactionOutputIndex, matchingAsset.AssetId, string.Join(", ", acceptedAssetIds));
        }

        var outpoint = $"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}";

        // Get asset metadata for proper decimal conversion
        var metadata = await assetMetadataService.GetAssetMetadata(matchingAsset.AssetId);
        var decimals = metadata?.Decimals ?? 0;
        var divisor = 1m;
        for (var i = 0; i < decimals; i++) divisor *= 10m;
        var displayAmount = (decimal)matchingAsset.Amount / divisor;

        var details = isMismatch
            ? new ArkadeAssetPaymentData(outpoint, matchingAsset.AssetId, checked((long)matchingAsset.Amount),
                IsMismatchedAsset: true,
                ReceivedTicker: metadata?.Ticker,
                ExpectedAssetIds: string.Join(", ", acceptedAssetIds))
            : new ArkadeAssetPaymentData(outpoint, matchingAsset.AssetId, checked((long)matchingAsset.Amount));

        await _paymentLock.WaitAsync(_cts.Token);
        try
        {
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null) return;

            var pmi = ArkadePlugin.ArkadeAssetPaymentMethodId;

            // For mismatched assets, use the prompt's currency so UpdateTotals doesn't
            // throw on a missing rate. The actual received asset info is in the details.
            var paymentCurrency = isMismatch
                ? (prompt.Currency ?? "ASSET")
                : (metadata?.Ticker ?? "ASSET");

            var paymentData = new PaymentData
            {
                Status = isMismatch ? PaymentStatus.Unaccounted : PaymentStatus.Settled,
                Amount = displayAmount,
                Created = vtxo.CreatedAt,
                Id = outpoint,
                Currency = paymentCurrency,
            }.Set(freshInvoice, arkadeAssetPaymentMethodHandler, details);

            var existing = freshInvoice
                .GetPayments(false)
                .SingleOrDefault(c => c.Id == paymentData.Id && c.PaymentMethodId == pmi);

            if (existing == null)
            {
                var payment = await paymentService.AddPayment(paymentData);
                if (payment != null && !isMismatch)
                    await ReceivedPayment(freshInvoice, payment);
            }
            else if (!isMismatch)
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

    private async Task HandlePaymentData(VtxoEntity vtxo, InvoiceEntity invoice, ArkadePaymentMethodHandler handler, string? destination = null, bool isConfirmed = true)
    {
        var pmi = ArkadePlugin.ArkadePaymentMethodId;
        var details = new ArkadePaymentData($"{vtxo.TransactionId}:{vtxo.TransactionOutputIndex}", destination);
        var status = isConfirmed ? PaymentStatus.Settled : PaymentStatus.Processing;

        // Serialize payment registration to prevent duplicate inserts from concurrent VTXO events
        await _paymentLock.WaitAsync(_cts.Token);
        try
        {
            // Re-fetch the invoice inside the lock to get the latest payment state
            var freshInvoice = await invoiceRepository.GetInvoice(invoice.Id);
            if (freshInvoice is null)
                return;

            var paymentData = new PaymentData
            {
                Status = status,
                Amount = Money.Satoshis(vtxo.Amount).ToDecimal(MoneyUnit.BTC),
                Created = vtxo.SeenAt,
                Id = details.Outpoint,
                Currency = "BTC",
            }.Set(freshInvoice, handler, details);

            // Override destination if payment came via boarding address (not the Ark contract address)
            if (destination is not null)
            {
                var blob = JObject.Parse(paymentData.Blob2);
                blob["Destination"] = destination;
                paymentData.Blob2 = blob.ToString(Newtonsoft.Json.Formatting.None);
            }

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
                // Update existing payment — upgrade Processing→Settled on confirmation
                alreadyExistingPaymentThatMatches.Status = status;
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
        // 1. Unsubscribe first to prevent new events from firing
        vtxoStorage.VtxosChanged -= OnVtxoChanged;
        swapStorage.SwapsChanged -= OnSwapChanged;

        // 2. Cancel to signal any in-flight handlers to exit
        await _cts.CancelAsync();

        // 3. Wait for in-flight async void handlers to drain before disposing resources
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref _inflightHandlers) > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);

        if (Volatile.Read(ref _inflightHandlers) > 0)
            logger.LogWarning("Timed out waiting for {Count} in-flight event handlers to complete", _inflightHandlers);

        // 4. Safe to dispose now — all handlers have exited
        _leases.Dispose();
        _leases = new CompositeDisposable();
        _paymentLock.Dispose();
        _cts.Dispose();
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
        var remaining = invoice.ExpirationTime - DateTimeOffset.UtcNow;
        return DateTimeOffset.UtcNow + (remaining >= TimeSpan.FromMinutes(5.0) ? remaining : TimeSpan.FromMinutes(5.0));
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
        while (!cancellation.IsCancellationRequested)
        {
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
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unhandled error in the Arkade invoice listener.");
                await Task.Delay(1000, cancellation);
            }
        }

        logger.LogInformation("Exiting poll loop.");
    }
}