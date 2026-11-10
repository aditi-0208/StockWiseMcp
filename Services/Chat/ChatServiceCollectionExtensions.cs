using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using StockWiseMcp.Mcp;

namespace StockWiseMcp.Services.Chat;

/// <summary>Whether AI mode is configured, and which model it uses. Never contains the API key.</summary>
public sealed record AiChatSettings(bool Enabled, string Model);

public static class ChatServiceCollectionExtensions
{
    /// <summary>Configuration key that overrides the MCP endpoint the chat services call.</summary>
    public const string McpEndpointSettingName = "StockWise:McpEndpoint";

    /// <summary>
    /// The OpenAI API key. NEVER put this in a file that is committed. Supply it as the environment
    /// variable <c>OpenAI__ApiKey</c> or with <c>dotnet user-secrets set "OpenAI:ApiKey" "..."</c>.
    /// </summary>
    public const string ApiKeySettingName = "OpenAI:ApiKey";

    public const string ModelSettingName = "OpenAI:Model";

    /// <summary>Optional base URL for an OpenAI-compatible server (for example a local test double).</summary>
    public const string EndpointSettingName = "OpenAI:Endpoint";

    public const string DefaultModel = "gpt-4o-mini";

    /// <summary>
    /// A Google AI Studio API key. When usable it takes priority over the OpenAI key. NEVER put this in a
    /// committed file. Supply it as <c>Gemini__ApiKey</c> or with <c>dotnet user-secrets set "Gemini:ApiKey" "..."</c>.
    /// </summary>
    public const string GeminiApiKeySettingName = "Gemini:ApiKey";

    public const string GeminiModelSettingName = "Gemini:Model";

    public const string DefaultGeminiModel = "gemini-3.6-flash";

    /// <summary>Google's OpenAI-compatible endpoint, which lets the OpenAI client talk to Gemini.</summary>
    public const string GeminiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

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

        services.AddScoped<GuidedDemoChatService>(provider =>
        {
            var configured = provider.GetRequiredService<IConfiguration>()[McpEndpointSettingName];
            var logger = provider.GetRequiredService<ILogger<GuidedDemoChatService>>();

            return new GuidedDemoChatService(
                ResolveEndpoint(provider, configured),
                logger,
                provider.GetService<McpAccessKey>()?.ClientKey);
        });

        services.AddScoped<IChatService>(provider => provider.GetRequiredService<GuidedDemoChatService>());

        return services;
    }

    /// <summary>
    /// Registers the chat mode selector and, only if a usable OpenAI API key is configured, the optional
    /// AI mode. Without a key nothing AI-related is created and Guided Demo keeps working unchanged.
    /// Call <see cref="AddGuidedDemoChat"/> as well.
    /// </summary>
    public static IServiceCollection AddAiChat(this IServiceCollection services, IConfiguration configuration)
    {
        var useGemini = IsUsableApiKey(configuration[GeminiApiKeySettingName]);
        var apiKey = configuration[useGemini ? GeminiApiKeySettingName : ApiKeySettingName];
        var configuredModel = configuration[useGemini ? GeminiModelSettingName : ModelSettingName];
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? (useGemini ? DefaultGeminiModel : DefaultModel)
            : configuredModel.Trim();
        var enabled = IsUsableApiKey(apiKey);

        services.AddSingleton(new AiChatSettings(enabled, model));
        services.AddSingleton<AiRequestLimiter>();
        services.AddScoped<ChatOrchestrator>();

        if (!enabled)
        {
            return services;
        }

        var clientOptions = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(30) };

        var endpoint = useGemini ? GeminiEndpoint : configuration[EndpointSettingName];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            clientOptions.Endpoint = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                                     && uri.Scheme is "http" or "https"
                ? uri
                : throw new InvalidOperationException($"'{EndpointSettingName}' must be an absolute http(s) URL.");
        }

        // The model can only reach inventory data through the tools passed with each request.
        IChatClient chatClient = new ChatClientBuilder(
                new OpenAIClient(new ApiKeyCredential(apiKey!.Trim()), clientOptions)
                    .GetChatClient(model)
                    .AsIChatClient())
            .UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = 5;
                client.IncludeDetailedErrors = true; // our tool errors are short, safe messages the model should see
            })
            .Build();

        services.AddSingleton(chatClient);

        services.AddScoped<AiChatService>(provider =>
        {
            var configured = provider.GetRequiredService<IConfiguration>()[McpEndpointSettingName];

            return new AiChatService(
                provider.GetRequiredService<IChatClient>(),
                ResolveEndpoint(provider, configured),
                provider.GetRequiredService<ILogger<AiChatService>>(),
                provider.GetService<McpAccessKey>()?.ClientKey);
        });

        return services;
    }

    /// <summary>
    /// A key is "usable" if it is present and does not look like a placeholder such as "your-key-here".
    /// This only guards against obvious mistakes; the key itself is validated by the AI provider.
    /// </summary>
    internal static bool IsUsableApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var trimmed = key.Trim();

        return trimmed.Length >= 16
               && !trimmed.Any(char.IsWhiteSpace)
               && !trimmed.StartsWith("your", StringComparison.OrdinalIgnoreCase)
               && !trimmed.StartsWith('<')
               && !trimmed.Contains("changeme", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Contains("xxxx", StringComparison.OrdinalIgnoreCase)
               && !trimmed.Contains("...", StringComparison.Ordinal);
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
