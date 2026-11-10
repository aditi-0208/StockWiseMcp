using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StockWiseMcp.Mcp;

namespace StockWiseMcp.Services.Chat;

/// <summary>
/// Guided Demo chatbot: understands basic inventory questions with simple rules (no API key,
/// no LLM) and answers them by calling the StockWise MCP tools over the /mcp endpoint.
/// It never touches the database directly. For every message it connects to the MCP server,
/// discovers the available tools at runtime, calls the one it needs, and turns the JSON result
/// into a natural-language answer.
/// </summary>
public sealed class GuidedDemoChatService : IChatService
{
    private readonly Uri _mcpEndpoint;
    private readonly ILogger<GuidedDemoChatService> _logger;
    private readonly string? _mcpAccessKey;

    /// <param name="mcpEndpoint">Absolute URL of the MCP server, for example http://localhost:5247/mcp.</param>
    /// <param name="mcpAccessKey">The key /mcp requires, or null when it is open (Development).</param>
    public GuidedDemoChatService(Uri mcpEndpoint, ILogger<GuidedDemoChatService> logger, string? mcpAccessKey = null)
    {
        ArgumentNullException.ThrowIfNull(mcpEndpoint);
        ArgumentNullException.ThrowIfNull(logger);

        if (!mcpEndpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The MCP endpoint must be an absolute URL.", nameof(mcpEndpoint));
        }

        _mcpEndpoint = mcpEndpoint;
        _logger = logger;
        _mcpAccessKey = mcpAccessKey;
    }

    public string ModeName => "Guided Demo";

    public async Task<ChatReply> ReplyAsync(string message, CancellationToken cancellationToken = default)
    {
        var intent = GuidedDemoIntentParser.Parse(message);

        // Anything that needs no inventory data (off-topic, SQL, injection attempts, thanks, gibberish)
        // is answered locally. No MCP connection is opened. Only the kind is logged, never the text.
        if (intent.ToolName is null && intent.Kind != ChatIntentKind.Help)
        {
            _logger.LogInformation("Guided Demo answered a '{Kind}' message without calling MCP.", intent.Kind);
            return new ChatReply(LocalReply(intent.Kind), []);
        }

        try
        {
            // The MCP server is stateless, so a short-lived client per message is simple and
            // always sees the server's current tool list.
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(McpAccessKey.WithAccessKey(
                    new HttpClientTransportOptions
                    {
                        Endpoint = _mcpEndpoint,
                        Name = "StockWise Guided Demo"
                    },
                    _mcpAccessKey)),
                cancellationToken: cancellationToken);

            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);

            if (intent.ToolName is null)
            {
                // Only "help" gets here: it lists whatever tools the server reports right now.
                return new ChatReply(
                    InventoryReplyFormatter.FormatHelp(
                        tools.Select(t => (t.Name, t.Description ?? string.Empty))),
                    []);
            }

            var tool = tools.FirstOrDefault(t => t.Name == intent.ToolName);
            if (tool is null)
            {
                _logger.LogWarning("MCP server does not offer the '{Tool}' tool.", intent.ToolName);
                return new ChatReply(
                    $"I can't answer that right now because the '{intent.ToolName}' tool isn't available on the server.",
                    []);
            }

            // Only send arguments the server's schema actually declares.
            var arguments = intent.Arguments
                .Where(a => DeclaresParameter(tool, a.Key))
                .ToDictionary(a => a.Key, a => a.Value);

            var result = await client.CallToolAsync(tool.Name, arguments, cancellationToken: cancellationToken);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

            if (result.IsError == true)
            {
                return new ChatReply(ToolErrorMessage(text), [tool.Name]);
            }

            using var json = JsonDocument.Parse(text);
            return new ChatReply(Format(intent, json.RootElement), [tool.Name]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or JsonException or IOException)
        {
            _logger.LogError(ex, "Guided Demo could not complete the request via {Endpoint}.", _mcpEndpoint);
            return new ChatReply(
                "Sorry, I couldn't reach the inventory tools just now. Please try again in a moment.",
                []);
        }
    }

    /// <summary>Fixed replies for messages that need no inventory data. Shared with AI mode.</summary>
    internal static string LocalReply(ChatIntentKind kind) => kind switch
    {
        ChatIntentKind.OffTopic => InventoryReplyFormatter.FormatOffTopic(),
        ChatIntentKind.Thanks => InventoryReplyFormatter.FormatThanks(),
        ChatIntentKind.SqlCommand => InventoryReplyFormatter.FormatSqlCommand(),
        ChatIntentKind.PromptInjection => InventoryReplyFormatter.FormatPromptInjection(),
        _ => InventoryReplyFormatter.FormatUnknown()
    };

    private static string Format(ChatIntent intent, JsonElement data) => intent.Kind switch
    {
        ChatIntentKind.Lookup => InventoryReplyFormatter.FormatProduct(data),
        ChatIntentKind.Search => InventoryReplyFormatter.FormatSearch(
            data,
            intent.Arguments.TryGetValue("searchText", out var text) ? text as string : null,
            intent.Arguments.TryGetValue("maxPrice", out var price) ? price as decimal? : null),
        ChatIntentKind.Summary => InventoryReplyFormatter.FormatSummary(data),
        ChatIntentKind.LowStock => InventoryReplyFormatter.FormatLowStock(
            data,
            intent.Arguments.TryGetValue("onlyOutOfStock", out var outOnly) && outOnly is true,
            intent.Keyword),
        ChatIntentKind.LowStockCount => InventoryReplyFormatter.FormatLowStockCount(
            data,
            intent.Arguments.TryGetValue("onlyOutOfStock", out var outOnlyCount) && outOnlyCount is true),
        ChatIntentKind.Rank => InventoryReplyFormatter.FormatRanking(
            data,
            highestFirst: intent.Arguments.GetValueOrDefault("highestFirst") is not false),
        _ => InventoryReplyFormatter.FormatUnknown()
    };

    private static bool DeclaresParameter(McpClientTool tool, string name) =>
        tool.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(name, out _);

    /// <summary>
    /// Tool errors look like "An error occurred invoking 'x': reason". Show only the reason.
    /// </summary>
    internal static string ToolErrorMessage(string toolText)
    {
        var separator = toolText.IndexOf("': ", StringComparison.Ordinal);
        var reason = separator >= 0 ? toolText[(separator + 3)..] : toolText;

        // Tool errors are written for AI models and may name other tools; users should see plain advice.
        reason = reason.Replace("Try search_inventory to find it by name.", "Try searching by product name instead.", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(reason)
            ? "Sorry, something went wrong while looking that up."
            : $"I couldn't do that: {reason.Trim()}";
    }
}
