using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// Payment prompt details for Ark asset payments.
/// Extends the base prompt with asset-specific information.
/// </summary>
public record ArkadeAssetPromptDetails
{
    public ArkadeAssetPromptDetails(string walletId, ArkContract contract, string assetId)
        : this(walletId, contract.ToString(), assetId)
    {
    }

    public ArkadeAssetPromptDetails(string WalletId, string ContractString, string AssetId)
    {
        this.WalletId = WalletId;
        this.ContractString = ContractString;
        this.AssetId = AssetId;
    }

    public ArkadeAssetPromptDetails()
    {
    }

    public string WalletId { get; init; }
    public string ContractString { get; init; }
    public string AssetId { get; init; }

    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContractParser.Parse(ContractString, network);
    }
}
