using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using StockWiseMcp.Data;

namespace StockWiseMcp.Mcp;

/// <summary>
/// MCP tools that let the chatbot query StockWise inventory.
/// Each tool validates its input, then delegates to the SQLite repository.
/// Invalid input is reported as an <see cref="McpException"/> so the model
/// receives a readable error message and can correct its call.
/// </summary>
[McpServerToolType]
public sealed class InventoryTools
{
    private const int MaxTextLength = 100;
    private const int MaxSkuLength = 50;
    private const int MinRankingCount = 1;
    private const int MaxRankingCount = 10;
    private const int DefaultRankingCount = 5;

    private readonly SqliteProductRepository _repository;

    public InventoryTools(SqliteProductRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    [McpServerTool(Name = "lookup_product", ReadOnly = true)]
    [Description(
        "Look up ONE product by its exact SKU (for example 'SW-001') and return its full details: " +
        "price, stock quantity, reorder level, location, supplier and retail inventory value. " +
        "Use this when the user gives a specific SKU. If the user only gives a product name or " +
        "description, use search_inventory instead.")]
    public async Task<ProductInfo> LookupProduct(
        [Description("The exact product SKU, such as 'SW-001'. Case-insensitive.")] string sku,
        CancellationToken cancellationToken = default)
    {
        var cleanSku = sku?.Trim();
        if (string.IsNullOrEmpty(cleanSku))
        {
            throw new McpException("The 'sku' argument is required, for example 'SW-001'.");
        }

        if (cleanSku.Length > MaxSkuLength)
        {
            throw new McpException($"The 'sku' argument must be at most {MaxSkuLength} characters.");
        }

        var product = await _repository.GetBySkuAsync(cleanSku, cancellationToken);

        return product is null
            ? throw new McpException(
                $"No product with SKU '{cleanSku}' exists. Try search_inventory to find it by name.")
            : ProductInfo.From(product);
    }

    [McpServerTool(Name = "search_inventory", ReadOnly = true)]
    [Description(
        "Search products by keyword and/or category and/or maximum price. The keyword is matched " +
        "(case-insensitive, substring) against SKU, product name, description and category. " +
        "Returns at most 15 products sorted by name, each with price, stock, location and supplier. " +
        "Use this for questions like 'do we have any monitors?', 'show laptops under $1000' or " +
        "'find USB-C products'. All arguments are optional and combine with AND. " +
        "This tool does not filter by stock level; use another tool for low-stock questions.")]
    public async Task<IReadOnlyList<ProductInfo>> SearchInventory(
        [Description("Keyword to look for, such as 'laptop' or 'usb-c'. Omit to not filter by keyword.")]
        string? searchText = null,
        [Description("Exact category name, such as 'Laptops', 'Monitors', 'Keyboards', 'Mice', " +
                     "'Accessories', 'Audio', 'Cameras', 'Networking' or 'Storage'. Omit for all categories.")]
        string? category = null,
        [Description("Maximum unit price in US dollars (not cents), for example 500 or 49.99. Must be 0 or more.")]
        decimal? maxPrice = null,
        CancellationToken cancellationToken = default)
    {
        searchText = searchText?.Trim();
        category = category?.Trim();

        if (searchText is { Length: > MaxTextLength })
        {
            throw new McpException($"The 'searchText' argument must be at most {MaxTextLength} characters.");
        }

        if (category is { Length: > MaxTextLength })
        {
            throw new McpException($"The 'category' argument must be at most {MaxTextLength} characters.");
        }

        if (maxPrice is < 0)
        {
            throw new McpException("The 'maxPrice' argument cannot be negative.");
        }

        var products = await _repository.SearchAsync(searchText, category, maxPrice, cancellationToken);

        return products.Select(ProductInfo.From).ToList();
    }

    [McpServerTool(Name = "get_inventory_summary", ReadOnly = true)]
    [Description(
        "Get overall inventory statistics: number of products and suppliers, total units in stock, " +
        "total retail value of the stock (in US dollars), how many products " +
        "are low on stock (at or below their reorder level, including out-of-stock items), and how " +
        "many are completely out of stock. Use this for broad questions like 'how is our inventory " +
        "doing?' or 'what is our total stock value?'. Takes no arguments.")]
    public async Task<InventorySummaryInfo> GetInventorySummary(
        CancellationToken cancellationToken = default)
        => InventorySummaryInfo.From(await _repository.SummarizeAsync(cancellationToken));

    [McpServerTool(Name = "rank_products_by_value", ReadOnly = true)]
    [Description(
        "Rank products by the retail value of the stock currently on hand (unit price x quantity " +
        "in stock). Returns each product with its inventory value in US dollars. By default the " +
        "HIGHEST value products are returned first; set highestFirst to false for the LOWEST value " +
        "products first. Use this for questions like 'which products tie up the most money?', " +
        "'top 5 products by inventory value' or 'which products have the lowest inventory value?'. " +
        "This ranks individual products, not categories.")]
    public async Task<IReadOnlyList<ProductInfo>> RankProductsByValue(
        [Description("How many products to return, from 1 to 10. Defaults to 5. " +
                     "Values outside the range are adjusted to fit.")]
        int count = DefaultRankingCount,
        [Description("True (the default) returns the highest-value products first; " +
                     "false returns the lowest-value products first.")]
        bool highestFirst = true,
        CancellationToken cancellationToken = default)
    {
        var clampedCount = Math.Clamp(count, MinRankingCount, MaxRankingCount);

        var products = await _repository.TopByValueAsync(clampedCount, cancellationToken, descending: highestFirst);

        return products.Select(ProductInfo.From).ToList();
    }

    [McpServerTool(Name = "get_low_stock_products", ReadOnly = true)]
    [Description(
        "List the products that need restocking: those whose quantity in stock is at or below their " +
        "reorder level (this includes products that are completely out of stock). Each product comes " +
        "with its stock level, reorder level, location and supplier (including supplier lead time). " +
        "Results are sorted by urgency: out-of-stock products first, then those furthest below their " +
        "reorder level. Use this for questions like 'which products need reordering?', 'what is running " +
        "low?' or 'what is out of stock?'. For just the NUMBER of low-stock products, " +
        "get_inventory_summary is enough.")]
    public async Task<IReadOnlyList<ProductInfo>> GetLowStockProducts(
        [Description("Set to true to list only products that are completely out of stock (quantity 0). " +
                     "Defaults to false, which lists everything at or below its reorder level.")]
        bool onlyOutOfStock = false,
        [Description("Optional exact category name to restrict the list to, such as 'Laptops' or 'Audio'. " +
                     "Omit for all categories.")]
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        category = category?.Trim();
        if (category is { Length: > MaxTextLength })
        {
            throw new McpException($"The 'category' argument must be at most {MaxTextLength} characters.");
        }

        var products = await _repository.LowStockAsync(onlyOutOfStock, category, cancellationToken);

        // The repository returns these in no particular order, so sort by urgency here:
        // out of stock first, then the largest shortfall against the reorder level.
        return products
            .OrderBy(p => p.QuantityInStock == 0 ? 0 : 1)
            .ThenBy(p => p.QuantityInStock - p.ReorderLevel)
            .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
            .Select(ProductInfo.From)
            .ToList();
    }
}
