namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record AcceptedAsset(string AssetId, string? DisplayName = null);

public record ArkadePaymentMethodConfig(
    string WalletId,
    bool GeneratedByStore = false,
    bool AllowSubDustAmounts = false,
    List<AcceptedAsset>? AcceptedAssets = null);