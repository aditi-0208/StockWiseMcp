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
    private readonly AiChatService? _ai;
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
        AiChatService? ai = null)
    {
        _guided = guided;
        _ai = ai;
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
            new ChatModeInfo(ChatModes.Ai, "AI Assistant", Available: AiAvailable)
        ]);

    /// <param name="requestedMode">"guided", "ai", or null for the server default.</param>
    public async Task<OrchestratedReply> ReplyAsync(
        string message,
        string? requestedMode,
        CancellationToken cancellationToken = default)
    {
        var mode = requestedMode ?? EffectiveDefault();

        if (mode != ChatModes.Ai)
        {
            return await GuidedAsync(message, notice: null, cancellationToken);
        }

        if (!AiAvailable)
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
            var reply = await _ai!.ReplyAsync(message, cancellationToken);
            return new OrchestratedReply(reply, _ai.ModeName, Notice: null);
        }
        catch (AiUnavailableException ex)
        {
            _logger.LogWarning("AI mode could not answer ({Reason}); falling back to Guided Demo.", ex.Message);
            return await GuidedAsync(
                message,
                "The AI assistant couldn't answer just now, so Guided Demo mode answered instead.",
                cancellationToken);
        }
    }

    private bool AiAvailable => _settings.Enabled && _ai is not null;

    private string EffectiveDefault() =>
        ChatModes.TryParse(_configuration[DefaultModeSettingName], out var configured)
        && configured == ChatModes.Ai
        && AiAvailable
            ? ChatModes.Ai
            : ChatModes.Guided;

    private async Task<OrchestratedReply> GuidedAsync(string message, string? notice, CancellationToken cancellationToken)
    {
        var reply = await _guided.ReplyAsync(message, cancellationToken);
        return new OrchestratedReply(reply, _guided.ModeName, notice);
    }

    private string ClientKey() =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
