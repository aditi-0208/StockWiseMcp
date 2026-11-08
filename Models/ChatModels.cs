using System.ComponentModel.DataAnnotations;

namespace StockWiseMcp.Models;

/// <summary>Body of POST /api/chat.</summary>
public sealed class ChatRequest
{
    public const int MaxMessageLength = 500;

    /// <summary>The user's question. Required, not blank, at most 500 characters.</summary>
    [Required(ErrorMessage = "Please enter a message.")]
    [StringLength(MaxMessageLength, ErrorMessage = "Messages can be at most 500 characters long.")]
    public string? Message { get; set; }
}

/// <summary>Response of GET /api/chat/token.</summary>
/// <param name="HeaderName">Name of the HTTP header the token must be sent in.</param>
/// <param name="Token">Request token to send in that header with every POST /api/chat.</param>
public sealed record AntiforgeryTokenResponse(string HeaderName, string Token);

/// <summary>Successful response of POST /api/chat.</summary>
/// <param name="Reply">Plain-text answer. Contains line breaks ("\n") and "- " bullet lines.</param>
/// <param name="ToolsUsed">MCP tools called to produce the answer (may be empty).</param>
/// <param name="Mode">Chat mode that produced the answer, for example "Guided Demo".</param>
public sealed record ChatResponse(string Reply, IReadOnlyList<string> ToolsUsed, string Mode);
