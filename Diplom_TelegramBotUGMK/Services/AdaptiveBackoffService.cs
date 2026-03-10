using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Telegrame_Test.Services
{
    /// <summary>
    /// Сервис для адаптивного backoff в зависимости от состояния сети
    /// </summary>
    public interface IAdaptiveBackoffService
    {
        Task<TimeSpan> GetBackoffDelayAsync(string key, int attemptNumber, Exception? lastException = null);
        void ResetBackoff(string key);
        Task<double> GetCurrentNetworkMultiplierAsync();
    }

    public class AdaptiveBackoffService : IAdaptiveBackoffService
    {
        private readonly INetworkResilienceService _networkService;
        private readonly ILogger<AdaptiveBackoffService> _logger;
        private readonly ConcurrentDictionary<string, BackoffState> _backoffStates = new();
        private readonly Random _random = new();

        public AdaptiveBackoffService(
            INetworkResilienceService networkService,
            ILogger<AdaptiveBackoffService> logger)
        {
            _networkService = networkService;
            _logger = logger;
        }

        public async Task<TimeSpan> GetBackoffDelayAsync(
            string key,
            int attemptNumber,
            Exception? lastException = null)
        {
            var state = _backoffStates.GetOrAdd(key, _ => new BackoffState());

            // Базовая задержка с экспоненциальным увеличением
            var baseDelayMs = Math.Min(
                100 * (int)Math.Pow(2, attemptNumber - 1),  // 100ms, 200ms, 400ms, 800ms...
                5000  // максимум 5 сек
            );

            // Добавляем jitter (20% вариативности)
            var jitterMs = (int)(baseDelayMs * 0.2 * _random.NextDouble());
            var delayWithJitterMs = baseDelayMs + jitterMs - baseDelayMs / 10;

            // Адаптивный множитель в зависимости от состояния сети
            var networkStats = await _networkService.GetNetworkStatsAsync();
            double multiplier = 1.0;

            if (networkStats.SuccessRate < 70)
            {
                // Плохая сеть - увеличиваем задержку на 50%
                multiplier = 1.5;
                _logger.LogWarning(
                    "Плохое состояние сети (успешность {SuccessRate}%). Увеличивается backoff для {Key}",
                    networkStats.SuccessRate, key);
            }
            else if (networkStats.SuccessRate < 85)
            {
                // Нормальная сеть - увеличиваем на 20%
                multiplier = 1.2;
            }

            // Специальная обработка для Rate Limit
            if (lastException?.Message.Contains("429") == true || lastException?.Message.Contains("rate") == true)
            {
                multiplier *= 2.0;  // Дважды больше для rate limit
                _logger.LogWarning("Rate limit обнаружен для {Key}. Задержка увеличена на 200%", key);
            }

            var finalDelayMs = (int)(delayWithJitterMs * multiplier);
            state.LastBackoffMs = finalDelayMs;
            state.LastBackoffTime = DateTime.UtcNow;

            _logger.LogDebug(
                "Backoff для {Key} (попытка {Attempt}): базовая={BaseMs}ms, с jitter={DelayMs}ms, итоговая={FinalMs}ms (множитель={Multiplier})",
                key, attemptNumber, baseDelayMs, delayWithJitterMs, finalDelayMs, multiplier);

            return TimeSpan.FromMilliseconds(finalDelayMs);
        }

        public void ResetBackoff(string key)
        {
            if (_backoffStates.TryRemove(key, out _))
            {
                _logger.LogDebug("Backoff сброшен для {Key}", key);
            }
        }

        public async Task<double> GetCurrentNetworkMultiplierAsync()
        {
            var stats = await _networkService.GetNetworkStatsAsync();
            return stats.SuccessRate < 70 ? 1.5 : (stats.SuccessRate < 85 ? 1.2 : 1.0);
        }

        private class BackoffState
        {
            public int LastBackoffMs { get; set; }
            public DateTime LastBackoffTime { get; set; }
        }
    }
}
