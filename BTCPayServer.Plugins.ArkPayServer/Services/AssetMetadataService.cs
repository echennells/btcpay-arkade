using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;

namespace BTCPayServer.Plugins.ArkPayServer.Services;

public record AssetMetadata(string AssetId, string Name, string Ticker, int Decimals, string? Icon, ulong TotalSupply);

public class AssetMetadataService(
    IClientTransport clientTransport,
    IMemoryCache memoryCache,
    ILogger<AssetMetadataService> logger)
{
    public async Task<AssetMetadata?> GetAssetMetadata(string assetId, CancellationToken ct = default)
    {
        var cacheKey = $"asset-metadata-{assetId}";
        if (memoryCache.TryGetValue(cacheKey, out AssetMetadata? cached))
            return cached;

        try
        {
            var details = await clientTransport.GetAssetDetailsAsync(assetId, ct);
            var metadata = MapToMetadata(assetId, details);

            memoryCache.Set(cacheKey, metadata, TimeSpan.FromHours(24));
            return metadata;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch metadata for asset {AssetId}", assetId);
            return null;
        }
    }

    private static AssetMetadata MapToMetadata(string assetId, ArkAssetDetails details)
    {
        var name = details.Metadata?.GetValueOrDefault("name") ?? assetId;
        var ticker = details.Metadata?.GetValueOrDefault("ticker") ?? "ASSET";
        var decimalsStr = details.Metadata?.GetValueOrDefault("decimals");
        var decimals = int.TryParse(decimalsStr, out var d) ? d : 0;
        var icon = details.Metadata?.GetValueOrDefault("icon");

        return new AssetMetadata(assetId, name, ticker, decimals, icon, details.Supply);
    }
}
