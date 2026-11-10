using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StockWiseMcp.Tests;

/// <summary>Runs AI mode against a local stand-in for Google's native Gemini API.</summary>
public class GeminiModeTests
{
    private const string Signature = "c2lnbmF0dXJl";

    private const string ToolCallResponse =
        $$$"""
        {"candidates":[{"content":{"role":"model","parts":[
          {"functionCall":{"name":"rank_products_by_value","args":{"count":5}},"thoughtSignature":"{{{Signature}}}"}
        ]},"finishReason":"STOP"}]}
        """;

    private const string FinalResponse =
        """
        {"candidates":[{"content":{"role":"model","parts":[{"text":"Here is what you asked for."}]},"finishReason":"STOP"}]}
        """;

    [Fact]
    public async Task Gemini_ToolCall_SendsSignatureBack_AndAnswersInAiMode()
    {
        var port = FreePort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var stub = Task.Run(() => ServeTwoRequestsAsync(server));

        using var factory = new StockWiseFactory(settings: new()
        {
            ["Gemini:ApiKey"] = "AIza-test-0123456789abcdefghij",
            ["Gemini:Model"] = "gemini-test",
            ["Gemini:Endpoint"] = $"http://127.0.0.1:{port}/",
        });

        var reply = await ChatApi.AskAsync(
            factory.CreateBrowserClient(), "What are the top 5 products by inventory value?", mode: "ai");

        Assert.Equal("AI Assistant", reply.GetProperty("mode").GetString());
        Assert.Contains("rank_products_by_value", reply.GetProperty("toolsUsed").EnumerateArray().Select(t => t.GetString()));
        Assert.Contains("SW-004", reply.GetProperty("reply").GetString());

        var followUp = await stub.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(Signature, followUp);
        Assert.Contains("functionResponse", followUp);
    }

    /// <summary>Answers with a tool call, then with text only if the signature came back, as strict models require.</summary>
    private static async Task<string> ServeTwoRequestsAsync(HttpListener server)
    {
        await RespondAsync(await server.GetContextAsync(), ToolCallResponse, 200);

        var context = await server.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        await RespondAsync(
            context,
            body.Contains(Signature, StringComparison.Ordinal) ? FinalResponse : """{"error":{"code":400,"message":"missing thought_signature"}}""",
            body.Contains(Signature, StringComparison.Ordinal) ? 200 : 400);

        return body;
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
