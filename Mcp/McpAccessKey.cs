using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Client;

namespace StockWiseMcp.Mcp;

/// <summary>
/// The shared secret that protects the /mcp endpoint.
/// <list type="bullet">
/// <item><b>Outside Development</b> a key is always required. It comes from the
/// <c>StockWise:McpAccessKey</c> setting (environment variable <c>StockWise__McpAccessKey</c>);
/// if none is set, a random one is generated when the app starts. The app's own chatbot reads it from
/// memory, so nothing needs configuring for the chatbot to keep working.</item>
/// <item><b>In Development</b> with no key set, /mcp is open so local MCP clients and demos need no setup.
/// Setting a key explicitly turns the protection on in Development too.</item>
/// </list>
/// The key must never be written to a file that is committed, logged, or returned in a response.
/// </summary>
public sealed class McpAccessKey
{
    /// <summary>Name of the HTTP header MCP clients send the key in. "Authorization: Bearer ..." also works.</summary>
    public const string HeaderName = "X-StockWise-Mcp-Key";

    /// <summary>Configuration key. Supply it as the environment variable <c>StockWise__McpAccessKey</c>.</summary>
    public const string SettingName = "StockWise:McpAccessKey";

    public const int MinimumConfiguredLength = 16;

    private readonly string _value;
    private readonly byte[] _hash;

    private McpAccessKey(bool isEnforced, string source, string value)
    {
        IsEnforced = isEnforced;
        Source = source;
        _value = value;
        _hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>True if requests to /mcp must carry the key.</summary>
    public bool IsEnforced { get; }

    /// <summary>Where the key came from, for the startup log: "configuration", "generated" or "none". Never the key.</summary>
    public string Source { get; }

    /// <summary>The key this app's own chat services send to /mcp, or null when no key is required.</summary>
    public string? ClientKey => IsEnforced ? _value : null;

    public static McpAccessKey Create(string? configured, bool isDevelopment)
    {
        var key = configured?.Trim();

        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length < MinimumConfiguredLength || key.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"'{SettingName}' must be at least {MinimumConfiguredLength} characters long and contain no spaces.");
            }

            return new McpAccessKey(isEnforced: true, source: "configuration", key);
        }

        if (isDevelopment)
        {
            return new McpAccessKey(isEnforced: false, source: "none", string.Empty);
        }

        // 32 random bytes => a 256-bit key. Only this process knows it, which is exactly what the
        // built-in chatbot needs; an outside MCP client must be given an explicit key instead.
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return new McpAccessKey(isEnforced: true, source: "generated", generated);
    }

    /// <summary>
    /// Checks a key presented by a caller. Both values are hashed first so the comparison takes the
    /// same time whatever the caller sent. Always true when no key is required.
    /// </summary>
    public bool IsValid(string? candidate)
    {
        if (!IsEnforced)
        {
            return true;
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
        return CryptographicOperations.FixedTimeEquals(candidateHash, _hash);
    }

    /// <summary>Adds the key (if one is required) to an MCP client's request headers.</summary>
    internal static HttpClientTransportOptions WithAccessKey(HttpClientTransportOptions options, string? key)
    {
        if (!string.IsNullOrEmpty(key))
        {
            options.AdditionalHeaders = new Dictionary<string, string> { [HeaderName] = key };
        }

        return options;
    }
}
