using System.Text.Json;
using ModelContextProtocol;
using StockWiseMcp.Data;
using StockWiseMcp.Mcp;

namespace StockWiseMcp.Tests;

/// <summary>
/// Known-answer tests for the five MCP tools (acceptance A2, A4). Every expected value is computed
/// with independent SQL against the seeded database, never by the repository under test.
/// </summary>
public sealed class McpToolTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly InventoryTools _tools;

    public McpToolTests() => _tools = new InventoryTools(new SqliteProductRepository(_db.ContentRoot));

    public void Dispose() => _db.Dispose();

    // ---------- A2: seed data ----------

    [Fact]
    public void A2_Seed_HasEnoughRelatedRecords_AndNoForeignKeyViolations()
    {
        var (products, suppliers) = _db.RowCounts();
        Assert.True(products + suppliers >= 25, $"Only {products + suppliers} records seeded.");
        Assert.True(suppliers >= 2);
        Assert.Empty(_db.Column("PRAGMA foreign_key_check"));
        Assert.Equal(0L, _db.Scalar<long>(
            "SELECT COUNT(*) FROM Products p LEFT JOIN Suppliers s ON s.SupplierId = p.SupplierId WHERE s.SupplierId IS NULL"));
    }

    // ---------- T01: lookup_product ----------

    [Fact]
    public async Task T01_LookupProduct_KnownSku_MatchesSql()
    {
        var product = await _tools.LookupProduct("SW-001");

        Assert.Equal("SW-001", product.Sku);
        Assert.Equal(_db.Scalar<string>("SELECT ProductName FROM Products WHERE SKU = 'SW-001'"), product.ProductName);
        Assert.Equal(_db.Scalar<long>("SELECT QuantityInStock FROM Products WHERE SKU = 'SW-001'"), product.QuantityInStock);
        Assert.Equal(_db.Scalar<long>("SELECT UnitPriceCents FROM Products WHERE SKU = 'SW-001'") / 100m, product.UnitPrice);
        Assert.Equal(_db.Scalar<string>(
            "SELECT s.SupplierName FROM Products p JOIN Suppliers s ON s.SupplierId = p.SupplierId WHERE p.SKU = 'SW-001'"),
            product.Supplier.SupplierName);
    }

    [Fact]
    public async Task T01_LookupProduct_IsCaseInsensitiveAndTrimmed()
    {
        var product = await _tools.LookupProduct("  sw-001 ");
        Assert.Equal("SW-001", product.Sku);
    }

    [Theory]
    [InlineData("ZZZ999")]
    [InlineData("SW-999")]
    public async Task T05_LookupProduct_UnknownSku_ReportsNotFound(string sku)
    {
        var error = await Assert.ThrowsAsync<McpException>(() => _tools.LookupProduct(sku));
        Assert.Contains("SKU", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LookupProduct_BlankSku_IsRejected(string sku) =>
        await Assert.ThrowsAsync<McpException>(() => _tools.LookupProduct(sku));

    [Fact]
    public async Task LookupProduct_OverlongSku_IsRejected() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.LookupProduct(new string('A', 51)));

    // ---------- T02: search_inventory ----------

    [Fact]
    public async Task T02_SearchInventory_ByText_MatchesSql()
    {
        var expected = _db.Column(
            "SELECT SKU FROM Products WHERE SKU LIKE '%laptop%' OR ProductName LIKE '%laptop%' " +
            "OR Description LIKE '%laptop%' OR Category LIKE '%laptop%' ORDER BY SKU");

        var results = await _tools.SearchInventory(searchText: "laptop");

        Assert.NotEmpty(expected);
        Assert.Equal(expected, results.Select(p => p.Sku).Order().ToList());
    }

    [Fact]
    public async Task T02_SearchInventory_ByCategoryAndMaxPrice_MatchesSql()
    {
        var expected = _db.Column("SELECT SKU FROM Products WHERE Category = 'Monitors' AND UnitPriceCents <= 30000 ORDER BY SKU");

        var results = await _tools.SearchInventory(category: "monitors", maxPrice: 300m);

        Assert.Equal(expected, results.Select(p => p.Sku).Order().ToList());
    }

    [Fact]
    public async Task SearchInventory_NoMatch_ReturnsEmpty() =>
        Assert.Empty(await _tools.SearchInventory(searchText: "unicorn printer"));

    [Fact]
    public async Task SearchInventory_ResultsAreBoundedTo20()
    {
        var results = await _tools.SearchInventory();
        Assert.True(results.Count <= 20, $"Returned {results.Count} rows.");
    }

    [Fact]
    public async Task SearchInventory_NegativePrice_IsRejected() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.SearchInventory(maxPrice: -1m));

    [Fact]
    public async Task SearchInventory_OverlongText_IsRejected() =>
        await Assert.ThrowsAsync<McpException>(() => _tools.SearchInventory(searchText: new string('x', 101)));

    // ---------- T03: get_inventory_summary ----------

    [Fact]
    public async Task T03_InventorySummary_MatchesSql()
    {
        var summary = await _tools.GetInventorySummary();

        Assert.Equal(_db.Scalar<long>("SELECT COUNT(*) FROM Products"), summary.ProductCount);
        Assert.Equal(_db.Scalar<long>("SELECT COUNT(*) FROM Suppliers"), summary.SupplierCount);
        Assert.Equal(_db.Scalar<long>("SELECT SUM(QuantityInStock) FROM Products"), summary.TotalUnits);
        Assert.Equal(_db.Scalar<long>("SELECT SUM(UnitPriceCents * QuantityInStock) FROM Products") / 100m, summary.TotalRetailInventoryValue);
        Assert.Equal(_db.Scalar<long>("SELECT COUNT(*) FROM Products WHERE QuantityInStock <= ReorderLevel"), summary.LowStockCount);
        Assert.Equal(_db.Scalar<long>("SELECT COUNT(*) FROM Products WHERE QuantityInStock = 0"), summary.OutOfStockCount);
    }

    // ---------- T04: rank_products_by_value ----------

    [Fact]
    public async Task T04_RankProductsByValue_Top5_MatchesSql()
    {
        var expected = _db.Column("SELECT SKU FROM Products ORDER BY UnitPriceCents * QuantityInStock DESC, ProductId LIMIT 5");

        var results = await _tools.RankProductsByValue(5);

        Assert.Equal(expected, results.Select(p => p.Sku).ToList());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(50, 10)]
    [InlineData(-3, 1)]
    public async Task RankProductsByValue_CountIsClampedTo1Through10(int requested, int expected) =>
        Assert.Equal(expected, (await _tools.RankProductsByValue(requested)).Count);

    // ---------- T04b: get_low_stock_products ----------

    [Fact]
    public async Task T04b_LowStock_MatchesSql_IncludingExactlyAtReorderLevel()
    {
        var expected = _db.Column("SELECT SKU FROM Products WHERE QuantityInStock <= ReorderLevel ORDER BY SKU");

        var results = await _tools.GetLowStockProducts();

        Assert.Equal(expected, results.Select(p => p.Sku).Order().ToList());
        Assert.All(results, p => Assert.True(p.QuantityInStock <= p.ReorderLevel));
    }

    [Fact]
    public async Task LowStock_OutOfStockItemsComeFirst()
    {
        var results = await _tools.GetLowStockProducts();
        var firstInStock = results.ToList().FindIndex(p => p.QuantityInStock > 0);
        Assert.All(results.Take(firstInStock < 0 ? results.Count : firstInStock), p => Assert.Equal(0, p.QuantityInStock));
    }

    [Fact]
    public async Task LowStock_OnlyOutOfStock_MatchesSql()
    {
        var expected = _db.Column("SELECT SKU FROM Products WHERE QuantityInStock = 0 ORDER BY SKU");
        var results = await _tools.GetLowStockProducts(onlyOutOfStock: true);
        Assert.Equal(expected, results.Select(p => p.Sku).Order().ToList());
    }

    [Fact]
    public async Task LowStock_UnknownCategory_ReturnsEmpty() =>
        Assert.Empty(await _tools.GetLowStockProducts(category: "Furniture"));

    // ---------- Security: data exposure and SQL injection ----------

    [Fact]
    public async Task ToolOutput_NeverContainsCostData()
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            await _tools.LookupProduct("SW-001"),
            await _tools.SearchInventory(),
            await _tools.GetInventorySummary(),
            await _tools.RankProductsByValue(10),
            await _tools.GetLowStockProducts(),
        });

        Assert.DoesNotContain("cost", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("SW-001'; DROP TABLE Products;--")]
    [InlineData("%")]
    [InlineData("_")]
    public async Task SEC01_SqlLikeSearchText_IsTreatedAsText_AndChangesNothing(string attack)
    {
        var before = _db.RowCounts();

        var results = await _tools.SearchInventory(searchText: attack);
        await Assert.ThrowsAsync<McpException>(() => _tools.LookupProduct(attack));

        Assert.Empty(results);
        Assert.Equal(before, _db.RowCounts());
    }
}
