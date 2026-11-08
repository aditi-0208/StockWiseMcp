namespace StockWiseMcp.Services.Chat;

public static class ChatServiceCollectionExtensions
{
    /// <summary>Configuration key that overrides the MCP endpoint the chat service calls.</summary>
    public const string McpEndpointSettingName = "StockWise:McpEndpoint";

    /// <summary>
    /// Registers the Guided Demo chat service. It calls this application's own /mcp endpoint.
    /// <para>
    /// By default the endpoint is built from the address the current request arrived on
    /// (loopback host + the server's actual local port), never from the client-supplied Host header,
    /// so a caller cannot make the server send requests to another machine.
    /// Set <c>StockWise:McpEndpoint</c> (for example "http://localhost:5247/mcp") to override it,
    /// which is useful if the server's HTTPS certificate is not trusted locally or after deployment.
    /// </para>
    /// </summary>
    public static IServiceCollection AddGuidedDemoChat(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<IChatService>(provider =>
        {
            var configured = provider.GetRequiredService<IConfiguration>()[McpEndpointSettingName];
            var logger = provider.GetRequiredService<ILogger<GuidedDemoChatService>>();

            return new GuidedDemoChatService(ResolveEndpoint(provider, configured), logger);
        });

        return services;
    }

    private static Uri ResolveEndpoint(IServiceProvider provider, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri)
                && configuredUri.Scheme is "http" or "https")
            {
                return configuredUri;
            }

            throw new InvalidOperationException(
                $"'{McpEndpointSettingName}' must be an absolute http(s) URL, but was '{configured}'.");
        }

        var context = provider.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException(
                $"No active HTTP request to infer the MCP endpoint from. Set '{McpEndpointSettingName}'.");

        return new UriBuilder(context.Request.Scheme, "localhost", context.Connection.LocalPort, "/mcp").Uri;
    }
}
