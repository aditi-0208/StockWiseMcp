using StockWiseMcp.Data;

namespace StockWiseMcp.Mcp;

// What the MCP tools return to callers (the chatbot, an AI model, or any other MCP client).
//
// These deliberately mirror the repository's ProductWithSupplier and InventorySummary records
// WITHOUT the cost fields (unit cost, inventory cost value, total inventory cost value), so cost data
// never leaves the server through /mcp. Property names and order match the repository records, so
// the JSON the chat code reads is unchanged apart from the removed cost fields.
// If you add a field here, check that it is safe to show to every visitor.

/// <summary>A supplier, as shown to MCP callers.</summary>
public sealed record SupplierInfo(
    int SupplierId,
    string SupplierName,
    string Country,
    int LeadTimeDays);

/// <summary>A product, as shown to MCP callers. Retail figures only; no cost data.</summary>
public sealed record ProductInfo(
    int ProductId,
    string Sku,
    string ProductName,
    string Description,
    string Category,
    decimal UnitPrice,
    int QuantityInStock,
    int ReorderLevel,
    string Location,
    SupplierInfo Supplier,
    decimal RetailInventoryValue)
{
    internal static ProductInfo From(ProductWithSupplier product) => new(
        product.ProductId,
        product.SKU,
        product.ProductName,
        product.Description,
        product.Category,
        product.UnitPrice,
        product.QuantityInStock,
        product.ReorderLevel,
        product.Location,
        new SupplierInfo(
            product.Supplier.SupplierId,
            product.Supplier.SupplierName,
            product.Supplier.Country,
            product.Supplier.LeadTimeDays),
        product.RetailInventoryValue);
}

/// <summary>Inventory totals, as shown to MCP callers. Retail value only; no cost totals.</summary>
public sealed record InventorySummaryInfo(
    long ProductCount,
    long SupplierCount,
    long TotalUnits,
    decimal TotalRetailInventoryValue,
    long LowStockCount,
    long OutOfStockCount)
{
    internal static InventorySummaryInfo From(InventorySummary summary) => new(
        summary.ProductCount,
        summary.SupplierCount,
        summary.TotalUnits,
        summary.TotalRetailInventoryValue,
        summary.LowStockCount,
        summary.OutOfStockCount);
}
