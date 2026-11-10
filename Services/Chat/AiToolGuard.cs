using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace StockWiseMcp.Services.Chat;

/// <summary>One MCP tool call made by the model during a turn, with the data it actually returned.</summary>
/// <param name="Name">MCP tool name.</param>
/// <param name="Arguments">The arguments the model passed (already checked against the user's message).</param>
/// <param name="Data">The tool's JSON result, or null if the call failed.</param>
/// <param name="Error">The tool's error message, or null if the call succeeded.</param>
internal sealed record ToolCallRecord(
    string Name,
    IReadOnlyDictionary<string, object?> Arguments,
    JsonDocument? Data,
    string? Error);

/// <summary>
/// Everything that happens in one AI turn. It does two jobs:
/// <list type="number">
/// <item>Checks each tool call BEFORE it runs against what the user actually asked for (the SKU they wrote,
/// the price limit and "top N" they stated). A mismatching call is refused and the model is told why.</item>
/// <item>Records the data each tool really returned, so the answer shown to the user can be built from
/// those results instead of from whatever numbers the model writes.</item>
/// </list>
/// </summary>
internal sealed class AiTurn : IDisposable
{
    private readonly object _gate = new();
    private readonly List<ToolCallRecord> _records = [];
    private readonly HashSet<string> _userSkus;
    private readonly HashSet<string> _seenSkus = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vocabulary = new(StringComparer.OrdinalIgnoreCase);
    private readonly decimal? _userMaxPrice;
    private readonly int? _userRankCount;
    private readonly bool _userWantsLowestValue;

    public AiTurn(string userMessage)
    {
        _userSkus = new HashSet<string>(GuidedDemoIntentParser.ExtractSkus(userMessage), StringComparer.OrdinalIgnoreCase);
        _userMaxPrice = GuidedDemoIntentParser.TryGetMaxPrice(userMessage);
        _userRankCount = GuidedDemoIntentParser.TryGetRankCount(userMessage);
        _userWantsLowestValue = GuidedDemoIntentParser.WantsLowestValue(userMessage);
        AddWords(userMessage);
    }

    /// <summary>How many tool calls were refused because they did not match the user's request.</summary>
    public int RejectedCalls { get; private set; }

    public IReadOnlyList<ToolCallRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToList();
            }
        }
    }

    /// <summary>Wraps an MCP tool so every call is validated and recorded.</summary>
    public AITool Wrap(AIFunction tool) => new GuardedTool(tool, this);

    /// <summary>True if the word appears in the user's message or in any data a tool returned this turn.</summary>
    public bool KnowsWord(string word)
    {
        lock (_gate)
        {
            return _vocabulary.Contains(word);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var record in _records)
            {
                record.Data?.Dispose();
            }
        }
    }

    // ---- validation -------------------------------------------------------------------------

    private void Validate(string tool, IReadOnlyDictionary<string, object?> args)
    {
        switch (tool)
        {
            case GuidedDemoTools.LookupProduct:
            {
                var sku = (args.GetValueOrDefault("sku") as string)?.Trim().ToUpperInvariant();
                bool allowed;
                lock (_gate)
                {
                    allowed = !string.IsNullOrEmpty(sku) && (_userSkus.Contains(sku) || _seenSkus.Contains(sku));
                }

                if (!allowed)
                {
                    Reject($"The user did not ask about SKU '{sku}'. Only look up a SKU the user wrote, " +
                           "or one returned by an earlier tool call in this turn.");
                }

                break;
            }

            case GuidedDemoTools.SearchInventory:
            {
                var maxPrice = args.GetValueOrDefault("maxPrice") as decimal?;
                if (maxPrice != _userMaxPrice)
                {
                    Reject(_userMaxPrice is null
                        ? "The user did not state a price limit, so do not pass maxPrice."
                        : $"The user's maximum price is {_userMaxPrice.Value.ToString("0.##", CultureInfo.InvariantCulture)} dollars; " +
                          "pass exactly that as maxPrice.");
                }

                break;
            }

            case GuidedDemoTools.RankProductsByValue:
            {
                var count = args.GetValueOrDefault("count") as decimal?;
                if (_userRankCount is not null && count != _userRankCount)
                {
                    Reject($"The user asked for {(_userWantsLowestValue ? "the lowest" : "the top")} {_userRankCount}; " +
                           "pass exactly that as count.");
                }

                // The direction must match what the user asked for, or the list would be labelled wrongly.
                var highestFirst = args.GetValueOrDefault("highestFirst") is not false;
                if (_userWantsLowestValue && highestFirst)
                {
                    Reject("The user asked for the LOWEST-value products; pass highestFirst=false.");
                }

                break;
            }
        }
    }

    private void Reject(string reasonForModel)
    {
        lock (_gate)
        {
            RejectedCalls++;
        }

        throw new InvalidOperationException(reasonForModel);
    }

    // ---- recording --------------------------------------------------------------------------

    private void RecordResult(string tool, IReadOnlyDictionary<string, object?> args, object? result)
    {
        var (data, error) = McpToolResult.Parse(result);

        lock (_gate)
        {
            _records.Add(new ToolCallRecord(tool, args, data, error));

            if (data is not null)
            {
                Harvest(data.RootElement);
            }
        }
    }

    private void RecordError(string tool, IReadOnlyDictionary<string, object?> args, string message)
    {
        lock (_gate)
        {
            _records.Add(new ToolCallRecord(tool, args, null, message));
        }
    }

    /// <summary>Remembers the SKUs and words that tool data contains (caller holds the lock).</summary>
    private void Harvest(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("sku", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        _seenSkus.Add(property.Value.GetString()!);
                    }

                    Harvest(property.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Harvest(item);
                }

                break;

            case JsonValueKind.String:
                AddWords(element.GetString());
                break;
        }
    }

    private void AddWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match word in Regex.Matches(text, @"[\p{L}][\p{L}'’\-]*"))
        {
            _vocabulary.Add(word.Value);
        }
    }

    // ---- the wrapper ------------------------------------------------------------------------

    private sealed class GuardedTool(AIFunction inner, AiTurn turn) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var args = Normalize(arguments);

            // Refuses the call (and tells the model why) if it doesn't match what the user asked for.
            turn.Validate(Name, args);

            try
            {
                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                turn.RecordResult(Name, args, result);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                turn.RecordError(Name, args, ex.Message);
                throw;
            }
        }
    }

    /// <summary>Turns the model's JSON arguments into plain strings, decimals and booleans.</summary>
    private static Dictionary<string, object?> Normalize(AIFunctionArguments arguments)
    {
        var normalized = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in arguments)
        {
            normalized[key] = value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetDecimal(out var d) ? d : null,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                JsonElement => null,
                int i => (decimal)i,
                long l => (decimal)l,
                double d => (decimal)d,
                float f => (decimal)f,
                _ => value
            };
        }

        return normalized;
    }
}

/// <summary>Reads the JSON that an MCP tool returned, however the client library wrapped it.</summary>
internal static class McpToolResult
{
    /// <returns>Either the tool's JSON data, or an error message (when the tool reported a failure).</returns>
    public static (JsonDocument? Data, string? Error) Parse(object? result)
    {
        JsonElement element;
        try
        {
            element = result is JsonElement json ? json : JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions);
        }
        catch (NotSupportedException)
        {
            return (null, "The tool result could not be read.");
        }

        var isError = TryGetProperty(element, "isError", out var flag) && flag.ValueKind == JsonValueKind.True;

        var text = ExtractText(element);
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "The tool returned no data.");
        }

        if (isError)
        {
            return (null, text);
        }

        try
        {
            return (JsonDocument.Parse(text), null);
        }
        catch (JsonException)
        {
            // Our tools return JSON on success, so plain text here is an error message.
            return (null, text);
        }
    }

    /// <summary>Case-insensitive property lookup (client libraries differ in how they name JSON properties).</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? ExtractText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Array:
                return string.Concat(element.EnumerateArray().Select(ExtractText));

            case JsonValueKind.Object:
                if (TryGetProperty(element, "text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }

                if (TryGetProperty(element, "content", out var content))
                {
                    return ExtractText(content);
                }

                return element.GetRawText();

            default:
                return null;
        }
    }
}

/// <summary>
/// Decides whether the model's own wording may be shown. It is only ever a short, figure-free lead-in:
/// all numbers and product facts come from the tool data. Anything that could smuggle in a made-up fact
/// (digits, dollar amounts, markup, names that appear nowhere in the data) causes the wording to be dropped.
/// </summary>
internal static partial class LeadInGuard
{
    private const int MaxLength = 280;

    private static readonly HashSet<string> AlwaysKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "I'm", "I've", "StockWise", "SKU", "SKUs", "AI", "USD"
    };

    [GeneratedRegex(@"[\p{L}][\p{L}'’\-]*")]
    private static partial Regex WordRegex();

    /// <returns>The wording to show, or null if it must not be shown.</returns>
    public static string? Accept(string? text, AiTurn turn)
    {
        var prose = text?.Trim();
        if (string.IsNullOrEmpty(prose) || prose.Length > MaxLength)
        {
            return null;
        }

        // No figures of any kind, no markup or tables, a single paragraph.
        if (prose.Any(c => char.IsDigit(c) || c is '$' or '%' or '<' or '>' or '`' or '|' or '{' or '}' or '\n' or '\r'))
        {
            return null;
        }

        // Any capitalized word that is not the start of a sentence must be known: from the user's own
        // message or from the data the tools returned. This catches an invented supplier or product name.
        foreach (Match word in WordRegex().Matches(prose))
        {
            if (!char.IsUpper(word.Value[0]) || AlwaysKnown.Contains(word.Value) || StartsSentence(prose, word.Index))
            {
                continue;
            }

            if (!turn.KnowsWord(word.Value))
            {
                return null;
            }
        }

        return prose;
    }

    private static bool StartsSentence(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return text[i] is '.' or '!' or '?' or ':';
        }

        return true; // first word
    }
}
