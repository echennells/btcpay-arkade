namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkPaymentDataViewModel
{
    public string Outpoint { get; set; }
    public string Amount { get; set; }
    public string Contract { get; set; }
    public string Address { get; set; }
    public DateTimeOffset ReceivedTime { get; set; }
    public string Currency { get; set; }
    public bool IsMismatchedAsset { get; set; }
    public string ReceivedTicker { get; set; }
    public string ExpectedAssetIds { get; set; }
}