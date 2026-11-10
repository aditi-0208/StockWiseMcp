using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using StockWiseMcp.Mcp;

namespace StockWiseMcp.Services.Chat;

/// <summary>One configured AI provider the page can choose. API keys stay inside <paramref name="Client"/>.</summary>
public sealed record AiProvider(string Id, string Name, IChatClient Client);

/// <summary>The AI providers that have a usable key, in order of preference.</summary>
public sealed record AiChatSettings(IReadOnlyList<AiProvider> Providers);

/// <summary>Creates the chat service for the provider a message asked for.</summary>
public sealed class AiChatServiceFactory(Uri mcpEndpoint, ILogger<AiChatService> logger, string? mcpAccessKey)
{
    public AiChatService Create(AiProvider provider) =>
        new(provider.Client, mcpEndpoint, logger, provider.Name, mcpAccessKey);
}

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
    /// A Google AI Studio API key. NEVER put this in a
    /// committed file. Supply it as <c>Gemini__ApiKey</c> or with <c>dotnet user-secrets set "Gemini:ApiKey" "..."</c>.
    /// </summary>
    public const string GeminiApiKeySettingName = "Gemini:ApiKey";

    public const string GeminiModelSettingName = "Gemini:Model";

    public const string DefaultGeminiModel = "gemini-3.6-flash";

    /// <summary>Optional base URL for a Gemini-compatible server (for example a local test double).</summary>
    public const string GeminiEndpointSettingName = "Gemini:Endpoint";

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
        var providers = new List<AiProvider>();

        var geminiKey = configuration[GeminiApiKeySettingName];
        if (IsUsableApiKey(geminiKey))
        {
            var model = ModelOrDefault(configuration[GeminiModelSettingName], DefaultGeminiModel);
            providers.Add(new AiProvider(
                ChatModes.Gemini,
                "AI Assistant (Gemini)",
                WithTools(CreateGeminiClient(geminiKey!.Trim(), model, configuration[GeminiEndpointSettingName]))));
        }

        var openAiKey = configuration[ApiKeySettingName];
        if (IsUsableApiKey(openAiKey))
        {
            var model = ModelOrDefault(configuration[ModelSettingName], DefaultModel);
            providers.Add(new AiProvider(
                ChatModes.OpenAi,
                "AI Assistant (OpenAI)",
                WithTools(CreateOpenAiClient(openAiKey!.Trim(), model, configuration[EndpointSettingName]))));
        }

        services.AddSingleton(new AiChatSettings(providers));
        services.AddSingleton<AiRequestLimiter>();
        services.AddScoped<ChatOrchestrator>();

        if (providers.Count == 0)
        {
            return services;
        }

        services.AddScoped<AiChatServiceFactory>(provider =>
        {
            var configured = provider.GetRequiredService<IConfiguration>()[McpEndpointSettingName];

            return new AiChatServiceFactory(
                ResolveEndpoint(provider, configured),
                provider.GetRequiredService<ILogger<AiChatService>>(),
                provider.GetService<McpAccessKey>()?.ClientKey);
        });

        return services;
    }

    private static string ModelOrDefault(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();

    /// <summary>The model can only reach inventory data through the tools passed with each request.</summary>
    private static IChatClient WithTools(IChatClient providerClient) =>
        new ChatClientBuilder(providerClient)
            .UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = 5;
                client.IncludeDetailedErrors = true; // our tool errors are short, safe messages the model should see
            })
            .Build();

    /// <summary>Uses Google's native Gemini API, whose SDK carries tool-call data between requests.</summary>
    private static IChatClient CreateGeminiClient(string apiKey, string model, string? endpoint)
    {
        var httpOptions = string.IsNullOrWhiteSpace(endpoint)
            ? null
            : new Google.GenAI.Types.HttpOptions { BaseUrl = ParseHttpUrl(endpoint, GeminiEndpointSettingName).ToString() };

        return new Google.GenAI.Client(vertexAI: false, apiKey: apiKey, httpOptions: httpOptions).AsIChatClient(model);
    }

    private static IChatClient CreateOpenAiClient(string apiKey, string model, string? endpoint)
    {
        var options = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = ParseHttpUrl(endpoint, EndpointSettingName);
        }

        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
    }

    private static Uri ParseHttpUrl(string value, string settingName) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException($"'{settingName}' must be an absolute http(s) URL.");

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
