namespace StockWiseMcp.Services.Chat;

/// <summary>The chat modes a client can ask for in the optional "mode" field of POST /api/chat.</summary>
public static class ChatModes
{
    public const string Guided = "guided";

    /// <summary>The first AI provider that is configured (Gemini, then OpenAI).</summary>
    public const string Ai = "ai";

    public const string Gemini = "gemini";
    public const string OpenAi = "openai";

    /// <summary>
    /// Parses a client-supplied mode. A missing/blank mode is valid and yields null (use the server default).
    /// Returns false for anything that is not a known mode.
    /// </summary>
    public static bool TryParse(string? value, out string? mode)
    {
        mode = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is Guided or Ai or Gemini or OpenAi)
        {
            mode = normalized;
            return true;
        }

        return false;
    }
}
