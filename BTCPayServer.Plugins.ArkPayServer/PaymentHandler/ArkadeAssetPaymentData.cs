namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

public record ArkadeAssetPaymentData(
    string Outpoint,
    string AssetId,
    long AssetAmount,
    bool IsMismatchedAsset = false,
    string? ReceivedTicker = null,
    string? ExpectedAssetIds = null);
