namespace StockWiseMcp.Models;

/// <summary>Data for the friendly HTTP status-code page (for example 404 Not Found).</summary>
public sealed class StatusCodeViewModel
{
    public StatusCodeViewModel(int code)
    {
        Code = code;
        (Title, Message) = code switch
        {
            404 => ("Page not found",
                    "We couldn't find that page. It may have been moved, or the link may be incorrect."),
            403 => ("Access denied",
                    "You don't have permission to view that page."),
            >= 500 => ("Something went wrong",
                    "The app ran into a problem handling that request. Please try again."),
            _ => ("Something went wrong",
                    "That request couldn't be completed."),
        };
    }

    public int Code { get; }

    public string Title { get; }

    public string Message { get; }
}
