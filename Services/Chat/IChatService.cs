namespace StockWiseMcp.Services.Chat;

/// <summary>
/// A chatbot backend. Guided Demo mode is one implementation; an LLM-backed mode
/// can implement the same interface later without changing callers.
/// </summary>
public interface IChatService
{
    /// <summary>Short name of this chat mode, for example "Guided Demo".</summary>
    string ModeName { get; }

    /// <summary>Answers one user message in plain, human-readable text.</summary>
    Task<ChatReply> ReplyAsync(string message, CancellationToken cancellationToken = default);
}

/// <summary>A chatbot answer plus the MCP tools that were used to produce it.</summary>
/// <param name="Reply">Natural-language answer, ready to show to the user.</param>
/// <param name="ToolsUsed">Names of the MCP tools called (empty if none).</param>
public sealed record ChatReply(string Reply, IReadOnlyList<string> ToolsUsed);
