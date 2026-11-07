namespace StockWiseMcp.Models;

/// <summary>
/// A product held in inventory. Monetary values are stored as integer cents.
/// </summary>
public sealed class Product
{
    public int ProductId { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int UnitPriceCents { get; set; }
    public int UnitCostCents { get; set; }
    public int QuantityInStock { get; set; }
    public int ReorderLevel { get; set; }
    public string Location { get; set; } = string.Empty;
    public int SupplierId { get; set; }
}