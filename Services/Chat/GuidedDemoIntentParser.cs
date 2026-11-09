using System.Globalization;
using System.Text.RegularExpressions;

namespace StockWiseMcp.Services.Chat;

/// <summary>What the user is asking for.</summary>
public enum ChatIntentKind
{
    Help,
    Lookup,
    Search,
    Summary,
    Rank,
    LowStock,

    /// <summary>"How many products need reordering?": a count, answered from the inventory summary.</summary>
    LowStockCount,

    // The kinds below never need an MCP tool; the chat service answers them locally.

    /// <summary>"thanks", "bye" and similar.</summary>
    Thanks,

    /// <summary>A clearly non-inventory topic such as the weather or jokes.</summary>
    OffTopic,

    /// <summary>Text that looks like a SQL statement or SQL injection attempt.</summary>
    SqlCommand,

    /// <summary>An attempt to change the bot's instructions, extract secrets, or run commands.</summary>
    PromptInjection,

    Unknown
}

/// <summary>
/// A parsed user request. <see cref="ToolName"/> is the MCP tool to call (null when no tool is needed)
/// and <see cref="Arguments"/> holds the arguments to send to it. <see cref="Keyword"/> is an optional
/// product-type word (for example "laptop") that the reply is narrowed to after the tool returns.
/// </summary>
public sealed record ChatIntent(
    ChatIntentKind Kind,
    string? ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    string? Keyword = null);

/// <summary>Names of the MCP tools the Guided Demo knows how to use.</summary>
public static class GuidedDemoTools
{
    public const string LookupProduct = "lookup_product";
    public const string SearchInventory = "search_inventory";
    public const string GetInventorySummary = "get_inventory_summary";
    public const string RankProductsByValue = "rank_products_by_value";
    public const string GetLowStockProducts = "get_low_stock_products";
}

/// <summary>
/// Rule-based understanding of basic inventory questions. No AI model or API key is involved;
/// this is deliberately simple, deterministic and easy to unit test.
/// </summary>
public static class GuidedDemoIntentParser
{
    private const int MaxSearchTextLength = 100;

    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>();

    private static readonly Regex SkuPattern =
        new(@"\b([A-Za-z]{1,5})-(\d{1,6})\b", RegexOptions.Compiled);

    private static readonly Regex GreetingPattern =
        new(@"^\s*(hi|hello|hey|help|what can you do|how do (i|you)|who are you|menu|\?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RankTrigger =
        new(@"\b(top|rank(ed|ing)?|most valuable|highest[- ]value|most money|worth the most|biggest|tie[sd]? up)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ValueCue =
        new(@"\b(value|valuable|worth|money|inventory|stock|expensive)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SummaryTrigger =
        new(@"\b(summary|summari[sz]e|overview|statistics|stats|report|status|total|totals|" +
            @"how many (products|items|suppliers|units)|how is (our|the) inventory|inventory (value|health))\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LowStockTrigger =
        new(@"\b(low[- ]stock|low (on|in) stock|low inventory|out of stock|sold out|stock ?outs?|running low|" +
            @"running out|almost out|reorder\w*|re-order\w*|restock\w*|replenish\w*|shortages?|understocked)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CountQuestion =
        new(@"\b(how many|number of|count of)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OutOfStockOnly =
        new(@"\b(out of stock|sold out|stock ?outs?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LowStockWording =
        new(@"\b(low|reorder\w*|re-order\w*|restock\w*|replenish\w*|running|almost|shortages?|understocked)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SearchTrigger =
        new(@"\b(do we have|have we got|find|search|show|list|looking for|look for|any|available|" +
            @"under|below|less than|cheaper|up to|at most|price|cost)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MaxPricePattern =
        new(@"(?:under|below|less than|cheaper than|up to|at most|no more than|within|max(?:imum)?(?: price)?(?: of)?|<=?)" +
            @"\s*\$?\s*(?<n>\d+(?:,\d{3})*(?:\.\d+)?)\s*(?<k>k\b)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrailingPricePattern =
        new(@"\$\s*(?<n>\d+(?:,\d{3})*(?:\.\d+)?)\s*(?<k>k\b)?\s*(?:or (?:less|under|below|cheaper))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CountPattern =
        new(@"\b(?:top|first)\s+(?<n>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\b" +
            @"|\b(?<n2>\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s+(?:most|highest|top|biggest)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WordPattern =
        new(@"[\p{L}\p{N}][\p{L}\p{N}\-\+\.]*", RegexOptions.Compiled);

    // --- Safety / scope patterns. These are checked before anything else, and they never reach MCP. ---
    // Messages are capped at 500 characters and every gap below is bounded, so matching stays cheap.
    private const RegexOptions SafetyOptions =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline;

    /// <summary>Attempts to override instructions, extract secrets/prompts, role-play, or run commands.</summary>
    private static readonly Regex PromptInjectionPattern =
        new(@"\b(ignore|disregard|forget|override|bypass|skip)\b.{0,40}\b(instructions?|prompts?|rules|guidelines|directives|restrictions|programming|guardrails|safety)\b" +
            @"|\b(system|developer|hidden|initial|original|secret)\s+(prompt|message|instructions?)\b" +
            @"|\b(reveal|show|print|repeat|display|leak|share|tell me|what (is|are))\b.{0,30}\b(your|the)\b.{0,20}\b(prompt|instructions|rules|configuration|config|api[- ]?keys?|secrets?|passwords?|credentials|source code|connection string)\b" +
            @"|\b(api[- ]?keys?|connection string|appsettings|environment variables?|secrets?\.json)\b" +
            @"|\b(you are now|you're now|from now on|pretend (to be|you)|act as|role-?play|jailbreak|developer mode|dan mode|do anything now|no restrictions|without restrictions)\b" +
            @"|\b(call|run|execute|invoke)\s+(the\s+)?(\w+\s+)?(tool|function|command|shell|terminal|script|bash|powershell|cmd)\b" +
            @"|\brm\s+-rf\b|\bsudo\b|\bcurl\s+https?|\bwget\b",
            SafetyOptions);

    /// <summary>SQL statements and the classic injection tricks (tautologies, comments, UNION).</summary>
    private static readonly Regex SqlPattern =
        new(@"\b(insert\s+into|delete\s+from|drop\s+(table|database|view|index)|truncate\s+table|alter\s+table|create\s+(table|database)|update\s+\w+\s+set|union\s+(all\s+)?select|xp_\w+|pragma\s+\w+|attach\s+database|sqlite_master|information_schema)\b" +
            @"|\bselect\s+(\*|count\s*\(|distinct\b)" +
            @"|\bselect\b.{0,100}\bfrom\b.{0,40}\b(products|suppliers|sqlite_master|users|information_schema)\b" +
            @"|['""]\s*(or|and)\s+['""]?\w+['""]?\s*=\s*['""]?\w+" +
            @"|\bor\s+1\s*=\s*1\b" +
            @"|;\s*--|--\s*$|/\*.*?\*/",
            SafetyOptions);

    /// <summary>Topics that are clearly not about inventory.</summary>
    private static readonly Regex OffTopicPattern =
        new(@"\b(weather|forecast|temperature|raining|snowing|jokes?|riddle|poem|poetry|songs?|lyrics|stor(y|ies)|recipes?|horoscope|movies?|celebrity|football|soccer|basketball|baseball|cricket|nba|nfl|bitcoin|crypto(currency)?|ethereum|nasdaq|dow jones|stock market|share price|election|president|prime minister|politic\w*|translate|translation|homework|essay|python|javascript|capital of|meaning of life|how old are you|who (made|created|built) you|are you (a |an )?(robot|human|ai|bot|real)|how are you|what'?s up|good (morning|afternoon|evening|night)|tell me about yourself)\b",
            SafetyOptions);

    /// <summary>The whole message is just an acknowledgement or goodbye.</summary>
    private static readonly Regex ThanksPattern =
        new(@"^\s*(thanks|thank you|thx|ty|cheers|bye|goodbye|see you|ok(ay)?|cool|great|nice|got it|awesome)\b[\s!.?]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, int> NumberWords =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10
        };

    // Words that carry no product meaning; everything else becomes the search keyword.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "for", "to", "in", "on", "at", "by", "with", "from",
        "i", "me", "my", "we", "us", "our", "you", "your", "it", "is", "are", "was", "were", "be",
        "do", "does", "did", "have", "has", "got", "can", "could", "would", "will", "please", "pls",
        "show", "list", "find", "search", "give", "get", "tell", "see", "look", "looking", "need", "want",
        "any", "all", "some", "there", "what", "which", "whats", "that", "this", "these", "those",
        "much", "many", "how", "available", "stock", "stocked", "inventory", "product", "products",
        "item", "items", "price", "prices", "cost", "costs", "priced", "cheaper", "cheap", "than",
        "under", "below", "less", "more", "most", "least", "up", "about", "info", "information",
        "details", "detail", "carry", "sell", "offer", "currently", "right", "now", "theres", "um",
        "like", "should", "today", "tomorrow", "bring", "thanks", "thank",
        "low", "running", "out", "needs", "needed", "reorder", "reordering", "restock", "restocking",
        "replenish", "shortage", "shortages", "soon", "attention", "urgent", "urgently", "sold", "almost",
        "anything", "something", "everything", "else", "also", "still", "already", "yet", "left",
        "update", "updates", "know", "check", "whether", "if", "so", "but", "not", "no", "yes",
        "help", "helps", "assist", "assistance", "support",
        // Words that describe the question, not the product (so they never become a search keyword).
        "low-stock", "lowstock", "low_stock", "their", "level", "levels", "category", "categories",
        "containing", "contain", "contains", "contained", "include", "includes", "including", "included",
        "named", "called", "matching", "match", "matches", "number", "count"
    };

    public static ChatIntent Parse(string? message)
    {
        var text = message?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Intent(ChatIntentKind.Help);
        }

        // 0. Safety first. These are checked before the SKU rule so that something like
        //    "SW-001'; DROP TABLE Products;--" is refused rather than quietly treated as "SW-001".
        //    The whole message is refused; no part of it is acted on.
        if (PromptInjectionPattern.IsMatch(text))
        {
            return Intent(ChatIntentKind.PromptInjection);
        }

        if (SqlPattern.IsMatch(text))
        {
            return Intent(ChatIntentKind.SqlCommand);
        }

        // 1. An explicit SKU such as "SW-001" is the clearest signal there is.
        var sku = SkuPattern.Match(text);
        if (sku.Success)
        {
            return new ChatIntent(
                ChatIntentKind.Lookup,
                GuidedDemoTools.LookupProduct,
                new Dictionary<string, object?> { ["sku"] = sku.Value.ToUpperInvariant() });
        }

        if (GreetingPattern.IsMatch(text))
        {
            return Intent(ChatIntentKind.Help);
        }

        if (ThanksPattern.IsMatch(text))
        {
            return Intent(ChatIntentKind.Thanks);
        }

        // 2. Ranking by inventory value, optionally with "top N".
        if (RankTrigger.IsMatch(text) && ValueCue.IsMatch(text))
        {
            var arguments = new Dictionary<string, object?>();
            var requestedCount = TryGetRankCount(text);
            if (requestedCount is not null)
            {
                arguments["count"] = requestedCount.Value;
            }

            return new ChatIntent(ChatIntentKind.Rank, GuidedDemoTools.RankProductsByValue, arguments);
        }

        // 3. Low-stock / reorder questions: list the products. "Out of stock" alone means quantity 0;
        //    anything mentioning low stock or reordering means "at or below the reorder level".
        if (LowStockTrigger.IsMatch(text))
        {
            var onlyOutOfStock = OutOfStockOnly.IsMatch(text) && !LowStockWording.IsMatch(text);
            var keywords = ExtractKeywords(text);

            // "How many ...?" without a product-type filter is a count question: answer it from the summary.
            // (With a filter, such as "how many low-stock laptops", the list's first line gives the count.)
            if (CountQuestion.IsMatch(text) && keywords.Count == 0)
            {
                return new ChatIntent(
                    ChatIntentKind.LowStockCount,
                    GuidedDemoTools.GetInventorySummary,
                    new Dictionary<string, object?> { ["onlyOutOfStock"] = onlyOutOfStock });
            }

            return new ChatIntent(
                ChatIntentKind.LowStock,
                GuidedDemoTools.GetLowStockProducts,
                new Dictionary<string, object?> { ["onlyOutOfStock"] = onlyOutOfStock },
                keywords.Count > 0 ? string.Join(' ', keywords) : null);
        }

        // 4. Whole-inventory questions.
        if (SummaryTrigger.IsMatch(text))
        {
            return new ChatIntent(ChatIntentKind.Summary, GuidedDemoTools.GetInventorySummary, NoArguments);
        }

        // 5. Clearly non-inventory topics get a polite redirect instead of a pointless product search.
        if (OffTopicPattern.IsMatch(text))
        {
            return Intent(ChatIntentKind.OffTopic);
        }

        // 6. Everything else is treated as a product search if it looks like one.
        return ParseSearch(text);
    }

    private static ChatIntent ParseSearch(string text)
    {
        var arguments = new Dictionary<string, object?>();
        var remaining = text;

        var price = MatchMaxPrice(remaining, out var amount);
        if (price is not null)
        {
            arguments["maxPrice"] = amount;
            remaining = remaining.Remove(price.Index, price.Length);
        }

        var keywords = ExtractKeywords(remaining);
        var searchText = string.Join(' ', keywords);

        // The search tool accepts at most 100 characters. Text longer than that is not a product
        // search, so ask the user to rephrase rather than show them the tool's validation error.
        if (searchText.Length > MaxSearchTextLength)
        {
            return Intent(ChatIntentKind.Unknown);
        }

        if (keywords.Count > 0)
        {
            arguments["searchText"] = searchText;
        }

        // Without a search cue or any usable keyword/price there is nothing to search for.
        // With no cue, at most two keywords ("monitors", "usb-c hub", "is the gaming laptop in stock")
        // count as a product query; more leftover words than that is more likely small talk.
        // (Clearly off-topic subjects such as the weather were already turned away above.)
        var looksLikeSearch = SearchTrigger.IsMatch(text) || keywords.Count is > 0 and <= 2;
        if (arguments.Count == 0 || !looksLikeSearch)
        {
            return Intent(ChatIntentKind.Unknown);
        }

        return new ChatIntent(ChatIntentKind.Search, GuidedDemoTools.SearchInventory, arguments);
    }

    /// <summary>Product-type words left in the text once filler and stop words are removed.</summary>
    private static List<string> ExtractKeywords(string text)
    {
        // Drop apostrophes first so "what's" becomes "whats" (a stop word) instead of "what" + "s".
        var cleaned = text.Replace("'", string.Empty).Replace("’", string.Empty);

        return WordPattern.Matches(cleaned)
            .Select(m => m.Value.TrimEnd('.', '-').ToLowerInvariant())
            .Where(w => w.Length > 1 && !StopWords.Contains(w) && !decimal.TryParse(w, out _))
            .Select(Singularize)
            .ToList();
    }

    // ---- What did the user actually ask for? ----------------------------------------------
    // These are public so AI mode can check a model's tool arguments against the user's own words.

    /// <summary>Every SKU written in the message (for example "SW-001"), upper-cased, without duplicates.</summary>
    public static IReadOnlyList<string> ExtractSkus(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : SkuPattern.Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    /// <summary>The maximum price the user stated ("under $200", "$60 or less"), in dollars, or null.</summary>
    public static decimal? TryGetMaxPrice(string? text) =>
        !string.IsNullOrEmpty(text) && MatchMaxPrice(text, out var amount) is not null ? amount : null;

    /// <summary>The "top N" the user stated ("top 3", "five most valuable"), or null.</summary>
    public static int? TryGetRankCount(string? text)
    {
        var count = string.IsNullOrEmpty(text) ? Match.Empty : CountPattern.Match(text);
        if (!count.Success)
        {
            return null;
        }

        var token = count.Groups["n"].Success ? count.Groups["n"].Value : count.Groups["n2"].Value;
        return ParseCount(token);
    }

    private static Match? MatchMaxPrice(string text, out decimal amount)
    {
        amount = 0m;

        var price = MaxPricePattern.Match(text);
        if (!price.Success)
        {
            price = TrailingPricePattern.Match(text);
        }

        if (!price.Success)
        {
            return null;
        }

        amount = decimal.Parse(
            price.Groups["n"].Value.Replace(",", string.Empty),
            CultureInfo.InvariantCulture);
        if (price.Groups["k"].Success)
        {
            amount *= 1000m;
        }

        return price;
    }

    private static int? ParseCount(string token)
    {
        if (NumberWords.TryGetValue(token, out var word))
        {
            return word;
        }

        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    /// <summary>
    /// Naive plural handling so "monitors" matches "Studio Monitor 27".
    /// Words like "accessories" and "mice" are left alone because they match category names as-is.
    /// </summary>
    private static string Singularize(string word)
    {
        if (word.Length <= 3 || !word.EndsWith('s') || word.EndsWith("ss") || word.EndsWith("ies"))
        {
            return word;
        }

        if (word.EndsWith("ches") || word.EndsWith("shes") || word.EndsWith("xes") || word.EndsWith("sses"))
        {
            return word[..^2];
        }

        return word[..^1];
    }

    private static ChatIntent Intent(ChatIntentKind kind) => new(kind, null, NoArguments);
}
