using Klassenbibliothek.Data;
using Klassenbibliothek.Services;
using Klassenbibliothek.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace TodoSuite.Server.Services;

/// <summary>
/// Hintergrunddienst: löscht täglich abgelaufene Papierkorb-Einträge (älter als 14 Tage) endgültig.
/// </summary>
public class TrashCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrashCleanupService> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public TrashCleanupService(IServiceProvider services, ILogger<TrashCleanupService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Kurze Startverzögerung
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var trashService = scope.ServiceProvider.GetRequiredService<ITodoTrashService>();
                var purged = await trashService.PurgeExpiredAsync(stoppingToken);

                if (purged > 0)
                    _logger.LogInformation("Papierkorb-Bereinigung: {Count} abgelaufene Einträge endgültig gelöscht.", purged);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fehler bei der Papierkorb-Bereinigung.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }
}
