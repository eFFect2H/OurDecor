using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Services;

public class InvitationCleanupHostedService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly ILogger<InvitationCleanupHostedService> _logger;

    public InvitationCleanupHostedService(
        IServiceProvider provider,
        ILogger<InvitationCleanupHostedService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InvitationCleanupHostedService запущен");

        await DelayToNext3AM(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _provider.CreateScope();
                var invitationService = scope.ServiceProvider
                    .GetRequiredService<InvitationService>();

                _logger.LogInformation("Очистка истёкших приглашений");
                invitationService.CleanupExpiredInvitations();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка очистки приглашений");
            }

            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }

    private static Task DelayToNext3AM(CancellationToken ct)
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(3);
        if (now >= next)
            next = next.AddDays(1);

        return Task.Delay(next - now, ct);
    }
}

