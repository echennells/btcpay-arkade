# Store Coin Refund Prevention

**Date:** 2026-04-11
**Branch:** `asset-support`
**Status:** Implementation complete, awaiting deploy & test

## Problem

BTCPay Server's refund pipeline requires a BTC exchange rate for every payout it
creates — see `submodules/btcpayserver/BTCPayServer/HostedServices/PullPaymentHostedService.cs:711`
where auto-approval calls `GetRate(payout, ...)` and aborts if no `BTC↔OriginalCurrency`
rate exists.

Store coins (`AssetPricingMode.StoreCoin` in
`BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadePaymentMethodConfig.cs:6-19`)
are merchant-defined units (e.g. `BEPSI`) that intentionally have no BTC peg. The
plugin's rate provider deliberately withholds `BTC↔TICKER` rates for store coins
(see comment at `BTCPayServer.Plugins.ArkPayServer/Services/ArkadeAssetRateProvider.cs:67-72`)
to prevent BTCPay from miscomputing invoice prices.

The result: store-coin refunds **cannot** flow through BTCPay's payout pipeline.
Currently the merchant clicks "Refund" on a store-coin invoice, fills the wizard,
sees "Refund successfully created!", and the payout silently fails downstream.

## Why simpler fixes don't work

We exhaustively explored options before settling on this plan. Findings:

| Approach | Why it doesn't work |
|---|---|
| Don't expose store coins as `prompt.Currency` | Invoice currency is set by merchant at invoice creation; plugin can't decouple `prompt.Currency` from `invoice.Currency` without breaking store-coin invoice pricing |
| Filter via `IPayoutHandler.IsSupported(StoreData)` | Per-store, not per-invoice. A store with mixed stablecoin + store-coin assets can't selectively block |
| Have rate provider refuse `BTC↔TICKER` for store coins | **Already does this** — that's the root cause. Can't withdraw `TICKER↔TICKER` identity rate either, since invoice pricing depends on it |
| Override `Invoice.cshtml` from a plugin | Plugin views can only override views in plugin-area controllers. Core's `UIInvoice` controller is not in an Area. Mechanism doesn't exist |
| Register a competing route for `/invoices/{id}/refund` | First-match-wins; registration order is undocumented and unreliable |
| Add a refund-validation extension point in core | Cleanest fix but requires patching `submodules/btcpayserver` and either carrying a fork patch or upstreaming a PR. Out of scope for this iteration |

## Chosen approach: CSS/JS shim via existing extension point + server-side backstop + notification

**Three pieces:**

1. **JS shim** — A new partial registered at the existing `store-invoices-payments`
   UI extension point detects store-coin invoices server-side, then injects an
   inline `<script>` that hides the core refund button (`#IssueRefund` in
   `submodules/btcpayserver/BTCPayServer/Views/UIInvoice/Invoice.cshtml:192`) and
   replaces it with a disabled placeholder showing an explanatory tooltip.
2. **Server-side backstop** — The existing `TrackClaim` cancel in
   `BTCPayServer.Plugins.ArkPayServer/Payouts/Ark/ArkPayoutHandler.cs:91-112`
   catches direct-URL access (bookmarks, API clients) that bypass the shim.
3. **Notification** — A new plugin notification type publishes a user-visible
   notification when `TrackClaim` cancels a payout, so the failure is at least
   surfaced (rather than silent).

### Why this combination

- The shim handles the 99% case (merchant clicks button in UI) cleanly: button
  is replaced before they can interact with it.
- The backstop handles direct-URL access — necessary because the shim only acts
  on the visible UI.
- The notification surfaces the backstop failure path so it isn't silent.

### Verified facts (do not re-research)

- `ListInvoicesPaymentsPartial.cshtml:94` contains
  `<vc:ui-extension-point location="store-invoices-payments" model="@invoice" />`.
- This partial is included from **both** `Invoice.cshtml:461` (invoice details
  page, single invoice) and `ListInvoices.cshtml:416` (store invoice list, looped
  per invoice). The shim must be a no-op when `#IssueRefund` is absent (list page
  case).
- Plugin partials at `store-invoices-payments` receive `InvoiceDetailsModel`
  (verified — see existing `BTCPayServer.Plugins.ArkPayServer/Views/Ark/ArkPaymentData.cshtml:9`).
- `StoreRepository` is injectable in Razor partials via `@inject StoreRepository`
  (precedent: `submodules/btcpayserver/BTCPayServer/Views/UIStores/StoreUsers.cshtml:5`).
  `FindStore(string storeId)` returns `Task<StoreData?>` —
  `submodules/btcpayserver/BTCPayServer/Services/Stores/StoreRepository.cs:47`.
- `NotificationSender.SendNotification(INotificationScope, BaseNotification)`
  is the publishing API (`submodules/btcpayserver/BTCPayServer/Services/Notifications/NotificationSender.cs:20`).
- `_notificationSender` is **already** constructor-injected into `ArkPayoutHandler`
  (line 43, 55) and used elsewhere at line 241-247 with `ExternalPayoutTransactionNotification`.
  No DI plumbing needed for piece 3.
- `NotificationHandler<T>` (the abstract base for plugin notification handlers)
  lives at `submodules/btcpayserver/BTCPayServer/Services/Notifications/INotificationHandler.cs:7`
  in the main BTCPayServer assembly, which the plugin already references.
- Notification handler registration is `services.AddSingleton<INotificationHandler, FooNotification.Handler>()`
  (precedent: `submodules/btcpayserver/BTCPayServer/Hosting/BTCPayServerServices.cs:476-481`).
- `PayoutNotification.cs` is the cookbook to clone for the new notification type
  (`submodules/btcpayserver/BTCPayServer/Services/Notifications/Blobs/PayoutNotification.cs`).

### Refund button DOM context

Per `submodules/btcpayserver/BTCPayServer/Views/UIInvoice/Invoice.cshtml:171-211`:
- Refund button is `id="IssueRefund"` inside a `.sticky-header` div
- Use selector `.sticky-header #IssueRefund` to be future-proof against any other
  page accidentally adding an element with the same ID

## Implementation

### Piece 1: New partial `Views/Ark/ArkRefundButtonHider.cshtml`

Create `BTCPayServer.Plugins.ArkPayServer/Views/Ark/ArkRefundButtonHider.cshtml`:

```razor
@using BTCPayServer.Abstractions.Extensions
@using BTCPayServer.Payments
@using BTCPayServer.Plugins.ArkPayServer
@using BTCPayServer.Plugins.ArkPayServer.PaymentHandler
@using BTCPayServer.Services.Stores
@model BTCPayServer.Models.InvoicingModels.InvoiceDetailsModel
@inject StoreRepository storeRepository
@inject PaymentMethodHandlerDictionary handlers

@{
    var isStoreCoinInvoice = false;
    var invoiceCurrency = Model.Entity.Currency;

    var store = await storeRepository.FindStore(Model.StoreId);
    if (store is not null)
    {
        var arkConfig = store.GetPaymentMethodConfig<ArkadePaymentMethodConfig>(
            ArkadePlugin.ArkadePaymentMethodId, handlers, true);

        if (arkConfig?.AcceptedAssets is { Count: > 0 })
        {
            isStoreCoinInvoice = arkConfig.AcceptedAssets.Any(a =>
                a.PricingMode == AssetPricingMode.StoreCoin &&
                !string.IsNullOrEmpty(a.Ticker) &&
                string.Equals(a.Ticker, invoiceCurrency, StringComparison.OrdinalIgnoreCase));
        }
    }
}

@if (isStoreCoinInvoice)
{
    <script>
        (function () {
            if (document.getElementById('arkade-refund-blocked')) return;
            var btn = document.querySelector('.sticky-header #IssueRefund');
            if (!btn) return; // not on details page (e.g. invoice list rows)
            var replacement = document.createElement('button');
            replacement.id = 'arkade-refund-blocked';
            replacement.className = 'btn btn-secondary text-nowrap';
            replacement.disabled = true;
            replacement.title = 'Store coin invoices cannot be refunded through BTCPay because no BTC exchange rate exists. Issue refunds manually from the Ark wallet.';
            replacement.textContent = 'Refund unavailable';
            btn.parentNode.insertBefore(replacement, btn);
            btn.style.display = 'none';
        })();
    </script>
}
```

**Notes:**
- Idempotent — guarded by `arkade-refund-blocked` ID check
- No-op when `#IssueRefund` absent (list page rows pose no problem)
- Native `title` tooltip — no Bootstrap tooltip init dance
- Selector `.sticky-header #IssueRefund` is future-proof against ID collisions

### Piece 2: Register the partial

In `BTCPayServer.Plugins.ArkPayServer/ArkPlugin.cs`, add inside
`RegisterUIExtensions(IServiceCollection services)` (around line 226):

```csharp
services.AddUIExtension("store-invoices-payments", "/Views/Ark/ArkRefundButtonHider.cshtml");
```

### Piece 3: New notification type

Create `BTCPayServer.Plugins.ArkPayServer/Notifications/StoreCoinRefundCancelledNotification.cs`:

Clone the structure from `submodules/btcpayserver/BTCPayServer/Services/Notifications/Blobs/PayoutNotification.cs`.
The new class:
- `BaseNotification` subclass with `PayoutId`, `StoreId`, `InvoiceId`, `Currency`
- Inner `Handler : NotificationHandler<StoreCoinRefundCancelledNotification>`
- `FillViewModel.Body` returns the explanatory message
- `ActionLink` points to the cancelled payout's invoice details page

### Piece 4: Register the notification handler

In `ArkPlugin.cs` service registration:

```csharp
services.AddSingleton<INotificationHandler, StoreCoinRefundCancelledNotification.Handler>();
```

### Piece 5: Publish notification from `TrackClaim`

In `BTCPayServer.Plugins.ArkPayServer/Payouts/Ark/ArkPayoutHandler.cs`, modify
the existing `TrackClaim` (lines 91-112) so that immediately after setting
`payoutData.State = PayoutState.Cancelled`, it publishes the new notification
via the already-injected `_notificationSender`:

```csharp
await _notificationSender.SendNotification(
    new StoreScope(payoutData.StoreDataId),
    new StoreCoinRefundCancelledNotification
    {
        PayoutId = payoutData.Id,
        StoreId = payoutData.StoreDataId,
        InvoiceId = payoutData.PullPaymentDataId, // or wherever the invoice ID lives
        Currency = payoutData.OriginalCurrency
    });
```

(Verify `payoutData.PullPaymentDataId` is the right property for the invoice
reference at implementation time — may need to plumb through differently.)

### Piece 6: Build and deploy

1. `cd /home/ubuntu/btcpay-arkade && dotnet build BTCPayServer.Plugins.ArkPayServer/BTCPayServer.Plugins.ArkPayServer.csproj`
2. Copy built DLLs into the running container per the existing deploy workflow
3. Restart the container; if plugin disabled after crash, remove
   `/root/.btcpayserver/Plugins/disabled` inside the container

## Cleanup decisions

### Already cleaned up in working tree (keep these changes)
- ✅ `AssetPayoutApprovalHook.cs` deleted — wrong strategy, replaced by `TrackClaim` cancel
- ✅ `ArkPlugin.cs` registration of that hook removed
- ✅ `TrackClaim` cancel logic added — this is the **backstop**, keep it

### NOT cleaning up (intentionally left alone)
- ❓ `ArkPayoutHandler.TryGenerateBip21` asset URI generation
- ❓ `ArkController.cs` BIP21 asset query param parsing (line 3994)
- ❓ `Send2DestinationViewModel.AssetTicker/AssetAmount` fields
- ❓ `ArkController.cs` asset send routing (lines 1457, 1753)

**Reasoning:** These were originally added to support the "merchant manually pays
asset refunds via the send wizard" path. For store coins this path is now blocked
entirely (this plan). For **stablecoin assets** (e.g. USDT), refunds DO have a
valid BTC rate and could in theory go through normal payout. But a merchant might
prefer to refund a 50 USDT invoice as 50 USDT (not as the BTC equivalent), and
the BIP21 asset routing supports that. **Keep until we explicitly decide
stablecoin asset refunds aren't a desired feature.**

### Submodule bumps in working tree (orthogonal — keep)
- `submodules/NNark` → `3dc4764` (payment repository feature)
- `submodules/btcpayserver` → `a13603a8` (v2.3.5 fix)

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Future BTCPay UI rename of `#IssueRefund` ID silently breaks shim | Add an integration test that loads a store-coin invoice page and asserts the replacement is present. Defer test until we decide on test infrastructure |
| Merchant inspects DOM and unhides via devtools | Backstop in `TrackClaim` catches it; notification surfaces the failure |
| Direct API refund creation | Same — backstop + notification |
| `StoreRepository.FindStore` adds DB call per invoice details render | Negligible. Could cache via `HttpContext.Items` if it ever matters. Defer |
| `TrackClaim` runs inside the payout creation transaction — long DB call could block | The notification publish is a separate `await` that opens its own context. Watch for transaction-related issues during testing |

## Out of scope (deferred)

- Adding a real `IRefundValidator` extension point upstream to BTCPay core
- Removing BIP21 asset routing if stablecoin refunds are decided unwanted
- Integration test for the JS shim
- Localization of the notification body and tooltip strings

## File-by-file change list

**New files:**
- `BTCPayServer.Plugins.ArkPayServer/Views/Ark/ArkRefundButtonHider.cshtml`
- `BTCPayServer.Plugins.ArkPayServer/Notifications/StoreCoinRefundCancelledNotification.cs`

**Modified files:**
- `BTCPayServer.Plugins.ArkPayServer/ArkPlugin.cs` — register partial, register notification handler
- `BTCPayServer.Plugins.ArkPayServer/Payouts/Ark/ArkPayoutHandler.cs` — publish notification on cancel

**Files in working tree, kept as-is:**
- `BTCPayServer.Plugins.ArkPayServer/Models/Send2ViewModel.cs` (asset send wizard fields)
- `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs` (asset BIP21 parsing)

## Resume checklist (if interrupted)

If you're picking this up cold, run through this checklist:

1. `cd /home/ubuntu/btcpay-arkade && git status` — confirm working tree shape matches the plan
2. Check whether `Views/Ark/ArkRefundButtonHider.cshtml` exists yet
3. Check whether `Notifications/StoreCoinRefundCancelledNotification.cs` exists yet
4. Check whether `ArkPlugin.cs` has the two new `AddUIExtension`/`AddSingleton` lines
5. Check whether `ArkPayoutHandler.TrackClaim` has the notification publish call
6. Run `dotnet build` and check for errors
7. If all six pieces present and building, deploy and test the UX
