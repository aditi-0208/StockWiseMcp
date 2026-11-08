using StockWiseMcp.Data;
using StockWiseMcp.Mcp;
using StockWiseMcp.Services.Chat;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// SQLite inventory repository (stateless; opens a connection per query).
builder.Services.AddSingleton(
    new SqliteProductRepository(builder.Environment.ContentRootPath));

// CSRF protection for the chat API: the token is issued by GET /api/chat/token and must be
// sent back in this header on POST /api/chat.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "StockWise.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Guided Demo chatbot (no API key). Calls this app's own /mcp endpoint.
builder.Services.AddGuidedDemoChat();

// MCP server exposing the inventory tools over HTTP.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<InventoryTools>();

var app = builder.Build();

// Create the schema and seed sample data on first run (no-op if data already exists).
await DatabaseInitializer.InitializeAsync(
    DatabasePath.GetPath(app.Environment.ContentRootPath));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapMcp("/mcp");

app.Run();
