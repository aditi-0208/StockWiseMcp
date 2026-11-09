using System.Net;

namespace StockWiseMcp.Tests;

/// <summary>The MVC pages render, and the inventory page shows correct figures without cost data.</summary>
public sealed class PageTests : IClassFixture<ChatApiTests.Server>
{
    private readonly StockWiseFactory _factory;

    public PageTests(ChatApiTests.Server server) => _factory = server.Factory;

    [Theory]
    [InlineData("/", "Ask your inventory a question")]
    [InlineData("/Home/Chat", "id=\"chat-form\"")]
    [InlineData("/Home/Inventory", "Needs reordering")]
    [InlineData("/Home/Architecture", "get_low_stock_products")]
    public async Task Page_Renders(string path, string expected)
    {
        var response = await _factory.CreateBrowserClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InventoryPage_ShowsSqlTotals_AndEveryLowStockSku_ButNoCost()
    {
        var html = await _factory.CreateBrowserClient().GetStringAsync("/Home/Inventory");
        var value = _factory.Database.Scalar<long>("SELECT SUM(UnitPriceCents * QuantityInStock) FROM Products") / 100m;

        Assert.Contains(value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US")), html);
        Assert.All(
            _factory.Database.Column("SELECT SKU FROM Products WHERE QuantityInStock <= ReorderLevel"),
            sku => Assert.Contains(sku, html));
        Assert.DoesNotContain("unit cost", html, StringComparison.OrdinalIgnoreCase);
    }
}
