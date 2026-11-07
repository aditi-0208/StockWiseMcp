using Microsoft.Data.Sqlite;

namespace StockWiseMcp.Data;

/// <summary>
/// Creates the inventory schema and inserts the initial sample data when the database is empty.
/// </summary>
public static class DatabaseInitializer
{
    private static readonly (string Name, string Country, int LeadTimeDays)[] Suppliers =
    [
        ("Northstar Components", "United States", 5),
        ("Pacific Circuit Supply", "Taiwan", 12),
        ("Alpine Audio Works", "Germany", 8),
        ("BrightScreen Electronics", "South Korea", 10),
        ("Evergreen Peripherals", "Japan", 7),
        ("Summit Mobile Parts", "United States", 6)
    ];

    private static readonly SeedProduct[] Products =
    [
        new("SW-001", "Creator Laptop 14", "Portable laptop for creative work.", "Laptops", 89900, 70000, 7, 4, "Aisle A1", 1),
        new("SW-002", "Everyday Laptop 15", "General-purpose laptop for home and office.", "Laptops", 64900, 49000, 12, 5, "Aisle A1", 2),
        new("SW-003", "Gaming Laptop 15", "High-performance laptop with discrete graphics.", "Laptops", 149900, 118000, 3, 4, "Aisle A2", 1),
        new("SW-004", "Business Laptop 13", "Lightweight business laptop with extended battery life.", "Laptops", 109900, 84000, 8, 3, "Aisle A2", 6),
        new("SW-005", "Ultralight Laptop 14", "Thin laptop designed for travel.", "Laptops", 119900, 93000, 0, 3, "Warehouse W1", 2),

        new("SW-006", "Studio Monitor 27", "27-inch color-accurate display.", "Monitors", 32900, 24000, 9, 3, "Aisle B1", 4),
        new("SW-007", "Office Monitor 24", "24-inch full-HD office display.", "Monitors", 15900, 11200, 18, 6, "Aisle B1", 4),
        new("SW-008", "Portable Monitor 16", "Compact USB-C travel display.", "Monitors", 21900, 15800, 2, 4, "Aisle B2", 4),
        new("SW-009", "Curved Gaming Monitor 32", "Curved high-refresh-rate gaming display.", "Monitors", 44900, 34500, 5, 3, "Aisle B2", 4),

        new("SW-010", "Mechanical Keyboard", "Wired mechanical keyboard with tactile switches.", "Keyboards", 8900, 5400, 24, 8, "Aisle C1", 5),
        new("SW-011", "Compact Wireless Keyboard", "Compact wireless keyboard for desk or travel.", "Keyboards", 5900, 3400, 1, 5, "Aisle C1", 5),
        new("SW-012", "Ergonomic Keyboard", "Split-layout keyboard with adjustable palm rest.", "Keyboards", 12900, 8100, 7, 3, "Aisle C2", 5),

        new("SW-013", "Wireless Precision Mouse", "Wireless mouse with adjustable sensitivity.", "Mice", 4900, 2700, 32, 10, "Aisle C2", 5),
        new("SW-014", "Gaming Mouse", "Wired gaming mouse with programmable buttons.", "Mice", 6900, 3900, 3, 5, "Aisle C2", 5),
        new("SW-015", "Ergonomic Trackball", "Trackball mouse for a relaxed hand position.", "Mice", 7900, 4600, 0, 4, "Warehouse W2", 5),

        new("SW-016", "USB-C Docking Station", "Dock with display, network, and USB ports.", "Accessories", 13900, 9200, 11, 4, "Aisle D1", 1),
        new("SW-017", "65W USB-C Charger", "Compact USB-C wall charger.", "Accessories", 3900, 2100, 28, 10, "Aisle D1", 1),
        new("SW-018", "USB-C Hub 7-Port", "Multiport hub for laptops and tablets.", "Accessories", 5900, 3300, 6, 5, "Aisle D1", 1),
        new("SW-019", "Laptop Sleeve 14", "Padded protective sleeve for 14-inch laptops.", "Accessories", 2900, 1300, 15, 6, "Aisle D2", 5),

        new("SW-020", "Noise-Canceling Headphones", "Over-ear headphones with active noise cancellation.", "Audio", 19900, 13700, 4, 5, "Aisle E1", 3),
        new("SW-021", "Bluetooth Earbuds", "Wireless earbuds with charging case.", "Audio", 9900, 6200, 20, 7, "Aisle E1", 3),
        new("SW-022", "USB Podcast Microphone", "Desktop USB microphone for recording and calls.", "Audio", 10900, 7200, 2, 4, "Aisle E2", 3),
        new("SW-023", "Desktop Speakers", "Compact stereo speakers for a computer setup.", "Audio", 6900, 4100, 8, 4, "Aisle E2", 3),

        new("SW-024", "1080p Webcam", "Full-HD webcam with privacy cover.", "Cameras", 7900, 4800, 13, 5, "Aisle F1", 4),
        new("SW-025", "4K Webcam", "Ultra-HD webcam with autofocus.", "Cameras", 14900, 9800, 1, 3, "Aisle F1", 4),

        new("SW-026", "Wi-Fi 6 Router", "Dual-band wireless router for home or office.", "Networking", 12900, 8700, 10, 4, "Aisle F2", 2),
        new("SW-027", "8-Port Gigabit Switch", "Unmanaged gigabit Ethernet switch.", "Networking", 4900, 2900, 17, 6, "Aisle F2", 2),

        new("SW-028", "Portable SSD 1TB", "External solid-state drive with USB-C connection.", "Storage", 10900, 7600, 5, 4, "Aisle G1", 6),
        new("SW-029", "USB Flash Drive 128GB", "Portable USB storage drive.", "Storage", 1900, 800, 40, 12, "Aisle G1", 6),
        new("SW-030", "External HDD 2TB", "Portable external hard drive for backups.", "Storage", 7900, 5600, 0, 3, "Warehouse W3", 6)
    ];

    /// <summary>
    /// Creates the schema and seeds sample rows only when both inventory tables are empty.
    /// </summary>
    public static async Task InitializeAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);

        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Set explicitly as well as in the connection string so foreign-key enforcement is clear.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        // Keep schema creation and seeding atomic; disposal rolls back if an operation fails.
        using var transaction = connection.BeginTransaction();

        await using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.Transaction = transaction;
            schemaCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Suppliers (
                    SupplierId INTEGER PRIMARY KEY,
                    SupplierName TEXT NOT NULL,
                    Country TEXT NOT NULL,
                    LeadTimeDays INTEGER NOT NULL CHECK (LeadTimeDays >= 0)
                );

                CREATE TABLE IF NOT EXISTS Products (
                    ProductId INTEGER PRIMARY KEY,
                    SKU TEXT NOT NULL UNIQUE,
                    ProductName TEXT NOT NULL,
                    Description TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    UnitPriceCents INTEGER NOT NULL CHECK (UnitPriceCents >= 0),
                    UnitCostCents INTEGER NOT NULL CHECK (UnitCostCents >= 0),
                    QuantityInStock INTEGER NOT NULL CHECK (QuantityInStock >= 0),
                    ReorderLevel INTEGER NOT NULL CHECK (ReorderLevel >= 0),
                    Location TEXT NOT NULL,
                    SupplierId INTEGER NOT NULL,
                    FOREIGN KEY (SupplierId) REFERENCES Suppliers (SupplierId)
                        ON UPDATE CASCADE
                        ON DELETE RESTRICT
                );
                """;

            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var supplierCount = await GetRowCountAsync(
            connection, transaction, "Suppliers", cancellationToken);
        var productCount = await GetRowCountAsync(
            connection, transaction, "Products", cancellationToken);

        // Do not overwrite or augment an existing inventory, including a partially populated one.
        if (supplierCount > 0 || productCount > 0)
        {
            transaction.Commit();
            return;
        }

        foreach (var supplier in Suppliers)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Suppliers (SupplierName, Country, LeadTimeDays)
                VALUES ($name, $country, $leadTimeDays);
                """;
            command.Parameters.AddWithValue("$name", supplier.Name);
            command.Parameters.AddWithValue("$country", supplier.Country);
            command.Parameters.AddWithValue("$leadTimeDays", supplier.LeadTimeDays);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var product in Products)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO Products (
                    SKU, ProductName, Description, Category,
                    UnitPriceCents, UnitCostCents, QuantityInStock,
                    ReorderLevel, Location, SupplierId
                )
                VALUES (
                    $sku, $productName, $description, $category,
                    $unitPriceCents, $unitCostCents, $quantityInStock,
                    $reorderLevel, $location, $supplierId
                );
                """;
            command.Parameters.AddWithValue("$sku", product.SKU);
            command.Parameters.AddWithValue("$productName", product.ProductName);
            command.Parameters.AddWithValue("$description", product.Description);
            command.Parameters.AddWithValue("$category", product.Category);
            command.Parameters.AddWithValue("$unitPriceCents", product.UnitPriceCents);
            command.Parameters.AddWithValue("$unitCostCents", product.UnitCostCents);
            command.Parameters.AddWithValue("$quantityInStock", product.QuantityInStock);
            command.Parameters.AddWithValue("$reorderLevel", product.ReorderLevel);
            command.Parameters.AddWithValue("$location", product.Location);
            command.Parameters.AddWithValue("$supplierId", product.SupplierId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    private static async Task<long> GetRowCountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        // Table names here are fixed internal constants, not user input.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    private sealed record SeedProduct(
        string SKU,
        string ProductName,
        string Description,
        string Category,
        int UnitPriceCents,
        int UnitCostCents,
        int QuantityInStock,
        int ReorderLevel,
        string Location,
        int SupplierId);
}