namespace IntuneAssignmentViewer.Models;

public class CacheOptions
{
    /// <summary>TTL for catalog lists (deviceConfigurations, mobileApps, intents, ...).</summary>
    public int CatalogTtlMinutes { get; set; } = 10;

    /// <summary>TTL for per-policy /assignments responses.</summary>
    public int AssignmentsTtlMinutes { get; set; } = 10;

    /// <summary>TTL for resolved group display names.</summary>
    public int GroupNamesTtlMinutes { get; set; } = 30;

    /// <summary>TTL for cached null/error responses (404/403) — prevents re-hammering.</summary>
    public int NegativeTtlMinutes { get; set; } = 1;
}

public class PerformanceOptions
{
    /// <summary>If true, use Graph $batch to combine up to 20 GET requests in one HTTP call.</summary>
    public bool EnableBatchRequests { get; set; } = true;

    /// <summary>Max requests per $batch call (Graph hard limit is 20).</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Hard cap on concurrent in-flight Graph requests across all users/circuits.</summary>
    public int MaxConcurrentGraphRequests { get; set; } = 10;

    /// <summary>Milliseconds to wait between consecutive $batch calls — avoids hitting
    /// the /$batch endpoint's own per-minute throttle. Set to 0 to disable.</summary>
    public int BatchSpacingMs { get; set; } = 100;
}

public class WarmupOptions
{
    /// <summary>If true, prefetch catalog + assignments at app startup and periodically thereafter.</summary>
    /// <remarks>Recommended OFF for on-prem until you've sized the impact on your Graph quota.</remarks>
    public bool Enabled { get; set; } = false;

    /// <summary>Seconds to wait after app startup before the first warmup run.</summary>
    public int InitialDelaySeconds { get; set; } = 60;

    /// <summary>Minutes between warmup cycles. Should be ≤ Cache:AssignmentsTtlMinutes.</summary>
    public int IntervalMinutes { get; set; } = 10;
}
