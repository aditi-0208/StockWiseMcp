using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using StockWiseMcp.Data;

namespace StockWiseMcp.Tests;

/// <summary>A throwaway content root with its own freshly seeded SQLite database.</summary>
public sealed class TempDatabase : IDisposable
{
    public TempDatabase()
    {
        ContentRoot = Path.Combine(Path.GetTempPath(), "StockWiseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRoot);
        DatabaseInitializer.InitializeAsync(DatabasePath.GetPath(ContentRoot)).GetAwaiter().GetResult();
    }

    public string ContentRoot { get; }

    public string DatabaseFile => DatabasePath.GetPath(ContentRoot);

    /// <summary>Runs an independent SQL query for an expected value (never through the code under test).</summary>
    public T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabaseFile};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    /// <summary>Changes this throwaway database (used to add boundary rows the seed data lacks).</summary>
    public void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public List<string> Column(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabaseFile};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetValue(0).ToString()!);
        }

        return values;
    }

    public (long Products, long Suppliers) RowCounts() =>
        (Scalar<long>("SELECT COUNT(*) FROM Products"), Scalar<long>("SELECT COUNT(*) FROM Suppliers"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(ContentRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Starts the real app on Kestrel (a real TCP port), because the chatbot calls the app's own /mcp
/// endpoint over HTTP. Each factory has its own database and settings.
/// </summary>
public sealed class StockWiseFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public StockWiseFactory(string environment = "Testing", Dictionary<string, string?>? settings = null)
    {
        Environment = environment;
        _settings = settings ?? new();
        Port = FreePort();
        UseKestrel(Port);
    }

    public TempDatabase Database { get; } = new();

    public string Environment { get; }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseContentRoot(Database.ContentRoot);

        // Make sure no developer key on this machine leaks into the tests.
        builder.UseSetting("OpenAI:ApiKey", "");
        builder.UseSetting("Gemini:ApiKey", "");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
    }

    public HttpClient CreateBrowserClient()
    {
        StartServer();
        return CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseUri, AllowAutoRedirect = false });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Database.Dispose();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public static class ChatApi
{
    public sealed record TokenResponse(string HeaderName, string Token);

    /// <summary>GET /api/chat/token (sets the antiforgery cookie on the client) and returns the token.</summary>
    public static async Task<TokenResponse> GetTokenAsync(HttpClient client)
    {
        var token = await client.GetFromJsonAsync<TokenResponse>("/api/chat/token", Json);
        return token!;
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, object body, string? token, string headerName = "X-CSRF-TOKEN")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Add(headerName, token);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Posts a question with a valid token and returns the parsed reply (asserts 200).</summary>
    public static async Task<JsonElement> AskAsync(HttpClient client, string message, string? mode = null)
    {
        var token = await GetTokenAsync(client);
        var response = await PostAsync(client, mode is null ? new { message } : new { message, mode }, token.Token, token.HeaderName);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 for '{message}', got {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
