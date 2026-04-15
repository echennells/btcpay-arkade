using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.ArkPayServer.Models;

public class ArkadeAssetRefundViewModel
{
    public string StoreId { get; set; } = "";
    public string InvoiceId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public int Decimals { get; set; }
    public decimal PaidAmount { get; set; }

    [Required]
    [Range(typeof(decimal), "0.00000001", "999999999999", ErrorMessage = "Refund amount must be positive.")]
    public decimal RefundAmount { get; set; }

    [Range(0, 99.99, ErrorMessage = "Reduction must be between 0 and 99.99%.")]
    public decimal ReductionPercent { get; set; }

    public string? Description { get; set; }
}
