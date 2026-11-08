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
    private readonly IChatService _chatService;
    private readonly IAntiforgery _antiforgery;
    private readonly AntiforgeryOptions _antiforgeryOptions;

    public ChatController(
        IChatService chatService,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        _chatService = chatService;
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

    /// <summary>Answers one inventory question.</summary>
    /// <response code="200">The chatbot's answer.</response>
    /// <response code="400">
    /// The anti-forgery token was missing or invalid (empty body), or the message was missing,
    /// blank or too long (standard problem-details body).
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
        var reply = await _chatService.ReplyAsync(request.Message!.Trim(), cancellationToken);

        return Ok(new ChatResponse(reply.Reply, reply.ToolsUsed, _chatService.ModeName));
    }
}
