using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StockWiseMcp.Tests;

/// <summary>Runs AI mode against a local stand-in for Google's native Gemini API.</summary>
public class GeminiModeTests
{
    private const string Signature = "c2lnbmF0dXJl";

    private static string ToolCallResponse(string tool, string args) =>
        $$$"""
        {"candidates":[{"content":{"role":"model","parts":[
          {"functionCall":{"name":"{{{tool}}}","args":{{{args}}}},"thoughtSignature":"{{{Signature}}}"}
        ]},"finishReason":"STOP"}]}
        """;

    private const string FinalResponse =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"Here is what you asked for."}]},"finishReason":"STOP"}]}
        """;

    [Fact]
    public async Task Gemini_ToolCall_SendsSignatureBack_AndAnswersInAiMode()
    {
        var (reply, _, followUp) = await AskAsync(
            "What are the top 5 products by inventory value?", "rank_products_by_value", """{"count":5}""");

        Assert.Equal("AI Assistant", reply.GetProperty("mode").GetString());
        Assert.Contains("rank_products_by_value", reply.GetProperty("toolsUsed").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("SW-004", reply.GetProperty("reply").GetString());
        Assert.Contains(Signature, followUp);
        Assert.Contains("functionResponse", followUp);
    }

    [Fact]
    public async Task Gemini_UnsupportedRequestAboutSku_ForcesLookup_AndStaysInAiMode()
    {
        var (reply, first, _) = await AskAsync(
            "i need to start return order for SW-001", "lookup_product", """{"sku":"SW-001"}""");

        Assert.Equal("AI Assistant", reply.GetProperty("mode").GetString());
        Assert.Contains("Creator Laptop 14", reply.GetProperty("reply").GetString());

        using var request = System.Text.Json.JsonDocument.Parse(first);
        var config = request.RootElement.GetProperty("toolConfig").GetProperty("functionCallingConfig");
        Assert.Equal("ANY", config.GetProperty("mode").GetString());
        Assert.Equal("lookup_product", config.GetProperty("allowedFunctionNames")[0].GetString());
    }

    private static async Task<(System.Text.Json.JsonElement Reply, string First, string FollowUp)> AskAsync(
        string question, string tool, string args)
    {
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var stub = Task.Run(() => ServeTwoRequestsAsync(server, ToolCallResponse(tool, args)));

        using var factory = new StockWiseFactory(settings: new()
        {
            ["Gemini:ApiKey"] = "AIza-test-0123456789abcdefghij",
            ["Gemini:Model"] = "gemini-test",
            ["Gemini:Endpoint"] = $"http://127.0.0.1:{port}/",
        });

        var reply = await ChatApi.AskAsync(factory.CreateBrowserClient(), question, mode: "ai");
        var (first, followUp) = await stub.WaitAsync(TimeSpan.FromSeconds(30));
        return (reply, first, followUp);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Answers with a tool call, then with text only if the signature came back, as strict models require.</summary>
    private static async Task<(string First, string FollowUp)> ServeTwoRequestsAsync(HttpListener server, string toolCall)
    {
        var firstContext = await server.GetContextAsync();
        var first = await ReadBodyAsync(firstContext);
        await RespondAsync(firstContext, toolCall, 200);

        var context = await server.GetContextAsync();
        var body = await ReadBodyAsync(context);

        await RespondAsync(
            context,
            body.Contains(Signature, StringComparison.Ordinal) ? FinalResponse : """{"error":{"code":400,"message":"missing thought_signature"}}""",
            body.Contains(Signature, StringComparison.Ordinal) ? 200 : 400);

        return (first, body);
    }

    private static async Task RespondAsync(HttpListenerContext context, string json, int status)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
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
