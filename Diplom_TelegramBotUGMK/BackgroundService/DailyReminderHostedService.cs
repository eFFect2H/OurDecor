using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Services;


public class DailyReminderHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailyReminderHostedService> _logger;

    public DailyReminderHostedService(IServiceProvider serviceProvider, ILogger<DailyReminderHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyReminderHostedService запущен");

        // Ждём ближайшее рабочее время
        await DelayToNextWorkingTime(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                if (IsWorkingTime(now))
                {
                    using var scope = _serviceProvider.CreateScope();
                    var reminderService = scope.ServiceProvider
                        .GetRequiredService<DailyReminderService>();

                    _logger.LogInformation("Запуск напоминаний о незакрытых заявках");
                    await reminderService.SendOpenApplicationsRemindersAsync();
                }
                else
                {
                    _logger.LogDebug("Вне рабочего времени — уведомления пропущены");
                }
            }
            catch (OperationCanceledException)
            {
                // корректное завершение
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в DailyReminderHostedService");
            }

            // Интервал проверки — 3 часа
            await Task.Delay(TimeSpan.FromHours(3), stoppingToken);
        }
    }

    private static bool IsWorkingTime(DateTime now)
        => now.Hour >= 8 && now.Hour < 20;

    private static Task DelayToNextWorkingTime(CancellationToken ct)
    {
        var now = DateTime.Now;
        DateTime next;

        if (now.Hour < 8)
            next = now.Date.AddHours(8);
        else if (now.Hour >= 20)
            next = now.Date.AddDays(1).AddHours(8);
        else
            return Task.CompletedTask;

        return Task.Delay(next - now, ct);
    }
}



