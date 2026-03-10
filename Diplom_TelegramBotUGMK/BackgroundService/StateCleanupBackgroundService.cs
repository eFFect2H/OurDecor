using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Service;

namespace Telegrame_Test
{
    public class StateCleanupBackgroundService : BackgroundService
    {
        private readonly UserStateService _userStateService;
        private readonly ILogger<StateCleanupBackgroundService> _logger;
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6);  // Каждые 5 часов
        private readonly TimeSpan _expirationTime = TimeSpan.FromHours(3);  // Состояния старше 3 часов удаляем

        public StateCleanupBackgroundService(UserStateService userStateService, ILogger<StateCleanupBackgroundService> logger)
        {
            _userStateService = userStateService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("StateCleanupBackgroundService запущен");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Serilog.Log.Information("Запуск очистки старых состояний...");
                    await _userStateService.CleanupOldStates(_expirationTime);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Ошибка при очистке старых состояний");
                }

                await Task.Delay(_cleanupInterval, stoppingToken);
            }
        }
    }
}
