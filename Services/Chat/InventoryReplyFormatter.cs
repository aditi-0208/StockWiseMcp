using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StockWiseMcp.Services.Chat;

/// <summary>
/// Turns the JSON returned by the MCP inventory tools into short, natural-language answers.
/// Only customer-safe fields are shown; unit cost and cost totals are intentionally left out.
/// </summary>
public static class InventoryReplyFormatter
{
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    // The search tool returns at most this many rows (limit set by the repository).
    private const int SearchResultCap = 15;

    /// <summary>
    /// Renders the result of an MCP tool call exactly as the data says, for any of the inventory tools.
    /// AI mode uses this so every figure shown to the user comes from the tool result, never from the model.
    /// </summary>
    /// <returns>The text, or null if the tool is unknown or the data does not have the expected shape.</returns>
    public static string? FormatToolResult(
        string toolName,
        JsonElement data,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var expectsArray = toolName is GuidedDemoTools.SearchInventory
            or GuidedDemoTools.RankProductsByValue
            or GuidedDemoTools.GetLowStockProducts;
        var expectedKind = expectsArray ? JsonValueKind.Array : JsonValueKind.Object;

        if (data.ValueKind != expectedKind || !HasExpectedFields(toolName, data))
        {
            return null; // Never show a card of empty/zero values because the data had an unexpected shape.
        }

        return toolName switch
        {
            GuidedDemoTools.LookupProduct => FormatProduct(data),
            GuidedDemoTools.SearchInventory => FormatSearch(
                data,
                arguments.GetValueOrDefault("searchText") as string,
                arguments.GetValueOrDefault("maxPrice") as decimal?),
            GuidedDemoTools.GetInventorySummary => FormatSummary(data),
            GuidedDemoTools.RankProductsByValue => FormatRanking(data, HighestFirst(arguments)),
            GuidedDemoTools.GetLowStockProducts => FormatLowStock(
                data,
                arguments.GetValueOrDefault("onlyOutOfStock") is true,
                keyword: null),
            _ => null
        };
    }

    /// <summary>True if the data actually contains the fields the formatter is about to read.</summary>
    private static bool HasExpectedFields(string toolName, JsonElement data)
    {
        static bool Has(JsonElement e, params string[] names) =>
            e.ValueKind == JsonValueKind.Object && names.All(n => e.TryGetProperty(n, out _));

        return toolName switch
        {
            GuidedDemoTools.GetInventorySummary => Has(data, "productCount", "totalUnits", "totalRetailInventoryValue"),
            GuidedDemoTools.LookupProduct => Has(data, "sku", "productName", "unitPrice", "quantityInStock"),
            // Lists may legitimately be empty; every item present must look like a product.
            GuidedDemoTools.SearchInventory or GuidedDemoTools.RankProductsByValue or GuidedDemoTools.GetLowStockProducts
                when data.ValueKind == JsonValueKind.Array =>
                data.EnumerateArray().All(item => Has(item, "sku", "productName", "unitPrice", "quantityInStock")),
            _ => false
        };
    }

    public static string FormatProduct(JsonElement product)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{Text(product, "productName")} ({Text(product, "sku")}) - {Text(product, "category")}");

        var description = Text(product, "description");
        if (description.Length > 0)
        {
            builder.AppendLine(description);
        }

        var quantity = Int(product, "quantityInStock");
        var reorder = Int(product, "reorderLevel");

        builder.AppendLine($"- Price: {Dollars(product, "unitPrice")}");
        builder.AppendLine($"- In stock: {Units(quantity)} (reorder level {reorder}) - {StockStatus(quantity, reorder)}");
        builder.AppendLine($"- Location: {Text(product, "location")}");

        if (product.TryGetProperty("supplier", out var supplier) && supplier.ValueKind == JsonValueKind.Object)
        {
            builder.AppendLine(
                $"- Supplier: {Text(supplier, "supplierName")} " +
                $"({Text(supplier, "country")}, {Int(supplier, "leadTimeDays")}-day lead time)");
        }

        builder.Append($"- Value of stock on hand: {Dollars(product, "retailInventoryValue")}");
        return builder.ToString();
    }

    public static string FormatSearch(JsonElement products, string? searchText, decimal? maxPrice)
    {
        var items = products.ValueKind == JsonValueKind.Array ? products.EnumerateArray().ToList() : [];
        var description = DescribeSearch(searchText, maxPrice);

        if (items.Count == 0)
        {
            return $"I couldn't find any products {description}. " +
                   "Try a different keyword, such as a product type (laptops, monitors, audio) or a SKU like SW-001.";
        }

        var builder = new StringBuilder();
        builder.AppendLine(items.Count == 1
            ? $"I found 1 product {description}:"
            : $"I found {items.Count} products {description}:");

        foreach (var item in items)
        {
            var quantity = Int(item, "quantityInStock");
            var reorder = Int(item, "reorderLevel");
            var flag = quantity == 0 ? " [OUT OF STOCK]" : quantity <= reorder ? " [low stock]" : string.Empty;

            builder.AppendLine(
                $"- {Text(item, "productName")} ({Text(item, "sku")}): {Dollars(item, "unitPrice")}, " +
                $"{quantity} in stock ({Text(item, "location")}){flag}");
        }

        builder.Append(items.Count >= SearchResultCap
            ? $"That's the first {SearchResultCap} matches - add a keyword or a price limit to narrow it down. "
            : string.Empty);
        builder.Append("Ask about a SKU (for example SW-001) for full details.");
        return builder.ToString();
    }

    public static string FormatSummary(JsonElement summary)
    {
        var low = Long(summary, "lowStockCount");
        var outOfStock = Long(summary, "outOfStockCount");

        var builder = new StringBuilder();
        builder.AppendLine("Here's the current inventory picture:");
        builder.AppendLine(
            $"- {Long(summary, "productCount")} products from {Long(summary, "supplierCount")} suppliers");
        builder.AppendLine($"- {Long(summary, "totalUnits"):N0} units in stock");
        builder.AppendLine($"- Stock value at retail prices: {Dollars(summary, "totalRetailInventoryValue")}");
        builder.Append(LowStockSentence(low, outOfStock));
        return builder.ToString();
    }

    /// <param name="products">Result of get_low_stock_products (already sorted by urgency).</param>
    /// <param name="onlyOutOfStock">True if the user asked only about products that are out of stock.</param>
    /// <param name="keyword">Optional product-type word; only matching products are listed.</param>
    public static string FormatLowStock(JsonElement products, bool onlyOutOfStock, string? keyword)
    {
        var items = products.ValueKind == JsonValueKind.Array ? products.EnumerateArray().ToList() : [];
        var allCount = items.Count;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            items = items.Where(item => MatchesKeyword(item, keyword)).ToList();
        }

        var scope = string.IsNullOrWhiteSpace(keyword) ? string.Empty : $" matching \"{keyword}\"";

        // There ARE products to reorder, but none of them match the filter word. Say exactly that;
        // "good news, nothing is low" would be false.
        if (items.Count == 0 && allCount > 0)
        {
            var allNoun = allCount == 1 ? "product" : "products";
            var allVerb = allCount == 1 ? "is" : "are";
            var allPronoun = allCount == 1 ? "it" : "them";

            return onlyOutOfStock
                ? $"No out-of-stock products match \"{keyword}\". In total {allCount} {allNoun} {allVerb} out of stock - " +
                  $"ask \"what is out of stock?\" to see {allPronoun}."
                : $"No low-stock products match \"{keyword}\". In total {allCount} {allNoun} {allVerb} at or below the reorder level - " +
                  $"ask \"which products need reordering?\" to see {allPronoun}.";
        }

        if (items.Count == 0)
        {
            return onlyOutOfStock
                ? $"Good news - no products{scope} are out of stock right now."
                : $"Good news - no products{scope} are at or below their reorder level right now.";
        }

        var outOfStock = items.Count(item => Int(item, "quantityInStock") == 0);
        var noun = items.Count == 1 ? "product" : "products";

        var builder = new StringBuilder();
        builder.AppendLine(onlyOutOfStock
            ? $"{items.Count} {noun}{scope} {(items.Count == 1 ? "is" : "are")} out of stock:"
            : $"{items.Count} {noun}{scope} {(items.Count == 1 ? "is" : "are")} at or below the reorder level" +
              (outOfStock > 0 ? $" ({outOfStock} completely out of stock):" : ":"));

        foreach (var item in items)
        {
            var quantity = Int(item, "quantityInStock");
            var reorder = Int(item, "reorderLevel");
            var status = quantity == 0
                ? "OUT OF STOCK"
                : $"{quantity} left, reorder level {reorder}";

            var supplier = item.TryGetProperty("supplier", out var s) && s.ValueKind == JsonValueKind.Object
                ? $" - supplier: {Text(s, "supplierName")}, {Int(s, "leadTimeDays")}-day lead time"
                : string.Empty;

            builder.AppendLine(
                $"- {Text(item, "productName")} ({Text(item, "sku")}): {status}{supplier}");
        }

        builder.Append("Ask about a SKU (for example SW-005) for full details.");
        return builder.ToString();
    }

    /// <summary>True if every word of the keyword appears somewhere in the product's text fields.</summary>
    private static bool MatchesKeyword(JsonElement item, string keyword)
    {
        var haystack = string.Join(' ', new[] { "productName", "category", "sku", "description" }
            .Select(field => Text(item, field)));

        return keyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The answer to "how many products need reordering / are out of stock?".</summary>
    public static string FormatLowStockCount(JsonElement summary, bool onlyOutOfStock)
    {
        var low = Long(summary, "lowStockCount");
        var outOfStock = Long(summary, "outOfStockCount");

        if (onlyOutOfStock)
        {
            return outOfStock == 0
                ? "No products are completely out of stock right now."
                : $"{outOfStock} {(outOfStock == 1 ? "product is" : "products are")} completely out of stock. " +
                  "Ask \"what is out of stock?\" to see which.";
        }

        return LowStockSentence(low, outOfStock) +
               (low == 0 ? string.Empty : " Ask \"which products need reordering?\" to see the list.");
    }

    /// <summary>Reads the rank direction from the tool arguments; defaults to highest-value first.</summary>
    private static bool HighestFirst(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.GetValueOrDefault("highestFirst") switch
        {
            false => false,
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => true
        };

    public static string FormatRanking(JsonElement products, bool highestFirst = true)
    {
        var items = products.ValueKind == JsonValueKind.Array ? products.EnumerateArray().ToList() : [];
        if (items.Count == 0)
        {
            return "There are no products in the inventory to rank.";
        }

        var ordering = highestFirst ? "top" : "lowest";
        var builder = new StringBuilder();
        builder.AppendLine($"Here are the {ordering} {items.Count} products by value of stock on hand (price x quantity):");

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            builder.AppendLine(
                $"{i + 1}. {Text(item, "productName")} ({Text(item, "sku")}): " +
                $"{Dollars(item, "retailInventoryValue")} " +
                $"({Int(item, "quantityInStock")} x {Dollars(item, "unitPrice")})");
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatHelp(IEnumerable<(string Name, string Description)> tools)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Hi! I'm the StockWise assistant (Guided Demo mode). I answer basic inventory questions using these tools:");

        foreach (var (name, description) in tools)
        {
            builder.AppendLine($"- {name}: {FirstSentence(description)}");
        }

        builder.AppendLine();
        builder.AppendLine("Try asking:");
        builder.AppendLine("- \"Tell me about SW-001\"");
        builder.AppendLine("- \"Do we have any monitors under $200?\"");
        builder.AppendLine("- \"Which products need reordering?\"");
        builder.AppendLine("- \"Give me an inventory summary\"");
        builder.Append("- \"What are the top 5 products by inventory value?\"");
        return builder.ToString();
    }

    public static string FormatUnknown() =>
        "I'm not sure what you're asking. In Guided Demo mode I can look up a product by SKU, " +
        "search products by keyword or price, list products that need reordering, summarize the inventory, " +
        "or rank products by stock value. " +
        "Type \"help\" to see examples.";

    // The canned replies below are fixed text. They deliberately do not repeat any part of the
    // user's message, so they cannot be used to reflect markup or instructions back to the page.

    private const string TryAsking =
        "Try asking about a product (\"Tell me about SW-001\"), a search (\"Do we have any monitors under $200?\"), " +
        "stock levels (\"Which products need reordering?\"), or type \"help\" for more.";

    public static string FormatOffTopic() =>
        "That's outside what I can help with - I only answer questions about StockWise inventory. " + TryAsking;

    public static string FormatThanks() =>
        "You're welcome! Ask me anything else about the inventory whenever you like.";

    public static string FormatSqlCommand() =>
        "That looks like a database command. I don't run SQL - I can only use a fixed set of read-only " +
        "inventory tools, so nothing was run. Ask in plain English instead. " + TryAsking;

    public static string FormatPromptInjection() =>
        "I can't change how I work, share my instructions or settings, or run commands - I'm a simple " +
        "assistant that only answers StockWise inventory questions. " + TryAsking;

    private static string DescribeSearch(string? searchText, decimal? maxPrice)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            parts.Add($"matching \"{searchText}\"");
        }

        if (maxPrice is not null)
        {
            parts.Add($"priced at {maxPrice.Value.ToString("C2", Money)} or less");
        }

        return parts.Count == 0 ? "in the inventory" : string.Join(" and ", parts);
    }

    private static string LowStockSentence(long low, long outOfStock)
    {
        if (low == 0)
        {
            return "No products are at or below their reorder level.";
        }

        return $"{low} {(low == 1 ? "product is" : "products are")} at or below the reorder level, " +
               $"and {outOfStock} {(outOfStock == 1 ? "is" : "are")} completely out of stock.";
    }

    private static string StockStatus(int quantity, int reorder) =>
        quantity == 0 ? "OUT OF STOCK" : quantity <= reorder ? "low, time to reorder" : "stock level is healthy";

    private static string Units(int quantity) => quantity == 1 ? "1 unit" : $"{quantity} units";

    private static string FirstSentence(string description)
    {
        // Tool descriptions are written for the LLM and run long. Keep only the opening clause.
        var end = description.IndexOfAny([':', '.']);
        var sentence = end > 0 ? description[..end] : description;
        return sentence.Trim().TrimEnd('.');
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string Dollars(JsonElement element, string name) =>
        (element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var number) ? number : 0m)
        .ToString("C2", Money);
}
