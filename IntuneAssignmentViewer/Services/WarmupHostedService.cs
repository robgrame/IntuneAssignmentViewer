using IntuneAssignmentViewer.Models;
using Microsoft.Extensions.Options;

namespace IntuneAssignmentViewer.Services;

/// <summary>
/// Optional background service that periodically warms the GraphResponseCache
/// by pre-fetching all catalog endpoints and per-policy assignments via $batch.
/// </summary>
/// <remarks>
/// When enabled (Warmup:Enabled = true), users almost never see a cold cache:
/// the first request after each refresh interval is served entirely from memory.
/// Recommended for cloud deployments. Defaults OFF for on-prem so admins can
/// opt in only after sizing the impact on their Graph quota.
/// </remarks>
public sealed class WarmupHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WarmupOptions _opts;
    private readonly ILogger<WarmupHostedService> _logger;

    public WarmupHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<WarmupOptions> opts,
        ILogger<WarmupHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Warmup service starting: initialDelay={Delay}s, interval={Interval}m",
            _opts.InitialDelaySeconds, _opts.IntervalMinutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_opts.InitialDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IIntuneService>();
                await service.WarmupAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Warmup cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _opts.IntervalMinutes)), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Warmup service stopped");
    }
}
