using Microsoft.Data.Sqlite;
using StockWiseMcp.Models;

namespace StockWiseMcp.Data;

/// <summary>
/// Read-only product queries backed by the application's SQLite database.
/// Pass the application content root path to the constructor.
/// </summary>
public sealed class SqliteProductRepository
{
    private const int SearchResultLimit = 15;
    private const int MaximumRankingCount = 10;

    private const string ProductSelectSql =
        """
        SELECT
            p.ProductId,
            p.SKU,
            p.ProductName,
            p.Description,
            p.Category,
            p.UnitPriceCents,
            p.UnitCostCents,
            p.QuantityInStock,
            p.ReorderLevel,
            p.Location,
            p.SupplierId,
            s.SupplierName,
            s.Country,
            s.LeadTimeDays
        FROM Products AS p
        INNER JOIN Suppliers AS s ON s.SupplierId = p.SupplierId
        """;

    private readonly string _connectionString;

    public SqliteProductRepository(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var databasePath = DatabasePath.GetPath(contentRootPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    /// <summary>
    /// Searches by SKU, name, description, or category, with optional category and price filters.
    /// Search text is matched as a literal substring. Price limits are in dollars.
    /// </summary>
    public Task<IReadOnlyList<ProductWithSupplier>> SearchAsync(
        string? searchText,
        string? category,
        decimal? maxPrice,
        CancellationToken cancellationToken)
    {
        if (maxPrice < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPrice), maxPrice, "The maximum price cannot be negative.");
        }

        string? searchPattern = null;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var escapedText = searchText.Trim()
                .Replace("!", "!!", StringComparison.Ordinal)
                .Replace("%", "!%", StringComparison.Ordinal)
                .Replace("_", "!_", StringComparison.Ordinal);

            searchPattern = $"%{escapedText}%";
        }

        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim();

        // Product prices are stored as integer cents. Flooring the limit preserves
        // the exact meaning of "price <= maxPrice" for any decimal dollar limit.
        long? maxPriceCents = null;
        if (maxPrice.HasValue)
        {
            var maximumStoredPrice = (decimal)int.MaxValue;
            maxPriceCents = maxPrice.Value >= maximumStoredPrice / 100m
                ? int.MaxValue
                : (long)decimal.Floor(maxPrice.Value * 100m);
        }

        return QueryProductsAsync(
            """
            WHERE
                (
                    $search IS NULL
                    OR p.SKU COLLATE NOCASE LIKE $search ESCAPE '!'
                    OR p.ProductName COLLATE NOCASE LIKE $search ESCAPE '!'
                    OR p.Description COLLATE NOCASE LIKE $search ESCAPE '!'
                    OR p.Category COLLATE NOCASE LIKE $search ESCAPE '!'
                )
                AND ($category IS NULL OR p.Category = $category COLLATE NOCASE)
                AND ($maxPriceCents IS NULL OR p.UnitPriceCents <= $maxPriceCents)
            ORDER BY p.ProductName COLLATE NOCASE, p.ProductId
            LIMIT $limit;
            """,
            cancellationToken,
            ("$search", searchPattern),
            ("$category", normalizedCategory),
            ("$maxPriceCents", maxPriceCents),
            ("$limit", SearchResultLimit));
    }

    /// <summary>Returns the exact SKU match, or null if it does not exist.</summary>
    public async Task<ProductWithSupplier?> GetBySkuAsync(
        string sku,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);

        var products = await QueryProductsAsync(
            """
            WHERE p.SKU = $sku COLLATE NOCASE
            LIMIT 1;
            """,
            cancellationToken,
            ("$sku", sku.Trim()));

        return products.Count == 0 ? null : products[0];
    }

    /// <summary>Returns products whose stock is at or below their reorder level.</summary>
    public Task<IReadOnlyList<ProductWithSupplier>> LowStockAsync(
        bool onlyOutOfStock,
        string? category,
        CancellationToken cancellationToken)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim();

        return QueryProductsAsync(
            """
            WHERE
            (
                ($onlyOutOfStock = 0 AND p.QuantityInStock <= p.ReorderLevel)
                OR ($onlyOutOfStock = 1 AND p.QuantityInStock = 0)
            )
            AND ($category IS NULL OR p.Category = $category COLLATE NOCASE);
            """,
            cancellationToken,
            ("$onlyOutOfStock", onlyOutOfStock ? 1 : 0),
            ("$category", normalizedCategory));
    }

    /// <summary>
    /// Returns up to 10 products ranked by retail value of their current stock.
    /// </summary>
    public Task<IReadOnlyList<ProductWithSupplier>> TopByValueAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ValidateRankingCount(count);

        return QueryProductsAsync(
            """
            ORDER BY
                (CAST(p.UnitPriceCents AS INTEGER) * p.QuantityInStock) DESC,
                p.ProductId
            LIMIT $limit;
            """,
            cancellationToken,
            ("$limit", count));
    }

    /// <summary>Returns inventory totals and stock-level counts.</summary>
    public async Task<InventorySummary> SummarizeAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                COUNT(*) AS ProductCount,
                (SELECT COUNT(*) FROM Suppliers) AS SupplierCount,
                COALESCE(SUM(QuantityInStock), 0) AS TotalUnits,
                COALESCE(SUM(CAST(UnitPriceCents AS INTEGER) * QuantityInStock), 0)
                    AS RetailValueCents,
                COALESCE(SUM(CAST(UnitCostCents AS INTEGER) * QuantityInStock), 0)
                    AS CostValueCents,
                COALESCE(SUM(
                    CASE WHEN QuantityInStock <= ReorderLevel THEN 1 ELSE 0 END
                ), 0) AS LowStockCount,
                COALESCE(SUM(
                    CASE WHEN QuantityInStock = 0 THEN 1 ELSE 0 END
                ), 0) AS OutOfStockCount
            FROM Products;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new InventorySummary(
            ProductCount: reader.GetInt64(0),
            SupplierCount: reader.GetInt64(1),
            TotalUnits: reader.GetInt64(2),
            TotalRetailInventoryValue: reader.GetInt64(3) / 100m,
            TotalInventoryCostValue: reader.GetInt64(4) / 100m,
            LowStockCount: reader.GetInt64(5),
            OutOfStockCount: reader.GetInt64(6));
    }

    /// <summary>Returns products supplied by the named supplier.</summary>
    public Task<IReadOnlyList<ProductWithSupplier>> GetProductsBySupplierAsync(
        string supplierName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supplierName);

        return QueryProductsAsync(
            """
            WHERE s.SupplierName = $supplierName COLLATE NOCASE
            ORDER BY p.ProductName COLLATE NOCASE, p.ProductId;
            """,
            cancellationToken,
            ("$supplierName", supplierName.Trim()));
    }

    private async Task<IReadOnlyList<ProductWithSupplier>> QueryProductsAsync(
        string whereOrderLimitSql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ProductSelectSql + "\n" + whereOrderLimitSql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var products = new List<ProductWithSupplier>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var unitPriceCents = reader.GetInt32(5);
            var unitCostCents = reader.GetInt32(6);
            var quantity = reader.GetInt32(7);

            products.Add(new ProductWithSupplier(
                ProductId: reader.GetInt32(0),
                SKU: reader.GetString(1),
                ProductName: reader.GetString(2),
                Description: reader.GetString(3),
                Category: reader.GetString(4),
                UnitPrice: unitPriceCents / 100m,
                UnitCost: unitCostCents / 100m,
                QuantityInStock: quantity,
                ReorderLevel: reader.GetInt32(8),
                Location: reader.GetString(9),
                Supplier: new Supplier
                {
                    SupplierId = reader.GetInt32(10),
                    SupplierName = reader.GetString(11),
                    Country = reader.GetString(12),
                    LeadTimeDays = reader.GetInt32(13)
                },
                RetailInventoryValue: ((long)unitPriceCents * quantity) / 100m,
                InventoryCostValue: ((long)unitCostCents * quantity) / 100m));
        }

        return products;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static void ValidateRankingCount(int count)
    {
        if (count is < 1 or > MaximumRankingCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"Count must be between 1 and {MaximumRankingCount}.");
        }
    }
}

/// <summary>Product query result with monetary amounts represented as dollars.</summary>
public sealed record ProductWithSupplier(
    int ProductId,
    string SKU,
    string ProductName,
    string Description,
    string Category,
    decimal UnitPrice,
    decimal UnitCost,
    int QuantityInStock,
    int ReorderLevel,
    string Location,
    Supplier Supplier,
    decimal RetailInventoryValue,
    decimal InventoryCostValue);

/// <summary>Aggregate inventory statistics, with monetary totals represented as dollars.</summary>
public sealed record InventorySummary(
    long ProductCount,
    long SupplierCount,
    long TotalUnits,
    decimal TotalRetailInventoryValue,
    decimal TotalInventoryCostValue,
    long LowStockCount,
    long OutOfStockCount);