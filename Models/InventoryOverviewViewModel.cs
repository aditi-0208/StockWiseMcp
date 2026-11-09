using StockWiseMcp.Mcp;

namespace StockWiseMcp.Models;

/// <summary>Data for the Inventory page. Retail figures only; no cost data.</summary>
public sealed record InventoryOverviewViewModel(
    InventorySummaryInfo Summary,
    IReadOnlyList<ProductInfo> LowStock,
    IReadOnlyList<ProductInfo> TopByValue);
