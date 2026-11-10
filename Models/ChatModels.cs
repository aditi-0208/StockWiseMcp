using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace StockWiseMcp.Models;

/// <summary>Body of POST /api/chat.</summary>
public sealed class ChatRequest
{
    public const int MinMessageLength = 2;
    public const int MaxMessageLength = 500;
    public const string LengthErrorMessage = "Messages must be between 2 and 500 characters long.";

    /// <summary>The user's question. Required, not blank, 2 to 500 characters (checked again after trimming).</summary>
    [Required(ErrorMessage = "Please enter a message.")]
    [StringLength(MaxMessageLength, MinimumLength = MinMessageLength, ErrorMessage = LengthErrorMessage)]
    public string? Message { get; set; }

    /// <summary>
    /// Optional. "guided" (Guided Demo, the default), "gemini", "openai", or "ai" (the first configured AI provider).
    /// If an AI mode is requested but unavailable, Guided Demo answers and the response explains why in "notice".
    /// </summary>
    public string? Mode { get; set; }
}

/// <summary>One chat mode as listed by GET /api/chat/modes.</summary>
/// <param name="Id">Value to send in the request's "mode" field.</param>
/// <param name="Name">Display name.</param>
/// <param name="Available">False if the mode is not configured on this server.</param>
public sealed record ChatModeInfo(string Id, string Name, bool Available);

/// <summary>Response of GET /api/chat/modes.</summary>
/// <param name="Default">Mode used when a request does not specify one.</param>
/// <param name="Modes">All modes and whether each can be used.</param>
public sealed record ChatModesResponse(string Default, IReadOnlyList<ChatModeInfo> Modes);

/// <summary>Response of GET /api/chat/token.</summary>
/// <param name="HeaderName">Name of the HTTP header the token must be sent in.</param>
/// <param name="Token">Request token to send in that header with every POST /api/chat.</param>
public sealed record AntiforgeryTokenResponse(string HeaderName, string Token);

/// <summary>Successful response of POST /api/chat.</summary>
/// <param name="Reply">Plain-text answer. Contains line breaks ("\n") and "- " bullet lines.</param>
/// <param name="ToolsUsed">MCP tools called to produce the answer (may be empty).</param>
/// <param name="Mode">Chat mode that produced the answer, for example "Guided Demo" or "AI Assistant".</param>
/// <param name="Notice">Only present when the requested mode could not be used and Guided Demo answered instead.</param>
public sealed record ChatResponse(
    string Reply,
    IReadOnlyList<string> ToolsUsed,
    string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notice = null);
