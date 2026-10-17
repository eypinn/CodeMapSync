using CodeMapSync.Options;
namespace CodeMapSync.Services;
public sealed class PeriodicSyncWorker(RepositorySyncService sync, SyncOptions options, ILogger<PeriodicSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A zero interval disables periodic sync; HTTP triggering still works.
        if (options.IntervalMinutes <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await sync.RunAsync(null, stoppingToken); }
            catch (InvalidOperationException ex) { logger.LogWarning(ex, "Sync already running"); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Periodic sync failed"); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
