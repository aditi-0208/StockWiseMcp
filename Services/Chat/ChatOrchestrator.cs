using Microsoft.Extensions.Logging;
using StockWiseMcp.Models;

namespace StockWiseMcp.Services.Chat;

/// <summary>The answer to one chat message, which mode produced it, and an optional explanation.</summary>
/// <param name="Reply">The answer and the MCP tools it used.</param>
/// <param name="Mode">Display name of the mode that actually produced the answer.</param>
/// <param name="Notice">Set when the requested mode could not be used and Guided Demo answered instead.</param>
public sealed record OrchestratedReply(ChatReply Reply, string Mode, string? Notice);

/// <summary>
/// Chooses between Guided Demo and AI mode for each message. Guided Demo is always available and is the
/// default; AI mode is used only when it is configured, requested, and within its rate limit. If AI mode
/// cannot answer, the message is answered by Guided Demo so the chat never breaks.
/// </summary>
public sealed class ChatOrchestrator
{
    public const string DefaultModeSettingName = "StockWise:Chat:DefaultMode";

    private readonly GuidedDemoChatService _guided;
    private readonly AiChatServiceFactory? _aiFactory;
    private readonly AiChatSettings _settings;
    private readonly AiRequestLimiter _limiter;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatOrchestrator> _logger;

    public ChatOrchestrator(
        GuidedDemoChatService guided,
        AiChatSettings settings,
        AiRequestLimiter limiter,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<ChatOrchestrator> logger,
        AiChatServiceFactory? aiFactory = null)
    {
        _guided = guided;
        _aiFactory = aiFactory;
        _settings = settings;
        _limiter = limiter;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Which modes exist, whether each is usable right now, and the default.</summary>
    public ChatModesResponse Describe() => new(
        EffectiveDefault(),
        [
            new ChatModeInfo(ChatModes.Guided, _guided.ModeName, Available: true),
            new ChatModeInfo(ChatModes.Gemini, "AI Assistant (Gemini)", Available: FindProvider(ChatModes.Gemini) is not null),
            new ChatModeInfo(ChatModes.OpenAi, "AI Assistant (OpenAI)", Available: FindProvider(ChatModes.OpenAi) is not null)
        ]);

    /// <param name="requestedMode">"guided", "gemini", "openai", "ai" (first configured), or null for the server default.</param>
    public async Task<OrchestratedReply> ReplyAsync(
        string message,
        string? requestedMode,
        CancellationToken cancellationToken = default)
    {
        var mode = requestedMode ?? EffectiveDefault();

        if (mode == ChatModes.Guided)
        {
            return await GuidedAsync(message, notice: null, cancellationToken);
        }

        var provider = FindProvider(mode);
        if (provider is null || _aiFactory is null)
        {
            return await GuidedAsync(
                message,
                "AI mode isn't set up on this server, so Guided Demo mode answered instead.",
                cancellationToken);
        }

        // Messages answered with a fixed reply never reach the model, so they don't use up a permit.
        if (AiChatService.NeedsModel(message) && !_limiter.TryAcquire(ClientKey()))
        {
            _logger.LogInformation("AI mode rate limit reached; answering in Guided Demo mode.");
            return await GuidedAsync(
                message,
                "AI mode is limited to a few questions per minute, so Guided Demo mode answered instead.",
                cancellationToken);
        }

        try
        {
            var ai = _aiFactory.Create(provider);
            var reply = await ai.ReplyAsync(message, cancellationToken);
            return new OrchestratedReply(reply, ai.ModeName, Notice: null);
        }
        catch (AiUnavailableException ex)
        {
            _logger.LogWarning("AI mode could not answer ({Category}: {Reason}); falling back to Guided Demo.",
                ex.Category, ex.Message);
            return await GuidedAsync(message, NoticeFor(ex.Category), cancellationToken);
        }
    }

    /// <summary>
    /// The message shown when AI mode fell back to Guided Demo. It explains the cause when that is safe,
    /// but never reveals authentication or configuration problems (those stay the general wording).
    /// </summary>
    private static string NoticeFor(AiUnavailableReason reason) => reason switch
    {
        AiUnavailableReason.Busy =>
            "The AI service is busy right now, so Guided Demo mode answered instead. Please try again in a moment.",
        AiUnavailableReason.Timeout =>
            "The AI assistant took too long to respond, so Guided Demo mode answered instead.",
        AiUnavailableReason.NotGrounded =>
            "The AI assistant couldn't ground its answer in the inventory data, so Guided Demo mode answered instead.",
        _ =>
            "The AI assistant couldn't answer just now, so Guided Demo mode answered instead.",
    };

    private AiProvider? FindProvider(string? mode) => mode == ChatModes.Ai
        ? _settings.Providers.FirstOrDefault()
        : _settings.Providers.FirstOrDefault(p => p.Id == mode);

    private string EffectiveDefault() =>
        ChatModes.TryParse(_configuration[DefaultModeSettingName], out var configured)
            ? FindProvider(configured)?.Id ?? ChatModes.Guided
            : ChatModes.Guided;

    private async Task<OrchestratedReply> GuidedAsync(string message, string? notice, CancellationToken cancellationToken)
    {
        var reply = await _guided.ReplyAsync(message, cancellationToken);
        return new OrchestratedReply(reply, _guided.ModeName, notice);
    }

    private string ClientKey() =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
