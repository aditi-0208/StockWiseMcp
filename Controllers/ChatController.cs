using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StockWiseMcp.Models;
using StockWiseMcp.Services.Chat;

namespace StockWiseMcp.Controllers;

/// <summary>JSON chat API used by the StockWise frontend.</summary>
[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    private readonly ChatOrchestrator _chat;
    private readonly IAntiforgery _antiforgery;
    private readonly AntiforgeryOptions _antiforgeryOptions;

    public ChatController(
        ChatOrchestrator chat,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        _chat = chat;
        _antiforgery = antiforgery;
        _antiforgeryOptions = antiforgeryOptions.Value;
    }

    /// <summary>
    /// Issues an anti-forgery (CSRF) token. This sets the antiforgery cookie on the response and
    /// returns the matching request token, which the frontend must send back in the header named
    /// <see cref="AntiforgeryTokenResponse.HeaderName"/> on every POST /api/chat.
    /// </summary>
    [HttpGet("token")]
    [Produces("application/json")]
    [ProducesResponseType<AntiforgeryTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<AntiforgeryTokenResponse> GetToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        return Ok(new AntiforgeryTokenResponse(
            _antiforgeryOptions.HeaderName ?? "X-CSRF-TOKEN",
            tokens.RequestToken ?? string.Empty));
    }

    /// <summary>Lists the chat modes and whether each can be used right now (never reveals any secret).</summary>
    [HttpGet("modes")]
    [Produces("application/json")]
    [ProducesResponseType<ChatModesResponse>(StatusCodes.Status200OK)]
    public ActionResult<ChatModesResponse> GetModes() => Ok(_chat.Describe());

    /// <summary>Answers one inventory question.</summary>
    /// <response code="200">The chatbot's answer.</response>
    /// <response code="400">
    /// The anti-forgery token was missing or invalid (empty body), or the message was missing,
    /// blank or too long, or the mode was unknown (standard problem-details body).
    /// </response>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ChatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ChatResponse>> Post(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        // The antiforgery filter has already run, so the request carries a valid token.
        // [ApiController] then rejects a missing, blank or over-long message with a 400 before
        // this point, so request.Message is a non-empty string here.
        // The attribute counts surrounding spaces, so " a " would pass it; check the trimmed length too.
        var message = request.Message!.Trim();
        if (message.Length < ChatRequest.MinMessageLength)
        {
            ModelState.AddModelError(nameof(request.Message), ChatRequest.LengthErrorMessage);
            return ValidationProblem(ModelState);
        }

        if (!ChatModes.TryParse(request.Mode, out var mode))
        {
            ModelState.AddModelError(nameof(request.Mode), "Mode must be 'guided' or 'ai'.");
            return ValidationProblem(ModelState);
        }

        var result = await _chat.ReplyAsync(message, mode, cancellationToken);

        return Ok(new ChatResponse(result.Reply.Reply, result.Reply.ToolsUsed, result.Mode, result.Notice));
    }
}
