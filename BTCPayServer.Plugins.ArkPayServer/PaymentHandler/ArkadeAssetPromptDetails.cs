using NArk.Abstractions.Contracts;
using NArk.Core.Contracts;
using NBitcoin;

namespace BTCPayServer.Plugins.ArkPayServer.PaymentHandler;

/// <summary>
/// A single asset option available for payment on an invoice.
/// </summary>
public record AssetPaymentOption
{
    public string AssetId { get; init; } = "";
    public string Ticker { get; init; } = "";
    public string? DisplayName { get; init; }
    public int Decimals { get; init; }
    public decimal Due { get; init; }
    public string PricingMode { get; init; } = "Stablecoin";
}

/// <summary>
/// Payment prompt details for Ark asset payments.
/// Contains the shared contract/address plus all accepted asset options with pre-calculated amounts.
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

    /// <summary>
    /// The primary (default) asset ID — used for Prompt.Currency and backward compatibility.
    /// </summary>
    public string AssetId { get; init; }

    /// <summary>
    /// All accepted assets with pre-calculated due amounts for this invoice.
    /// Null or empty means single-asset mode (use AssetId only).
    /// </summary>
    public List<AssetPaymentOption>? AssetOptions { get; init; }

    public ArkContract? GetContract(Network network)
    {
        if (string.IsNullOrEmpty(ContractString))
            return null;
        return ArkContractParser.Parse(ContractString, network);
    }
}
