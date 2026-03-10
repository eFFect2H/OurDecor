using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Telegrame_Test.Models.Proxy;

namespace Telegrame_Test.Services
{
    /// <summary>
    /// Сервис для управления сетевой устойчивостью и прокси
    /// </summary>
    public interface INetworkResilienceService
    {
        HttpMessageHandler CreateMessageHandler(string clientName);
        Task<bool> TestConnectivityAsync(CancellationToken ct = default);
        Task<NetworkStats> GetNetworkStatsAsync();
        void RecordRequestMetrics(string clientName, long durationMs, bool success);
    }

    public class NetworkStats
    {
        public DateTime LastUpdate { get; set; }
        public int TotalRequests { get; set; }
        public int FailedRequests { get; set; }
        public double SuccessRate { get; set; }
        public long AverageDurationMs { get; set; }
        public bool IsNetworkHealthy { get; set; }
    }

    public class NetworkResilienceService : INetworkResilienceService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<NetworkResilienceService> _logger;
        private readonly ProxyConfiguration _proxyConfig;
        private readonly AdvancedResilienceConfig _resilienceConfig;
        private readonly Dictionary<string, NetworkStats> _stats = new();
        private readonly object _statsLock = new();

        public NetworkResilienceService(IConfiguration config, ILogger<NetworkResilienceService> logger)
        {
            _config = config;
            _logger = logger;
            _proxyConfig = config.GetSection("Network:Proxy").Get<ProxyConfiguration>() ?? new();
            _resilienceConfig = config.GetSection("Network:Resilience").Get<AdvancedResilienceConfig>() ?? new();
        }

        public HttpMessageHandler CreateMessageHandler(string clientName)
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = clientName == "TelegramPolling" ? 5 : 50,
                PooledConnectionLifetime = TimeSpan.FromMinutes(clientName == "TelegramPolling" ? 10 : 5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(_resilienceConfig.ConnectTimeoutSec),
                AutomaticDecompression = DecompressionMethods.All,
#if NET7_0_OR_GREATER
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                ResponseDrainTimeout = TimeSpan.FromSeconds(5),
#endif
            };

            // Настройка прокси
            if (_proxyConfig.Enabled && !string.IsNullOrEmpty(_proxyConfig.Host))
            {
                handler.Proxy = CreateWebProxy(_proxyConfig);
                handler.UseProxy = true;
                _logger.LogInformation(
                    "Прокси включен для {ClientName}: {ProxyType}://{Host}:{Port}",
                    clientName, _proxyConfig.Type, _proxyConfig.Host, _proxyConfig.Port);
            }

            // DNS кэширование для стабильности
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            return handler;
        }

        private WebProxy CreateWebProxy(ProxyConfiguration config)
        {
            var proxyUri = new Uri($"{config.Type}://{config.Host}:{config.Port}");
            var proxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
            {
                proxy.Credentials = new NetworkCredential(config.Username, config.Password);
            }

            return proxy;
        }

        /// <summary>
        /// Тестирование подключения к сети
        /// </summary>
        public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
        {
            try
            {
                using var httpClient = new HttpClient(CreateMessageHandler("Diagnostics"))
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };

                // Тест подключения к Google DNS и Telegram
                var tests = new[]
                {
                    ("Google DNS", "https://8.8.8.8"),
                    ("Telegram API", "https://api.telegram.org"),
                    ("OpenDNS", "https://208.67.222.123")
                };

                var results = new List<(string name, bool success, long ms)>();

                foreach (var (name, url) in tests)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        sw.Stop();
                        results.Add((name, response.IsSuccessStatusCode, sw.ElapsedMilliseconds));
                        _logger.LogInformation("Диагностика сети [{Name}]: ✅ {Url} ({DurationMs}ms)", name, url, sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        results.Add((name, false, sw.ElapsedMilliseconds));
                        _logger.LogWarning(ex, "Диагностика сети [{Name}]: ❌ {Url}", name, url);
                    }
                }

                var successCount = results.Count(r => r.success);
                var isHealthy = successCount >= 2;
                var avgLatency = results.Average(r => r.ms);

                _logger.LogInformation(
                    "Состояние сети: {Success}/{Total} успешно, средняя задержка: {AvgLatency}ms",
                    successCount, results.Count, avgLatency);

                return isHealthy;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при тестировании подключения");
                return false;
            }
        }

        public Task<NetworkStats> GetNetworkStatsAsync()
        {
            lock (_statsLock)
            {
                var combinedStats = new NetworkStats
                {
                    LastUpdate = DateTime.UtcNow,
                    TotalRequests = _stats.Values.Sum(s => s.TotalRequests),
                    FailedRequests = _stats.Values.Sum(s => s.FailedRequests),
                };

                if (combinedStats.TotalRequests > 0)
                {
                    combinedStats.SuccessRate = 100.0 * (combinedStats.TotalRequests - combinedStats.FailedRequests) / combinedStats.TotalRequests;
                    combinedStats.AverageDurationMs = (long)_stats.Values.Average(s => s.AverageDurationMs);
                }

                combinedStats.IsNetworkHealthy = combinedStats.SuccessRate > 90;

                return Task.FromResult(combinedStats);
            }
        }

        public void RecordRequestMetrics(string clientName, long durationMs, bool success)
        {
            lock (_statsLock)
            {
                if (!_stats.TryGetValue(clientName, out var stats))
                {
                    stats = new NetworkStats();
                    _stats[clientName] = stats;
                }

                stats.TotalRequests++;
                if (!success) stats.FailedRequests++;
                stats.AverageDurationMs = (stats.AverageDurationMs + durationMs) / 2;
                stats.LastUpdate = DateTime.UtcNow;
            }
        }
    }
}
