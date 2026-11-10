using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StockWiseMcp.Data;
using StockWiseMcp.Mcp;
using StockWiseMcp.Models;

namespace StockWiseMcp.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Chat()
    {
        return View();
    }

    public IActionResult Architecture()
    {
        return View();
    }

    /// <summary>Inventory overview. Uses the same cost-free models as the MCP tools.</summary>
    public async Task<IActionResult> Inventory(
        [FromServices] SqliteProductRepository repository,
        CancellationToken cancellationToken)
    {
        var summary = await repository.SummarizeAsync(cancellationToken);
        var lowStock = await repository.LowStockAsync(onlyOutOfStock: false, category: null, cancellationToken);
        var top = await repository.TopByValueAsync(5, cancellationToken);

        return View(new InventoryOverviewViewModel(
            InventorySummaryInfo.From(summary),
            lowStock
                .OrderBy(p => p.QuantityInStock == 0 ? 0 : 1)
                .ThenBy(p => p.QuantityInStock - p.ReorderLevel)
                .ThenBy(p => p.ProductName, StringComparer.OrdinalIgnoreCase)
                .Select(ProductInfo.From)
                .ToList(),
            top.Select(ProductInfo.From).ToList()));
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    /// <summary>Friendly page for 404 and other HTTP error status codes (re-executed, URL unchanged).</summary>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult StatusCode(int? code)
    {
        if (code.HasValue)
        {
            Response.StatusCode = code.Value;
        }

        return View(new StatusCodeViewModel(code ?? 500));
    }
}
