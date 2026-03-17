# Resume Point — Arkade Asset Support

## Where We Left Off

Plugin is **deployed and running** on the local BTCPay Server at https://btcpay.yvrbtclabs.dev with asset support code. The DB migration was applied manually. The plugin loads successfully.

**Status**: Core implementation complete (steps 1-10), needs end-to-end testing.

## What's Done

### Infrastructure
- BTCPay Server running at https://btcpay.yvrbtclabs.dev (IP: 170.75.172.6)
- Nginx reverse proxy with Let's Encrypt TLS (expires 2026-06-10)
- LND node active with 1 channel (pubkey: `032524ea...`)
- Admin account: eric@brodie.rocks

### Code Changes (all in `/home/ubuntu/btcpay-arkade/`)
All changes are **uncommitted local modifications** on the default branch.

**New files (9):**
- `BTCPayServer.Plugins.ArkPayServer/Data/Entities/VtxoAsset.cs` — DB entity
- `BTCPayServer.Plugins.ArkPayServer/Data/Migrations/20260312000000_AddVtxoAssets.cs` — Migration
- `BTCPayServer.Plugins.ArkPayServer/Services/AssetMetadataService.cs` — Cached GetAsset RPC
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadeAssetPaymentMethodHandler.cs` — ARKADE_ASSET handler
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadeAssetPaymentData.cs` — Payment data record
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadeAssetPromptDetails.cs` — Prompt details
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadeAssetCheckoutModelExtension.cs` — Checkout model
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadeAssetPaymentLinkExtension.cs` — BIP21 link
- `BTCPayServer.Plugins.ArkPayServer/Views/Shared/Arkade/ArkadeAssetMethodCheckout.cshtml` — Checkout UI

**Modified files (13):**
- `NArk/Protos/ark/v1/indexer.proto` — Added IndexerAsset, assets field, GetAsset RPC
- `NArk/Protos/ark/v1/types.proto` — Added VtxoAsset, assets field
- `BTCPayServer.Plugins.ArkPayServer/Data/Entities/VTXO.cs` — Assets nav property + GetHashCode
- `BTCPayServer.Plugins.ArkPayServer/Data/ArkPluginDbContext.cs` — VtxoAssets DbSet
- `BTCPayServer.Plugins.ArkPayServer/Data/Migrations/ArkPluginDbContextModelSnapshot.cs` — Snapshot update
- `BTCPayServer.Plugins.ArkPayServer/Services/ArkVtxoSynchronizationService.cs` — MapAssets(), eager load
- `BTCPayServer.Plugins.ArkPayServer/Services/ArkContractInvoiceListener.cs` — Asset payment detection
- `BTCPayServer.Plugins.ArkPayServer/PaymentHandler/ArkadePaymentMethodConfig.cs` — AcceptedAssets list
- `BTCPayServer.Plugins.ArkPayServer/Models/ArkadeListenedContract.cs` — Asset contract record
- `BTCPayServer.Plugins.ArkPayServer/Models/StoreOverviewViewModel.cs` — AcceptedAssets VM
- `BTCPayServer.Plugins.ArkPayServer/ArkPlugin.cs` — Registered all new services
- `BTCPayServer.Plugins.ArkPayServer/Controllers/ArkController.cs` — Add/Remove asset actions
- `BTCPayServer.Plugins.ArkPayServer/Views/Ark/StoreOverview.cshtml` — Accepted Assets UI card

### DB
- `VtxoAssets` table created manually in PostgreSQL (EF migration file exists but auto-run needs fixing)

## How to Rebuild & Deploy

```bash
# Build
export PATH="/home/ubuntu/.dotnet:$PATH"
cd /home/ubuntu/btcpay-arkade
dotnet build

# Copy updated DLLs into container
for f in BTCPayServer.Plugins.ArkPayServer/bin/Debug/net8.0/BTCPayServer.Plugins.ArkPayServer.* \
         BTCPayServer.Plugins.ArkPayServer/bin/Debug/net8.0/NArk*; do
  sudo docker cp "$f" generated_btcpayserver_1:/root/.btcpayserver/Plugins/BTCPayServer.Plugins.ArkPayServer/
done

# Restart
sudo docker restart generated_btcpayserver_1

# If plugin gets disabled after a crash:
sudo docker exec generated_btcpayserver_1 rm /root/.btcpayserver/Plugins/disabled
sudo docker restart generated_btcpayserver_1
```

## What Needs Testing

1. **Store Overview UI** — Go to Ark plugin page, check "Accepted Assets" card appears
2. **Add Asset** — Click "Add Asset", enter an asset ID, verify it saves
3. **Create Invoice** — Create an invoice; if ARKADE_ASSET payment method is enabled, it should show
4. **Payment Detection** — Send an asset payment to the invoice's Ark address, verify it detects
5. **Checkout UI** — Verify the asset checkout page renders (QR, Ark address, no Lightning)

## Known Issues / TODO

- EF migration auto-run needs investigation (table was created manually)
- `BeforeFetchingRates` in asset handler uses `.GetAwaiter().GetResult()` for async metadata fetch — should be refactored
- Multiple accepted assets: currently only first asset is used for invoice creation. Need either separate payment method IDs per asset or asset selection in invoice creation UI
- No asset balance display on store overview (VTXOs page doesn't show asset column yet)
- Asset payouts not implemented (only receiving)

## Rate Script Configuration

Any store accepting Arkade assets **must** have a rate script configured, otherwise the ARKADE_ASSET payment method will silently fail during invoice creation (BTCPay can't price the invoice without a rate).

Go to **Store Settings → Rates**, enable **Rate Scripting**, and add the appropriate rule.

### Stablecoin Assets (e.g. USDT pegged to USD)

The rate script tells BTCPay to use the Arkade plugin's rate provider instead of public exchanges:

```
FAKEUSDT_USD = arkadeassets(FAKEUSDT_USD);
```

Replace `FAKEUSDT` with your asset's ticker (from the Ark indexer metadata) and `USD` with the peg currency. The `arkadeassets` provider returns the peg rate you configured when adding the asset (e.g. 1:1 for a stablecoin).

**Store setup for stablecoins:**
1. Create store, set default currency to the peg fiat (e.g. `USD` or `JPY`)
2. Set up Ark wallet
3. Add the asset with **Stablecoin** pricing mode, peg currency = `USD`, peg rate = `1`
4. Add rate script: `{TICKER}_{FIAT} = arkadeassets({TICKER}_{FIAT});`
5. Price products in the fiat currency

### Store Coin Assets (e.g. BEPSI — token IS the currency)

For store coins, the store currency is the token itself. You need a rate script so the BTC/Lightning payment methods know the sat conversion:

```
BEPSI_BTC = 0.00002;
```

This means 1 BEPSI = 0.00002 BTC (2000 sats). The ARKADE_ASSET handler uses identity pricing (1 BEPSI invoice = 1 BEPSI token), so the rate script only affects the BTC/Lightning payment options.

**Store setup for store coins:**
1. Create store, set default currency to the token ticker (e.g. `BEPSI`) — this works even before the asset is configured, BTCPay doesn't validate currency names
2. Set up Ark wallet
3. Add the asset with **StoreCoin** pricing mode
4. Add rate script: `BEPSI_BTC = 0.00002;` (set your desired sat price)
5. Price products in the token (e.g. 1 BEPSI for a coke)

### Test Assets on This Server

| Asset | ID | Ticker | Decimals | Type | Rate Script |
|-------|-----|--------|----------|------|-------------|
| BEPSI | `33f3ae20792a5bcdc733c4b11a2b571b29c95ad5e98dc474d965d3ba0e95b6ab0000` | BEPSI | 0 | StoreCoin | `BEPSI_BTC = 0.00002;` |
| Eric USD Tether | `376d32abd7ac4ccc191957286dfe1916bc0c9d8c6fc056d8e35491bf6997ab080000` | FAKEUSDT | 2 | Stablecoin (USD) | `FAKEUSDT_USD = arkadeassets(FAKEUSDT_USD);` |

## Shared Wallet Fix (2026-03-17)

Fixed a bug where importing the same mnemonic into a second store would reset the wallet's `LastUsedIndex` to 0, causing address collisions and `duplicate key` crashes on `AddressInvoices`. The fix is in `NArk.Storage.EfCore/Storage/EfCoreWalletStorage.cs` — `UpsertWallet` and `SaveWallet` now use `Math.Max(existing.LastUsedIndex, wallet.LastUsedIndex)` to never go backwards.

## Key Reference

- Arkade Assets spec: https://github.com/arkade-os/arkade-assets
- Plugin source: https://github.com/ArkLabsHQ/btcpay-arkade
- Indexer API: `GET /v1/indexer/asset/{asset_id}` for metadata, `GetVtxos` returns assets per VTXO
- Ark server (mainnet): https://arkade.computer
