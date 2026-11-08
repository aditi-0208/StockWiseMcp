using System.Threading.RateLimiting;

namespace StockWiseMcp.Services.Chat;

/// <summary>
/// Caps how often AI mode (which costs money per call) can be used: a limit per client and an overall
/// limit for the whole server. When a limit is hit the chat falls back to Guided Demo instead of failing.
/// </summary>
public sealed class AiRequestLimiter : IDisposable
{
    public const int DefaultPerClientPerMinute = 10;
    public const int DefaultGlobalPerMinute = 60;

    private readonly PartitionedRateLimiter<string> _limiter;

    public AiRequestLimiter()
        : this(DefaultPerClientPerMinute, DefaultGlobalPerMinute)
    {
    }

    public AiRequestLimiter(int perClientPerMinute, int globalPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perClientPerMinute, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(globalPerMinute, 1);

        _limiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<string, string>(clientKey =>
                RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => Window(perClientPerMinute))),
            PartitionedRateLimiter.Create<string, string>(_ =>
                RateLimitPartition.GetFixedWindowLimiter("all-clients", _ => Window(globalPerMinute))));
    }

    /// <summary>Tries to use one AI request for the given client. Returns false if a limit is reached.</summary>
    public bool TryAcquire(string clientKey)
    {
        using var lease = _limiter.AttemptAcquire(clientKey);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    };
}
