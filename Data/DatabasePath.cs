namespace StockWiseMcp.Data;

/// <summary>
/// Resolves the local SQLite database location beneath the application content root.
/// </summary>
public static class DatabasePath
{
    public const string DatabaseFileName = "stockwise.db";

    /// <summary>
    /// Creates the application's App_Data directory if needed and returns the database path.
    /// </summary>
    public static string GetPath(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var dataDirectory = Path.Combine(contentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);

        return Path.Combine(dataDirectory, DatabaseFileName);
    }
}