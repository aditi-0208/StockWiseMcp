namespace StockWiseMcp.Models;

/// <summary>
/// A supplier that provides products to StockWise.
/// </summary>
public sealed class Supplier
{
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public int LeadTimeDays { get; set; }
}