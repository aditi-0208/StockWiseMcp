namespace StockWiseMcp.Mcp;

/// <summary>
/// Rejects requests to /mcp that do not carry the access key. Everything else (the website and
/// /api/chat) passes straight through. Failures get an empty 401 so nothing is revealed about the key.
/// </summary>
public sealed class McpAccessKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly McpAccessKey _key;
    private readonly ILogger<McpAccessKeyMiddleware> _logger;

    public McpAccessKeyMiddleware(RequestDelegate next, McpAccessKey key, ILogger<McpAccessKeyMiddleware> logger)
    {
        _next = next;
        _key = key;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // StartsWithSegments matches "/mcp" and "/mcp/..." but not a look-alike such as "/mcpx".
        if (_key.IsEnforced
            && context.Request.Path.StartsWithSegments(McpAccessKeyExtensions.McpPath)
            && !HasValidKey(context.Request))
        {
            // Never log the presented value, only that a request was refused.
            _logger.LogWarning("Refused a {Method} request to /mcp without a valid access key.", context.Request.Method);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        await _next(context);
    }

    private bool HasValidKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue(McpAccessKey.HeaderName, out var header)
            && header.Any(value => _key.IsValid(value?.Trim())))
        {
            return true;
        }

        // "Authorization: Bearer <key>" is what many MCP clients and tools support out of the box.
        var authorization = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";

        return authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
               && _key.IsValid(authorization[scheme.Length..].Trim());
    }
}

public static class McpAccessKeyExtensions
{
    /// <summary>The path the MCP server is mapped at.</summary>
    public const string McpPath = "/mcp";

    /// <summary>Creates the access key (from configuration, generated, or none in Development).</summary>
    public static IServiceCollection AddMcpAccessKey(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddSingleton(McpAccessKey.Create(configuration[McpAccessKey.SettingName], environment.IsDevelopment()));
        return services;
    }

    /// <summary>Protects /mcp with the access key. Call after UseRouting and before the endpoints run.</summary>
    public static IApplicationBuilder UseMcpAccessKey(this IApplicationBuilder app)
    {
        var key = app.ApplicationServices.GetRequiredService<McpAccessKey>();
        app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("StockWiseMcp.Security")
            .LogInformation(
                "MCP endpoint access key: {State} (source: {Source}).",
                key.IsEnforced ? "required" : "NOT required",
                key.Source);

        return app.UseMiddleware<McpAccessKeyMiddleware>();
    }
}
